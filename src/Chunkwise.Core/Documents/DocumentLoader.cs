namespace Chunkwise.Core.Documents;

public static class DocumentLoader
{
    /// <summary>Loads every <c>*.md</c> file in <paramref name="directory"/>, ordered by file name.</summary>
    public static IReadOnlyList<Document> LoadDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.md")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(LoadFile)
            .ToList();

    public static Document LoadFile(string path) => FromText(Path.GetFileName(path), File.ReadAllText(path));

    public static Document FromText(string id, string rawText)
    {
        // Normalise line endings once, up front. Git on Windows may check files out with CRLF, and
        // if we didn't normalise, every character offset (and the golden-answer lookup) would differ
        // between a developer's laptop and the Linux CI runner.
        var text = rawText.Replace("\r\n", "\n").Replace('\r', '\n');
        var structure = MarkdownStructure.Parse(text);
        var title = structure.Headings.FirstOrDefault(h => h.Level == 1)?.Text
                    ?? Path.GetFileNameWithoutExtension(id);
        return new Document(id, title, text);
    }
}
