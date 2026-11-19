using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// The baseline every RAG tutorial starts with: slide a window of <c>ChunkSize</c> characters over the
/// text, stepping by <c>ChunkSize - ChunkOverlap</c>. It ignores words, sentences and headings entirely,
/// which is exactly why it is useful as a control group: every other strategy should beat it.
/// </summary>
public sealed class FixedSizeChunker : IChunker
{
    public string Name => "fixed";
    public string DisplayName => "Fixed-size";
    public string Description => "Equal character windows with overlap. Blind to words, sentences and headings.";

    public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options)
    {
        options.Validate();
        var text = document.Text;
        int step = options.ChunkSize - options.ChunkOverlap;

        var spans = new List<TextSpan>();
        for (int start = 0; start < text.Length; start += step)
        {
            int end = Math.Min(start + options.ChunkSize, text.Length);
            spans.Add(new TextSpan(start, end));

            // Stop once a window reaches the end; otherwise the overlap would produce a final
            // chunk that is just a duplicate of the previous window's tail.
            if (end == text.Length) break;
        }

        // trim: false keeps windows exactly ChunkSize long, so the configured overlap is exact.
        return ChunkBuilder.Build(document, Name, spans, trim: false);
    }
}
