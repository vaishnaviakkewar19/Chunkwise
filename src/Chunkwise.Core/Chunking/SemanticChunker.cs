using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// Splits where the <i>meaning</i> shifts rather than where a character count runs out: each sentence is
/// embedded (together with its neighbours) and the text is cut at the boundaries where consecutive
/// sentences are least similar. Chunks are also capped at <c>ChunkSize</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a relative threshold?</b> Cosine similarity between neighbouring sentences depends heavily on
/// the embedder: around 0.1-0.3 for the bag-of-words hashing embedder, 0.5-0.9 for nomic-embed-text.
/// A fixed absolute cut-off would split on every sentence with one and never with the other. So
/// <c>SemanticThreshold</c> is a <i>percentile</i>: 0.25 means "cut at boundaries whose similarity is in
/// this document's lowest 25%". It is the same idea as LangChain's percentile breakpoint mode.
/// </para>
/// <para>
/// <b>Why embed a window?</b> A single short sentence ("It is taxable.") has a noisy embedding. Embedding
/// each sentence with <c>windowSize</c> neighbours either side smooths that noise, as in Greg Kamradt's
/// original semantic-chunking write-up.
/// </para>
/// </remarks>
public sealed class SemanticChunker(int windowSize = 1) : IEmbeddingAwareChunker
{
    // Used by the synchronous Split(): the hashing embedder is pure CPU and completes synchronously,
    // so blocking on it cannot deadlock. Real models always go through SplitAsync.
    private static readonly HashingEmbedder OfflineEmbedder = new();

    public string Name => "semantic";
    public string DisplayName => "Semantic";
    public string Description => "Embeds every sentence and cuts where neighbouring sentences are least similar (bottom percentile), capped at the size limit.";

    public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options) =>
        SplitAsync(document, options, OfflineEmbedder).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<Chunk>> SplitAsync(Document document, ChunkerOptions options, IEmbedder embedder, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var text = document.Text;
        var units = SentenceUnits(text, options);
        if (units.Count <= 1)
            return ChunkBuilder.Build(document, Name, units);

        // 1. Embed each sentence with its neighbours.
        var windows = new List<string>(units.Count);
        for (int i = 0; i < units.Count; i++)
        {
            var first = units[Math.Max(0, i - windowSize)];
            var last = units[Math.Min(units.Count - 1, i + windowSize)];
            windows.Add(text[first.Start..last.End]);
        }
        var vectors = await embedder.EmbedAsync(windows, EmbeddingInput.Document, cancellationToken);

        // 2. Similarity of each boundary (between unit i and i+1).
        var similarities = new double[units.Count - 1];
        for (int i = 0; i < similarities.Length; i++)
            similarities[i] = VectorMath.Dot(vectors[i], vectors[i + 1]);

        // 3. Cut at low-similarity boundaries, or when the chunk would exceed the size limit.
        double cutoff = Percentile(similarities, options.SemanticThreshold);
        var spans = new List<TextSpan>();
        int groupStart = 0;
        for (int i = 0; i < similarities.Length; i++)
        {
            bool topicShift = similarities[i] < cutoff;
            bool tooBig = units[i + 1].End - units[groupStart].Start > options.ChunkSize;
            if (!topicShift && !tooBig) continue;

            spans.Add(new TextSpan(units[groupStart].Start, units[i].End));
            groupStart = i + 1;
        }
        spans.Add(new TextSpan(units[groupStart].Start, units[^1].End));

        return ChunkBuilder.Build(document, Name, spans);
    }

    /// <summary>
    /// The value below which a fraction <paramref name="p"/> of <paramref name="values"/> falls, used with a
    /// strict "&lt;" comparison. p = 0 never splits; p = 1 splits at every boundary that isn't the maximum.
    /// </summary>
    internal static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0 || p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[(int)Math.Floor(p * (sorted.Length - 1))];
    }

    /// <summary>Sentences, with any sentence longer than the limit pre-split so the size cap always holds.</summary>
    private static List<TextSpan> SentenceUnits(string text, ChunkerOptions options)
    {
        var fallback = new RecursiveCharacterTextSplitter(options.ChunkSize, 0, options.Separators);
        var units = new List<TextSpan>();
        foreach (var sentence in SentenceSplitter.Split(text, new TextSpan(0, text.Length)))
        {
            if (sentence.Length <= options.ChunkSize) units.Add(sentence);
            else units.AddRange(fallback.Split(text, sentence));
        }
        return units;
    }
}
