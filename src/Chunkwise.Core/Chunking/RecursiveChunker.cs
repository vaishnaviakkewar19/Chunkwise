using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// LangChain's default splitter applied to the whole document. It prefers paragraph breaks, then line
/// breaks, then spaces, so chunks are large but rarely cut mid-word. It knows nothing about Markdown.
/// </summary>
public sealed class RecursiveChunker : IChunker
{
    public string Name => "recursive";
    public string DisplayName => "Recursive (LangChain)";
    public string Description => "Splits on paragraphs, then lines, then words until chunks fit. LangChain's default.";

    public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options)
    {
        options.Validate();
        var splitter = new RecursiveCharacterTextSplitter(options.ChunkSize, options.ChunkOverlap, options.Separators);
        var spans = splitter.Split(document.Text, new TextSpan(0, document.Text.Length));
        return ChunkBuilder.Build(document, Name, spans);
    }
}
