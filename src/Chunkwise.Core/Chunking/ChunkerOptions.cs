namespace Chunkwise.Core.Chunking;

/// <summary>
/// Knobs shared by all chunkers. Sizes are measured in <b>characters</b>, not tokens: it keeps the
/// library dependency-free and deterministic, and for English prose ~4 characters is roughly one token.
/// Each chunker documents which options it honours.
/// </summary>
public sealed record ChunkerOptions
{
    public static ChunkerOptions Default { get; } = new();

    /// <summary>Target maximum chunk length in characters.</summary>
    public int ChunkSize { get; init; } = 500;

    /// <summary>Characters shared between neighbouring chunks (fixed, recursive, sentence).</summary>
    public int ChunkOverlap { get; init; } = 50;

    /// <summary>Separators for the recursive splitter, tried in order. Null = LangChain's defaults.</summary>
    public IReadOnlyList<string>? Separators { get; init; }

    /// <summary>
    /// Semantic chunker: split at boundaries whose neighbouring-sentence similarity is in the lowest
    /// fraction (0-1) of the document. A percentile, not an absolute cosine value; see SemanticChunker.
    /// </summary>
    public double SemanticThreshold { get; init; } = 0.25;

    /// <summary>Parent-child chunker: maximum parent size. Children use <see cref="ChunkSize"/>.</summary>
    public int ParentChunkSize { get; init; } = 1500;

    public void Validate()
    {
        if (ChunkSize < 20)
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), ChunkSize, "Chunk size must be at least 20 characters.");
        if (ChunkOverlap < 0 || ChunkOverlap >= ChunkSize)
            throw new ArgumentOutOfRangeException(nameof(ChunkOverlap), ChunkOverlap, "Overlap must be >= 0 and smaller than the chunk size.");
        if (SemanticThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(SemanticThreshold), SemanticThreshold, "Semantic threshold is a percentile between 0 and 1.");
        if (ParentChunkSize < 20)
            throw new ArgumentOutOfRangeException(nameof(ParentChunkSize), ParentChunkSize, "Parent chunk size must be at least 20 characters.");
    }
}
