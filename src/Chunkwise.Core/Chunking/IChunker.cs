using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>Splits a document into chunks whose offsets point back into the original text.</summary>
public interface IChunker
{
    /// <summary>Short, URL-safe identifier, e.g. <c>recursive</c>.</summary>
    string Name { get; }

    /// <summary>Label for UIs, e.g. <c>Recursive (LangChain)</c>.</summary>
    string DisplayName { get; }

    /// <summary>One-sentence explanation shown in the UI and CLI.</summary>
    string Description { get; }

    IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options);
}
