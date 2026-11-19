using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// Structure-aware chunking: every heading starts a new chunk, so a chunk never mixes two sections.
/// Sections that are too long are split recursively, but code fences and tables are atomic and are
/// never cut, even if that makes a chunk longer than <c>ChunkSize</c>.
/// </summary>
/// <remarks>
/// Why allow oversize chunks? Half a table loses its header row and half a code sample does not
/// compile. One slightly-too-big chunk that still makes sense retrieves better than two that don't.
/// </remarks>
public sealed class MarkdownHeaderChunker : IChunker
{
    public string Name => "markdown";
    public string DisplayName => "Markdown headers";
    public string Description => "One chunk per heading section, split recursively inside; never cuts code blocks or tables.";

    public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options)
    {
        options.Validate();
        var spans = new List<TextSpan>();
        foreach (var section in document.Structure.Sections)
            spans.AddRange(SplitSection(document, section.Span, options));

        return ChunkBuilder.Build(document, Name, spans);
    }

    /// <summary>Splits one section; also reused by the parent-child chunker to build parents.</summary>
    internal static List<TextSpan> SplitSection(Document document, TextSpan section, ChunkerOptions options)
    {
        var text = document.Text;
        section = section.Trim(text);
        if (section.IsEmpty) return [];
        if (section.Length <= options.ChunkSize) return [section];

        var splitter = new RecursiveCharacterTextSplitter(options.ChunkSize, options.ChunkOverlap, options.Separators);
        var pieces = new List<TextSpan>();

        // Walk the section, alternating between prose (split recursively) and atomic blocks (kept whole).
        int cursor = section.Start;
        foreach (var block in document.Structure.AtomicBlocks.Where(b => section.Contains(b.Span)))
        {
            if (block.Span.Start > cursor)
                pieces.AddRange(splitter.Split(text, new TextSpan(cursor, block.Span.Start)));
            pieces.Add(block.Span);
            cursor = block.Span.End;
        }
        if (cursor < section.End)
            pieces.AddRange(splitter.Split(text, new TextSpan(cursor, section.End)));

        return MergeNeighbours(pieces, options.ChunkSize);
    }

    /// <summary>
    /// Glues adjacent pieces back together while they fit. This keeps the sentence that introduces a
    /// code sample ("A successful request returns HTTP 201 Created:") in the same chunk as the sample.
    /// </summary>
    private static List<TextSpan> MergeNeighbours(List<TextSpan> pieces, int maxSize)
    {
        var merged = new List<TextSpan>();
        foreach (var piece in pieces.Where(p => !p.IsEmpty))
        {
            if (merged.Count > 0 && piece.End - merged[^1].Start <= maxSize)
                merged[^1] = new TextSpan(merged[^1].Start, Math.Max(merged[^1].End, piece.End));
            else
                merged.Add(piece);
        }
        return merged;
    }
}
