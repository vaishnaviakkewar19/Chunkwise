namespace Chunkwise.Core.Documents;

/// <summary>A source document in the knowledge base.</summary>
/// <param name="Id">Stable identifier; we use the file name (e.g. <c>benefits-guide.md</c>).</param>
/// <param name="Title">Human-readable title, taken from the first level-1 heading.</param>
/// <param name="Text">Full text with line endings normalised to <c>\n</c>.</param>
public sealed class Document(string id, string title, string text)
{
    private MarkdownStructure? _structure;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Text { get; } = text;

    /// <summary>
    /// Headings, sections, code fences and tables. Parsed lazily and cached because several chunkers
    /// (and the section-path lookup for every chunk) need it.
    /// </summary>
    public MarkdownStructure Structure => _structure ??= MarkdownStructure.Parse(Text);

    public override string ToString() => Id;
}
