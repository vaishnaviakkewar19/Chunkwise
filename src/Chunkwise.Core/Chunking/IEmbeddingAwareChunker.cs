using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// Optional interface for chunkers that need embeddings to decide where to split (the semantic chunker).
/// </summary>
/// <remarks>
/// Most chunkers are pure string functions, so <see cref="IChunker.Split"/> stays synchronous and
/// easy to test. Only strategies that call a model implement this async variant, and callers go
/// through <see cref="ChunkerExtensions.SplitAsync"/> so they never need to know which kind they hold.
/// </remarks>
public interface IEmbeddingAwareChunker : IChunker
{
    Task<IReadOnlyList<Chunk>> SplitAsync(Document document, ChunkerOptions options, IEmbedder embedder, CancellationToken cancellationToken = default);
}

public static class ChunkerExtensions
{
    public static Task<IReadOnlyList<Chunk>> SplitAsync(this IChunker chunker, Document document, ChunkerOptions options, IEmbedder embedder, CancellationToken cancellationToken = default) =>
        chunker is IEmbeddingAwareChunker aware
            ? aware.SplitAsync(document, options, embedder, cancellationToken)
            : Task.FromResult(chunker.Split(document, options));
}
