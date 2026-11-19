using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// "Search small, return big." Each Markdown section (split to at most <c>ParentChunkSize</c>) is a
/// <i>parent</i>; it is cut into small <i>children</i> of <c>ChunkSize</c> characters. Only children are
/// embedded and searched, but each hit returns its whole parent.
/// </summary>
/// <remarks>
/// This resolves the classic chunk-size tension. Small chunks give sharp, specific embeddings (a
/// 150-character child about dental limits matches a dental question strongly), while large chunks
/// give the LLM enough surrounding context to answer, including the second paragraph of a
/// two-paragraph answer. The price is more text per result, which the leaderboard reports honestly
/// as "Returned size". This is LangChain's ParentDocumentRetriever, with offsets instead of a docstore.
/// </remarks>
public sealed class ParentChildChunker : IChunker
{
    public string Name => "parent-child";
    public string DisplayName => "Parent-child";
    public string Description => "Searches small child chunks but returns their whole parent section (up to the parent size).";

    public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options)
    {
        options.Validate();
        var text = document.Text;

        // Parents never overlap: overlapping parents would hand the LLM duplicate text.
        var parentOptions = options with
        {
            ChunkSize = Math.Max(options.ParentChunkSize, options.ChunkSize),
            ChunkOverlap = 0,
        };
        var childSplitter = new RecursiveCharacterTextSplitter(options.ChunkSize, options.ChunkOverlap, options.Separators);

        var parentOfChild = new Dictionary<TextSpan, TextSpan>();
        foreach (var section in document.Structure.Sections)
        {
            foreach (var parent in MarkdownHeaderChunker.SplitSection(document, section.Span, parentOptions))
            {
                // Children are cut inside one parent, so a child can never belong to two parents.
                foreach (var child in childSplitter.Split(text, parent))
                    parentOfChild[child] = parent;
            }
        }

        return ChunkBuilder.Build(document, Name, parentOfChild.Keys, parentOf: child => parentOfChild[child]);
    }
}
