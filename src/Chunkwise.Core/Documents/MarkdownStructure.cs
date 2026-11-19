using System.Text.RegularExpressions;

namespace Chunkwise.Core.Documents;

public sealed record Heading(int Level, string Text, TextSpan Line);

/// <summary>
/// A heading plus everything up to the next heading (of any level).
/// <see cref="Path"/> is the breadcrumb of level-2+ headings, e.g. <c>Medical Plans &gt; Copays</c>.
/// </summary>
public sealed record Section(TextSpan Span, string Path, Heading? Heading);

public enum BlockKind { CodeFence, Table }

/// <summary>A block that must never be cut in half by a structure-aware chunker.</summary>
public sealed record AtomicBlock(BlockKind Kind, TextSpan Span);

/// <summary>
/// A deliberately small, line-based Markdown scanner. It only recognises what chunking cares about:
/// ATX headings, fenced code blocks and pipe tables.
/// </summary>
/// <remarks>
/// Why not Markdig? Markdig is excellent, but we need just three constructs plus exact character
/// offsets, and ~100 lines that a reader can follow end-to-end teach more than a full CommonMark AST.
/// The scanner tracks fence state so that a <c>#</c> inside a code block is never mistaken for a heading.
/// </remarks>
public sealed partial class MarkdownStructure
{
    private readonly int[] _sectionStarts;

    private MarkdownStructure(IReadOnlyList<Heading> headings, IReadOnlyList<Section> sections, IReadOnlyList<AtomicBlock> blocks)
    {
        Headings = headings;
        Sections = sections;
        AtomicBlocks = blocks;
        _sectionStarts = sections.Select(s => s.Span.Start).ToArray();
    }

    public IReadOnlyList<Heading> Headings { get; }
    public IReadOnlyList<Section> Sections { get; }
    public IReadOnlyList<AtomicBlock> AtomicBlocks { get; }

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.+?)[ \t#]*$")]
    private static partial Regex HeadingRegex();

    public static MarkdownStructure Parse(string text)
    {
        var headings = new List<Heading>();
        var blocks = new List<AtomicBlock>();

        int? fenceStart = null;      // start offset of the currently open code fence
        string? fenceMarker = null;  // ``` or ~~~ (a fence must be closed by the same marker)
        int? tableStart = null;
        int tableEnd = 0;

        foreach (var line in EnumerateLines(text))
        {
            var content = text.AsSpan(line.Start, line.Length);
            var trimmed = content.TrimStart();

            if (fenceStart is not null)
            {
                if (trimmed.StartsWith(fenceMarker!))
                {
                    blocks.Add(new AtomicBlock(BlockKind.CodeFence, new TextSpan(fenceStart.Value, line.End)));
                    fenceStart = null;
                    fenceMarker = null;
                }
                continue;
            }

            bool isTableLine = trimmed.StartsWith("|");
            if (tableStart is not null && !isTableLine)
            {
                blocks.Add(new AtomicBlock(BlockKind.Table, new TextSpan(tableStart.Value, tableEnd)));
                tableStart = null;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                fenceStart = line.Start;
                fenceMarker = trimmed[..3].ToString();
                continue;
            }

            if (isTableLine)
            {
                tableStart ??= line.Start;
                tableEnd = line.End;
                continue;
            }

            var match = HeadingRegex().Match(content.ToString());
            if (match.Success)
                headings.Add(new Heading(match.Groups[1].Length, match.Groups[2].Value.Trim(), line));
        }

        // An unclosed fence or a table on the last line still counts as one block.
        if (fenceStart is not null)
            blocks.Add(new AtomicBlock(BlockKind.CodeFence, new TextSpan(fenceStart.Value, text.Length)));
        if (tableStart is not null)
            blocks.Add(new AtomicBlock(BlockKind.Table, new TextSpan(tableStart.Value, tableEnd)));

        return new MarkdownStructure(headings, BuildSections(text, headings), blocks);
    }

    private static List<Section> BuildSections(string text, List<Heading> headings)
    {
        var sections = new List<Section>();

        // Text before the first heading becomes an untitled section so that every offset has a section.
        int firstStart = headings.Count > 0 ? headings[0].Line.Start : text.Length;
        if (firstStart > 0)
            sections.Add(new Section(new TextSpan(0, firstStart), "", null));

        // The breadcrumb stack: when we meet a level-N heading we drop everything at level >= N.
        var stack = new List<Heading>();
        for (int i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            stack.RemoveAll(h => h.Level >= heading.Level);
            stack.Add(heading);

            int end = i + 1 < headings.Count ? headings[i + 1].Line.Start : text.Length;

            // The level-1 heading is the document title, which is already stored on Document.Title,
            // so we leave it out of the path to keep breadcrumbs short ("Dental", not "Benefits Guide > Dental").
            var path = string.Join(" > ", stack.Where(h => h.Level > 1).Select(h => h.Text));
            sections.Add(new Section(new TextSpan(heading.Line.Start, end), path, heading));
        }

        return sections;
    }

    /// <summary>The section that contains <paramref name="offset"/>.</summary>
    public Section SectionAt(int offset)
    {
        if (Sections.Count == 0)
            return new Section(new TextSpan(0, 0), "", null);

        int index = Array.BinarySearch(_sectionStarts, offset);
        if (index < 0) index = ~index - 1;   // insertion point - 1 = last section starting before offset
        return Sections[Math.Clamp(index, 0, Sections.Count - 1)];
    }

    public string SectionPathAt(int offset) => SectionAt(offset).Path;

    /// <summary>The atomic block that strictly contains <paramref name="offset"/>, if any.</summary>
    public AtomicBlock? AtomicBlockAt(int offset) =>
        AtomicBlocks.FirstOrDefault(b => b.Span.Start < offset && offset < b.Span.End);

    /// <summary>Yields each line as a span that excludes its trailing newline.</summary>
    internal static IEnumerable<TextSpan> EnumerateLines(string text)
    {
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            yield return new TextSpan(start, end);
            if (newline < 0) yield break;
            start = newline + 1;
        }
    }
}
