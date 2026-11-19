using System.Collections.Concurrent;
using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;
using Chunkwise.Core.Evaluation;
using Chunkwise.Core.Retrieval;

namespace Chunkwise.Web;

/// <summary>
/// Holds the corpus and caches built indexes and leaderboards. Registered as a singleton, because the
/// corpus never changes while the app runs and rebuilding indexes on every slider move would waste
/// work (and, with Ollama, seconds).
/// </summary>
public sealed class ChunkwiseService(
    IReadOnlyList<Document> documents,
    IReadOnlyList<GoldenQuestion> questions,
    ChunkerRegistry registry,
    IEmbedder embedder,
    string? embedderWarning)
{
    // The sliders can produce hundreds of (size, overlap) combinations. A crude cap keeps memory bounded;
    // with the hashing embedder an index rebuilds in milliseconds, so evicting everything is cheap.
    private const int MaxCachedIndexes = 300;

    private readonly ConcurrentDictionary<IndexKey, Lazy<Task<StrategyIndex>>> _indexes = new();
    private readonly ConcurrentDictionary<(int, int), Lazy<Task<IReadOnlyList<StrategyReport>>>> _leaderboards = new();
    private readonly Evaluator _evaluator = new(embedder);
    private readonly SemaphoreSlim _evaluatorLock = new(1, 1);

    private readonly record struct IndexKey(string Strategy, int Size, int Overlap, bool Enrich);

    public IReadOnlyList<Document> Documents => documents;
    public IReadOnlyList<GoldenQuestion> Questions => questions;
    public ChunkerRegistry Registry => registry;
    public IEmbedder Embedder => embedder;

    /// <summary>Set when Ollama was requested but unreachable and we fell back to hashing.</summary>
    public string? EmbedderWarning => embedderWarning;

    public Document GetDocument(string id) =>
        documents.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown document '{id}'.");

    public Task<IReadOnlyList<Chunk>> ChunkAsync(string documentId, string strategy, ChunkerOptions options, CancellationToken ct) =>
        registry.Get(strategy).SplitAsync(GetDocument(documentId), options, embedder, ct);

    public Task<StrategyIndex> GetIndexAsync(IChunker chunker, ChunkerOptions options, bool enrich)
    {
        if (_indexes.Count > MaxCachedIndexes) _indexes.Clear();

        var key = new IndexKey(chunker.Name, options.ChunkSize, options.ChunkOverlap, enrich);
        return GetOrAddSafely(_indexes, key, () => StrategyIndex.BuildAsync(chunker, documents, options, enrich, embedder));
    }

    public Task<IReadOnlyList<StrategyReport>> GetLeaderboardAsync(ChunkerOptions options)
    {
        if (_leaderboards.Count > 50) _leaderboards.Clear();

        return GetOrAddSafely(_leaderboards, (options.ChunkSize, options.ChunkOverlap), async () =>
        {
            var reports = new List<StrategyReport>();
            foreach (var chunker in registry.All)
            {
                foreach (var enrich in new[] { false, true })
                {
                    var index = await GetIndexAsync(chunker, options, enrich);
                    // The evaluator caches question vectors in a plain dictionary, so serialise access.
                    await _evaluatorLock.WaitAsync();
                    try { reports.Add(await _evaluator.EvaluateAsync(index, questions)); }
                    finally { _evaluatorLock.Release(); }
                }
            }
            return (IReadOnlyList<StrategyReport>)reports;
        });
    }

    /// <summary>
    /// <c>Lazy&lt;Task&gt;</c> guarantees one build per key even when the UI fires several requests at
    /// once. A failed build is evicted, so a transient error (Ollama restarting) is not cached forever.
    /// </summary>
    private static async Task<T> GetOrAddSafely<TKey, T>(ConcurrentDictionary<TKey, Lazy<Task<T>>> cache, TKey key, Func<Task<T>> factory)
        where TKey : notnull
    {
        var lazy = cache.GetOrAdd(key, _ => new Lazy<Task<T>>(factory));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            cache.TryRemove(key, out _);
            throw;
        }
    }
}
