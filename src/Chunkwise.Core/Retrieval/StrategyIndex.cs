using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;

namespace Chunkwise.Core.Retrieval;

/// <summary>
/// Everything needed to answer questions with one chunking strategy: the chunks of every document,
/// embedded and loaded into a vector index. This is the "indexing" half of a RAG pipeline.
/// </summary>
public sealed class StrategyIndex
{
    private readonly InMemoryVectorIndex _index;
    private readonly IEmbedder _embedder;

    private StrategyIndex(IChunker chunker, ChunkerOptions options, bool enriched, IReadOnlyList<Chunk> chunks, InMemoryVectorIndex index, IEmbedder embedder)
    {
        Chunker = chunker;
        Options = options;
        Enriched = enriched;
        Chunks = chunks;
        _index = index;
        _embedder = embedder;
    }

    public IChunker Chunker { get; }
    public ChunkerOptions Options { get; }
    public bool Enriched { get; }
    public IReadOnlyList<Chunk> Chunks { get; }

    public static async Task<StrategyIndex> BuildAsync(
        IChunker chunker,
        IReadOnlyList<Document> documents,
        ChunkerOptions options,
        bool enrich,
        IEmbedder embedder,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<Chunk>();
        var texts = new List<string>();
        foreach (var document in documents)
        {
            foreach (var chunk in await chunker.SplitAsync(document, options, embedder, cancellationToken))
            {
                chunks.Add(chunk);
                texts.Add(ContextualEnrichment.TextToEmbed(document, chunk, enrich));
            }
        }

        // One batched call for the whole corpus: far fewer round-trips for remote embedders.
        var vectors = await embedder.EmbedAsync(texts, EmbeddingInput.Document, cancellationToken);
        var index = new InMemoryVectorIndex();
        for (int i = 0; i < chunks.Count; i++)
            index.Add(chunks[i], vectors[i]);

        return new StrategyIndex(chunker, options, enrich, chunks, index, embedder);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string question, int k, CancellationToken cancellationToken = default) =>
        Search(await _embedder.EmbedOneAsync(question, EmbeddingInput.Query, cancellationToken), k);

    /// <summary>
    /// Top <paramref name="k"/> results, with duplicates removed by <see cref="Chunk.RetrievalSpan"/>.
    /// </summary>
    /// <remarks>
    /// With parent-child chunking several children often share one parent. Returning that parent three
    /// times would waste two of the three result slots, so (like LangChain's ParentDocumentRetriever)
    /// we keep only the best-scoring child per parent.
    /// </remarks>
    public IReadOnlyList<SearchHit> Search(float[] queryVector, int k)
    {
        // Over-fetch so that de-duplication still leaves k results.
        var candidates = _index.Search(queryVector, Math.Min(_index.Count, k * 8));
        var seen = new HashSet<(string, TextSpan)>();
        var results = new List<SearchHit>(k);
        foreach (var hit in candidates)
        {
            if (!seen.Add((hit.Chunk.DocumentId, hit.Chunk.RetrievalSpan))) continue;
            results.Add(hit with { Rank = results.Count + 1 });
            if (results.Count == k) break;
        }
        return results;
    }
}
