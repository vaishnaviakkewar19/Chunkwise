namespace Chunkwise.Core.Chunking;

/// <summary>The list of strategies that the CLI, web UI and evaluator iterate over.</summary>
public sealed class ChunkerRegistry
{
    private readonly Dictionary<string, IChunker> _byName;

    public ChunkerRegistry(IEnumerable<IChunker> chunkers)
    {
        All = chunkers.ToList();
        _byName = All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IChunker> All { get; }

    /// <summary>All seven strategies, ordered roughly from naive to sophisticated.</summary>
    public static ChunkerRegistry CreateDefault() => new(
    [
        new FixedSizeChunker(),
        new SentenceChunker(),
        new ParagraphChunker(),
        new RecursiveChunker(),
        new MarkdownHeaderChunker(),
        new SemanticChunker(),
        new ParentChildChunker(),
    ]);

    public IChunker Get(string name) =>
        _byName.TryGetValue(name, out var chunker)
            ? chunker
            : throw new KeyNotFoundException($"Unknown chunking strategy '{name}'. Known: {string.Join(", ", _byName.Keys)}.");
}
