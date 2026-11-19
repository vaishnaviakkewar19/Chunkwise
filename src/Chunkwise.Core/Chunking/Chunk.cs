namespace Chunkwise.Core.Chunking;

/// <summary>A contiguous slice of a document produced by an <see cref="IChunker"/>.</summary>
/// <param name="Id">Unique within a chunking run: <c>{document}#{strategy}#{index}</c>.</param>
/// <param name="Start">Inclusive start offset into <c>Document.Text</c>.</param>
/// <param name="End">Exclusive end offset into <c>Document.Text</c>.</param>
/// <param name="Text">Always equal to <c>Document.Text[Start..End]</c>; stored for convenience.</param>
/// <param name="SectionPath">Heading breadcrumb where the chunk starts, e.g. <c>Leave and Time Off &gt; Sick Leave</c>.</param>
public sealed record Chunk(
    string Id,
    string DocumentId,
    string Strategy,
    int Index,
    int Start,
    int End,
    string Text,
    string SectionPath)
{
    public int Length => End - Start;

    public TextSpan Span => new(Start, End);

    /// <summary>
    /// For parent-child chunking: the larger span that is handed to the LLM when this (small) chunk
    /// is retrieved. Null for every other strategy.
    /// </summary>
    public TextSpan? ParentSpan { get; init; }

    /// <summary>
    /// What a RAG pipeline would actually return for this chunk. Evaluation scores this span,
    /// because that is the text the answer generator would see.
    /// </summary>
    public TextSpan RetrievalSpan => ParentSpan ?? Span;
}
