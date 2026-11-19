using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Chunkwise.Core.Embeddings;

/// <summary>
/// Calls a local <a href="https://ollama.com">Ollama</a> server's <c>/api/embed</c> endpoint.
/// Default model: <c>nomic-embed-text</c> (768 dimensions, runs comfortably on a laptop CPU).
/// </summary>
/// <remarks>
/// A dozen lines of <see cref="HttpClient"/> instead of an SDK package: the API is one POST with a
/// JSON body, and seeing the raw request demystifies what an "embedding call" actually is.
/// </remarks>
public sealed class OllamaEmbedder : IEmbedder
{
    // Ollama accepts arrays of inputs; batching cuts HTTP overhead roughly in proportion to batch size.
    private const int BatchSize = 32;

    private readonly HttpClient _http;
    private readonly string _model;

    public OllamaEmbedder(HttpClient http, string model = "nomic-embed-text")
    {
        _http = http;
        _model = model;
    }

    public OllamaEmbedder(Uri baseAddress, string model = "nomic-embed-text")
        : this(new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) }, model)
    {
    }

    public string ModelId => $"ollama-{_model}";

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInput input, CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(BatchSize))
        {
            var request = new EmbedRequest(_model, batch.Select(t => ApplyTaskPrefix(t, input)).ToArray());

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync("api/embed", request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Could not reach Ollama at {_http.BaseAddress}. Is it running? Try 'ollama serve' and 'ollama pull {_model}'.", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {body}. Did you run 'ollama pull {_model}'?");
            }

            var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken)
                          ?? throw new InvalidOperationException("Ollama returned an empty response.");
            if (payload.Embeddings.Length != batch.Length)
                throw new InvalidOperationException($"Ollama returned {payload.Embeddings.Length} embeddings for {batch.Length} inputs.");

            results.AddRange(payload.Embeddings.Select(VectorMath.Normalize));
        }

        return results.ToArray();
    }

    /// <summary>
    /// nomic-embed-text was trained with task prefixes and retrieves noticeably worse without them.
    /// Other models get the text unchanged.
    /// </summary>
    internal string ApplyTaskPrefix(string text, EmbeddingInput input) =>
        _model.StartsWith("nomic-embed", StringComparison.OrdinalIgnoreCase)
            ? (input == EmbeddingInput.Query ? "search_query: " : "search_document: ") + text
            : text;

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] float[][] Embeddings);
}
