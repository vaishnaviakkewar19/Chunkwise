namespace Chunkwise.Core.Embeddings;

/// <summary>
/// Whether a text is being stored in the index or used to search it.
/// </summary>
/// <remarks>
/// Many modern embedding models are <i>asymmetric</i>: they are trained with different instructions for
/// queries and passages (nomic-embed-text expects <c>search_query:</c> / <c>search_document:</c> prefixes).
/// Getting this wrong silently costs several points of recall, so it is part of the interface.
/// </remarks>
public enum EmbeddingInput
{
    Document,
    Query,
}

/// <summary>Turns text into vectors. All vectors returned are L2-normalised.</summary>
/// <remarks>
/// Why our own interface instead of using <c>IEmbeddingGenerator</c> from Microsoft.Extensions.AI
/// everywhere? Two reasons: we need the query/document distinction above, and returning plain
/// <c>float[]</c> keeps the vector index and evaluator trivially simple. Any
/// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> (OpenAI, Azure OpenAI, ONNX...) plugs in
/// through <see cref="EmbeddingGeneratorEmbedder"/>.
/// </remarks>
public interface IEmbedder
{
    /// <summary>Identifies the model; used as part of the embedding cache key.</summary>
    string ModelId { get; }

    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInput input, CancellationToken cancellationToken = default);
}

public static class EmbedderExtensions
{
    public static async Task<float[]> EmbedOneAsync(this IEmbedder embedder, string text, EmbeddingInput input, CancellationToken cancellationToken = default) =>
        (await embedder.EmbedAsync([text], input, cancellationToken))[0];
}
