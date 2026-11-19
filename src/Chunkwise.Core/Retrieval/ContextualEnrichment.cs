using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Retrieval;

/// <summary>
/// Builds the text that is <i>embedded</i> for a chunk. With enrichment on, the chunk is prefixed with
/// its document title and heading breadcrumb, e.g.
/// <c>Brightwell Health Employee Handbook &gt; Working Hours and Location &gt; Remote Work Stipend: This policy provides...</c>
/// </summary>
/// <remarks>
/// A chunk that says "This policy provides $75 per month" has lost the words "remote work stipend",
/// which live in a heading the chunk no longer contains. Prepending the breadcrumb puts them back,
/// for the cost of a few tokens per chunk. The idea is the same as Anthropic's "contextual retrieval",
/// minus the LLM call: we use the structure the author already wrote.
/// Only the embedded text changes; the chunk's offsets and displayed text stay the same.
/// </remarks>
public static class ContextualEnrichment
{
    public static string Prefix(Document document, Chunk chunk) =>
        string.IsNullOrEmpty(chunk.SectionPath)
            ? $"{document.Title}: "
            : $"{document.Title} > {chunk.SectionPath}: ";

    public static string TextToEmbed(Document document, Chunk chunk, bool enrich) =>
        enrich ? Prefix(document, chunk) + chunk.Text : chunk.Text;
}
