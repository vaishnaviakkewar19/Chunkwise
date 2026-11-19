using Chunkwise.Core.Chunking;
using Chunkwise.Core.Embeddings;

namespace Chunkwise.Core.Retrieval;

public sealed record SearchHit(Chunk Chunk, float Score, int Rank);

/// <summary>
/// Brute-force cosine-similarity search over vectors held in memory.
/// </summary>
/// <remarks>
/// Exact search compares the query with every vector: O(n). For this corpus (a few hundred chunks per
/// strategy) that takes well under a millisecond, and it has no approximation error to muddy the
/// comparison between chunking strategies. Past ~100k vectors you would switch to an ANN index
/// (HNSW in pgvector, Qdrant, Azure AI Search...); the roadmap in the README covers this.
/// </remarks>
public sealed class InMemoryVectorIndex
{
    private readonly List<(Chunk Chunk, float[] Vector)> _entries = [];

    public int Count => _entries.Count;

    public void Add(Chunk chunk, float[] vector) =>
        _entries.Add((chunk, VectorMath.Normalize((float[])vector.Clone())));

    /// <summary>Top <paramref name="k"/> chunks by cosine similarity, best first.</summary>
    public IReadOnlyList<SearchHit> Search(float[] query, int k)
    {
        var normalised = VectorMath.Normalize((float[])query.Clone());
        return _entries
            .Select(e => (e.Chunk, Score: VectorMath.Dot(normalised, e.Vector)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.Id, StringComparer.Ordinal)   // deterministic tie-break, so runs are reproducible
            .Take(k)
            .Select((x, i) => new SearchHit(x.Chunk, x.Score, i + 1))
            .ToList();
    }
}
