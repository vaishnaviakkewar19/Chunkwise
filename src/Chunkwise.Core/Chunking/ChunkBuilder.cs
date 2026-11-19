using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// Turns raw spans into <see cref="Chunk"/> records. Centralising this means every strategy gets the
/// same guarantees for free: no empty chunks, ordered output, consistent IDs and section paths.
/// </summary>
public static class ChunkBuilder
{
    /// <param name="trim">
    /// Strip leading/trailing whitespace from each span. Fixed-size chunking passes <c>false</c> so that
    /// its windows (and therefore its overlaps) stay exactly the configured size.
    /// </param>
    public static List<Chunk> Build(
        Document document,
        string strategy,
        IEnumerable<TextSpan> spans,
        bool trim = true,
        Func<TextSpan, TextSpan?>? parentOf = null)
    {
        var chunks = new List<Chunk>();
        foreach (var raw in spans.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            var span = trim ? raw.Trim(document.Text) : raw;

            // A chunk that is empty or pure whitespace would embed to a meaningless vector and
            // pollute search results, so it is dropped here rather than in every chunker.
            if (span.IsEmpty || string.IsNullOrWhiteSpace(document.Text.AsSpan(span.Start, span.Length).ToString()))
                continue;

            // Section path is taken from the first non-whitespace character, so a fixed-size window
            // that begins with "\n## Dental" is labelled Dental rather than the previous section.
            int firstVisible = span.Trim(document.Text).Start;
            int index = chunks.Count;
            chunks.Add(new Chunk(
                Id: $"{document.Id}#{strategy}#{index}",
                DocumentId: document.Id,
                Strategy: strategy,
                Index: index,
                Start: span.Start,
                End: span.End,
                Text: span.Slice(document.Text),
                SectionPath: document.Structure.SectionPathAt(firstVisible))
            {
                ParentSpan = parentOf?.Invoke(span),
            });
        }

        return chunks;
    }
}
