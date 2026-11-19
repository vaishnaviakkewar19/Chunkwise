using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Chunkwise.Core.Embeddings;

/// <summary>
/// Decorator that stores embeddings on disk, keyed by SHA-256 of (model, input kind, text).
/// </summary>
/// <remarks>
/// <para>
/// Evaluating 7 strategies x 2 enrichment settings x several chunk sizes re-embeds a lot of identical
/// text. With a local model that is minutes per run; with a hosted model it is money. Content-addressed
/// caching makes re-runs near-instant and is safe: if a chunk's text changes, its key changes too.
/// </para>
/// <para>
/// Format: one append-only text file per model, one <c>key base64(float32[])</c> line per vector.
/// Human-inspectable, trivially mergeable, and safe to delete at any time.
/// </para>
/// </remarks>
public sealed class CachingEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly string _filePath;
    private readonly Dictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public CachingEmbedder(IEmbedder inner, string cacheDirectory)
    {
        _inner = inner;
        Directory.CreateDirectory(cacheDirectory);
        var safeName = string.Concat(inner.ModelId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
        _filePath = Path.Combine(cacheDirectory, $"{safeName}.embeddings.txt");
    }

    public string ModelId => _inner.ModelId;

    /// <summary>Number of vectors fetched from the inner embedder since construction (for tests and logs).</summary>
    public int Misses { get; private set; }

    public string FilePath => _filePath;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInput input, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_loaded) Load();

            var keys = texts.Select(t => Key(input, t)).ToArray();
            var missing = Enumerable.Range(0, texts.Count)
                .Where(i => !_cache.ContainsKey(keys[i]))
                .GroupBy(i => keys[i])          // embed duplicate texts only once
                .Select(g => g.First())
                .ToList();

            if (missing.Count > 0)
            {
                var vectors = await _inner.EmbedAsync(missing.Select(i => texts[i]).ToList(), input, cancellationToken);
                var lines = new StringBuilder();
                for (int j = 0; j < missing.Count; j++)
                {
                    var key = keys[missing[j]];
                    _cache[key] = vectors[j];
                    lines.Append(key).Append(' ').Append(Convert.ToBase64String(MemoryMarshal.AsBytes(vectors[j].AsSpan()))).Append('\n');
                }
                await File.AppendAllTextAsync(_filePath, lines.ToString(), cancellationToken);
                Misses += missing.Count;
            }

            // Return copies so callers can't mutate cached vectors by accident.
            return keys.Select(k => (float[])_cache[k].Clone()).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    private string Key(EmbeddingInput input, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{_inner.ModelId}\n{input}\n{text}"));
        return Convert.ToHexString(bytes);
    }

    private void Load()
    {
        _loaded = true;
        if (!File.Exists(_filePath)) return;

        foreach (var line in File.ReadLines(_filePath))
        {
            int space = line.IndexOf(' ');
            if (space <= 0) continue;   // tolerate a torn last line from an interrupted run
            try
            {
                var bytes = Convert.FromBase64String(line[(space + 1)..]);
                _cache[line[..space]] = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            }
            catch (FormatException)
            {
                // Corrupt entry: ignore it; it will simply be re-embedded.
            }
        }
    }
}
