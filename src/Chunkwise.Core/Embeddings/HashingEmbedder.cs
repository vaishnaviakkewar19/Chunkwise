using System.Text;

namespace Chunkwise.Core.Embeddings;

/// <summary>
/// An offline, zero-setup "embedding": a hashed bag of words and word pairs. It needs no model download
/// and no network, so the demo always runs, and it is fully deterministic, so tests can depend on it.
/// </summary>
/// <remarks>
/// <para>
/// How it works (the "hashing trick"): each token and each adjacent token pair is hashed to one of
/// <see cref="Dimensions"/> buckets, with a second hash bit choosing +1 or -1 so collisions tend to cancel
/// rather than pile up. Term frequency is damped with log(1 + tf), and the vector is L2-normalised.
/// </para>
/// <para>
/// What it cannot do: it has no idea that "shut out" means "locked" or that "boss" relates to "names".
/// It only matches shared words. That limitation is deliberate and useful: the golden questions tagged
/// <c>no-keyword-overlap</c> show exactly where a real semantic model (see <see cref="OllamaEmbedder"/>) earns its keep.
/// </para>
/// </remarks>
public sealed class HashingEmbedder(int dimensions = 1024) : IEmbedder
{
    // A short English stop-word list. Without it, "the", "of" and "a" would dominate every vector and
    // make all chunks look alike; real models learn this weighting, we have to hand-code it.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "about", "after", "all", "also", "an", "and", "any", "are", "as", "at", "be", "been", "before",
        "but", "by", "can", "do", "does", "for", "from", "get", "has", "have", "how", "i", "if", "in", "into",
        "is", "it", "its", "may", "me", "must", "my", "no", "not", "of", "on", "only", "or", "our", "should",
        "so", "than", "that", "the", "their", "them", "then", "there", "these", "they", "this", "to", "up",
        "us", "was", "we", "were", "what", "when", "where", "which", "who", "will", "with", "you", "your",
    };

    public int Dimensions { get; } = dimensions > 0 ? dimensions : throw new ArgumentOutOfRangeException(nameof(dimensions));

    public string ModelId => $"hashing-bow-{Dimensions}";

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInput input, CancellationToken cancellationToken = default) =>
        Task.FromResult(texts.Select(Embed).ToArray());

    public float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var tokens = Tokenize(text).ToList();
        foreach (var token in tokens)
            counts[token] = counts.GetValueOrDefault(token) + 1;

        // Bigrams add a little word order ("sick leave" vs "leave sick") and phrase matching.
        for (int i = 1; i < tokens.Count; i++)
        {
            var bigram = tokens[i - 1] + " " + tokens[i];
            counts[bigram] = counts.GetValueOrDefault(bigram) + 1;
        }

        foreach (var (feature, count) in counts)
        {
            uint hash = Fnv1a(feature);
            int bucket = (int)(hash % (uint)Dimensions);
            float sign = (hash & 0x8000_0000) == 0 ? 1f : -1f;
            float weight = (float)Math.Log(1 + count);
            // Bigrams count half as much as single words: they are rarer and noisier.
            if (feature.Contains(' ')) weight *= 0.5f;
            vector[bucket] += sign * weight;
        }

        return VectorMath.Normalize(vector);
    }

    /// <summary>
    /// Lower-cases, splits on anything that is not a letter, digit or <c>$</c>, splits camelCase, drops
    /// stop words, and stems.
    /// </summary>
    /// <remarks>
    /// Splitting camelCase (<c>IsValidSignature</c> to <c>is valid signature</c>) is what code-search
    /// tools do. Without it, an identifier in a C# sample never matches the plain-English question about it.
    /// </remarks>
    public static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();
        char previous = ' ';
        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw);
            bool camelBoundary = char.IsUpper(raw) && char.IsLower(previous);
            previous = raw;

            if (camelBoundary && current.Length > 0)
            {
                if (Normalise(current.ToString()) is { } part) yield return part;
                current.Clear();
            }

            if (char.IsLetterOrDigit(c) || c == '$')
            {
                current.Append(c);
                continue;
            }

            if (current.Length > 0)
            {
                if (Normalise(current.ToString()) is { } token) yield return token;
                current.Clear();
            }
        }

        if (current.Length > 0 && Normalise(current.ToString()) is { } last)
            yield return last;
    }

    private static string? Normalise(string token)
    {
        if (StopWords.Contains(token)) return null;
        return Stem(token);
    }

    /// <summary>
    /// A deliberately tiny suffix stripper (a real system would use Porter or Snowball). It folds the
    /// plural and verb forms that matter most for matching questions to answers: "months" and "monthly"
    /// to "month"; "expire", "expires" and "expired" to "expir"; "leave" and "leaves" to "leav".
    /// </summary>
    public static string Stem(string token)
    {
        if (token.Length <= 3 || token.Any(char.IsDigit)) return token;

        foreach (var suffix in (string[])["ingly", "edly", "ing", "ly", "ed", "s"])
        {
            if (!token.EndsWith(suffix, StringComparison.Ordinal) || token.Length - suffix.Length < 3)
                continue;

            // "ss" endings (access, business) are not plurals.
            if (suffix == "s" && token.EndsWith("ss", StringComparison.Ordinal))
                break;

            token = token[..^suffix.Length];
            break;
        }

        // Dropping a final "e" makes "leave"/"leaves" and "expire"/"expired" meet at the same stem.
        return token.Length > 3 && token[^1] == 'e' ? token[..^1] : token;
    }

    /// <summary>
    /// FNV-1a: a fast, well-distributed, <b>stable</b> hash. We cannot use <c>string.GetHashCode()</c>
    /// because .NET randomises it per process, so vectors (and the cache) would differ on every run.
    /// </summary>
    private static uint Fnv1a(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}
