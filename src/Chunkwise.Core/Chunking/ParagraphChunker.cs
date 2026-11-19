using System.Text.RegularExpressions;
using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// One chunk per blank-line-separated paragraph. Paragraphs are the author's own unit of meaning, so
/// chunks are coherent, but headings become tiny chunks of their own and an answer that spans two
/// paragraphs is always split. Paragraphs longer than <c>ChunkSize</c> fall back to the recursive splitter.
/// </summary>
public sealed partial class ParagraphChunker : IChunker
{
    public string Name => "paragraph";
    public string DisplayName => "Paragraph";
    public string Description => "One chunk per paragraph (blank-line separated). Oversized paragraphs are split recursively.";

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLine();

    public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options)
    {
        options.Validate();
        var text = document.Text;
        var fallback = new RecursiveCharacterTextSplitter(options.ChunkSize, options.ChunkOverlap, options.Separators);

        var spans = new List<TextSpan>();
        foreach (var paragraph in Paragraphs(text))
        {
            if (paragraph.Length <= options.ChunkSize)
                spans.Add(paragraph);
            else
                spans.AddRange(fallback.Split(text, paragraph));
        }

        return ChunkBuilder.Build(document, Name, spans);
    }

    internal static IEnumerable<TextSpan> Paragraphs(string text)
    {
        int start = 0;
        foreach (Match match in BlankLine().Matches(text))
        {
            var paragraph = new TextSpan(start, match.Index).Trim(text);
            if (!paragraph.IsEmpty) yield return paragraph;
            start = match.Index + match.Length;
        }

        var last = new TextSpan(start, text.Length).Trim(text);
        if (!last.IsEmpty) yield return last;
    }
}
