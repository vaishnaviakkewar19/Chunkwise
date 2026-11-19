using Microsoft.Extensions.AI;

namespace Chunkwise.Core.Embeddings;

/// <summary>
/// Adapts any Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> to
/// <see cref="IEmbedder"/>. This is the extension point for hosted models: an OpenAI, Azure OpenAI or
/// ONNX generator from their own NuGet packages can be dropped in without touching the rest of Chunkwise.
/// </summary>
/// <example>
/// <code>
/// // dotnet add package Microsoft.Extensions.AI.OpenAI
/// IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt; generator =
///     new OpenAIClient(apiKey).GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();
/// IEmbedder embedder = new EmbeddingGeneratorEmbedder(generator, "openai-text-embedding-3-small");
/// </code>
/// </example>
public sealed class EmbeddingGeneratorEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    string modelId,
    string? queryPrefix = null,
    string? documentPrefix = null) : IEmbedder
{
    public string ModelId { get; } = modelId;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInput input, CancellationToken cancellationToken = default)
    {
        var prefix = input == EmbeddingInput.Query ? queryPrefix : documentPrefix;
        var values = prefix is null ? texts : texts.Select(t => prefix + t).ToList();

        var embeddings = await generator.GenerateAsync(values, cancellationToken: cancellationToken);
        return embeddings.Select(e => VectorMath.Normalize(e.Vector.ToArray())).ToArray();
    }
}
