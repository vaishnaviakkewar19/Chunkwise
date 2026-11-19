using System.Net;
using System.Text;
using System.Text.Json;
using Chunkwise.Core.Embeddings;
using Microsoft.Extensions.AI;

namespace Chunkwise.Tests;

public class HashingEmbedderTests
{
    private readonly HashingEmbedder _embedder = new(dimensions: 512);

    [Fact]
    public void Vectors_are_deterministic_and_unit_length()
    {
        var a = _embedder.Embed("Annual leave carries over until March.");
        var b = _embedder.Embed("Annual leave carries over until March.");

        Assert.Equal(512, a.Length);
        Assert.Equal(a, b);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => (double)x * x)), precision: 5);
    }

    [Fact]
    public void Related_text_scores_higher_than_unrelated_text()
    {
        var query = _embedder.Embed("How many days of annual leave do I get?");
        var related = _embedder.Embed("Full-time employees receive 25 days of paid annual leave.");
        var unrelated = _embedder.Embed("Tokens expire after 3600 seconds.");

        Assert.True(VectorMath.Dot(query, related) > VectorMath.Dot(query, unrelated));
    }

    [Theory]
    [InlineData("months", "month")]
    [InlineData("monthly", "month")]
    [InlineData("leaves", "leav")]
    [InlineData("leave", "leav")]
    [InlineData("expired", "expir")]
    [InlineData("access", "access")]   // "ss" is not a plural
    [InlineData("2025", "2025")]
    public void Stemmer_folds_common_suffixes(string input, string expected)
    {
        Assert.Equal(expected, HashingEmbedder.Stem(input));
    }

    [Fact]
    public void Tokenizer_drops_stop_words_and_splits_camel_case() =>
        Assert.Equal(["valid", "signatur", "header"], HashingEmbedder.Tokenize("IsValidSignature is the header"));   // "is" and "the" are stop words

    [Fact]
    public void Empty_text_gives_the_zero_vector_rather_than_NaN() =>
        Assert.All(_embedder.Embed("the of and"), x => Assert.Equal(0f, x));
}

public class CachingEmbedderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "chunkwise-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Second_call_is_served_from_cache_and_survives_a_restart()
    {
        var inner = new CountingEmbedder();
        var cache = new CachingEmbedder(inner, _directory);

        var first = await cache.EmbedAsync(["alpha", "beta", "alpha"], EmbeddingInput.Document);
        Assert.Equal(2, inner.Calls);   // the duplicate "alpha" is embedded once

        var second = await cache.EmbedAsync(["beta", "alpha"], EmbeddingInput.Document);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(first[1], second[0]);

        // A fresh instance (a new process) reads the file instead of calling the model.
        var restarted = new CachingEmbedder(inner, _directory);
        var third = await restarted.EmbedAsync(["alpha"], EmbeddingInput.Document);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(first[0], third[0]);
    }

    [Fact]
    public void Factory_resolves_a_relative_cache_directory_against_the_base_directory_not_the_cwd()
    {
        // Regression: the web app (started in its project folder) and the CLI (started at the repo root)
        // used to write two separate caches, so the second one re-embedded everything.
        var embedder = EmbedderFactory.Create(new EmbedderSettings { Provider = "ollama", CacheDirectory = ".cache" }, _directory);

        var caching = Assert.IsType<CachingEmbedder>(embedder);
        Assert.StartsWith(Path.Combine(_directory, ".cache"), caching.FilePath);
    }

    [Fact]
    public async Task Query_and_document_embeddings_are_cached_separately()
    {
        var inner = new CountingEmbedder();
        var cache = new CachingEmbedder(inner, _directory);

        await cache.EmbedAsync(["alpha"], EmbeddingInput.Document);
        await cache.EmbedAsync(["alpha"], EmbeddingInput.Query);
        Assert.Equal(2, inner.Calls);
    }

    private sealed class CountingEmbedder : IEmbedder
    {
        public int Calls { get; private set; }
        public string ModelId => "counting";

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInput input, CancellationToken cancellationToken = default)
        {
            Calls += texts.Count;
            return Task.FromResult(texts.Select(t => new[] { t.Length, (float)input, 1f }).ToArray());
        }
    }
}

public class OllamaEmbedderTests
{
    [Fact]
    public async Task Sends_nomic_task_prefixes_and_normalises_the_response()
    {
        string? sentBody = null;
        var handler = new StubHandler(async request =>
        {
            sentBody = await request.Content!.ReadAsStringAsync();
            Assert.EndsWith("/api/embed", request.RequestUri!.AbsolutePath);
            return Json("""{"embeddings":[[3,4],[0,2]]}""");
        });
        var embedder = new OllamaEmbedder(new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") });

        var vectors = await embedder.EmbedAsync(["a", "b"], EmbeddingInput.Query);

        using var json = JsonDocument.Parse(sentBody!);
        Assert.Equal("nomic-embed-text", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(["search_query: a", "search_query: b"], json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([0.6f, 0.8f], vectors[0]);
        Assert.Equal([0f, 1f], vectors[1]);
    }

    [Fact]
    public async Task Other_models_get_the_text_unchanged()
    {
        string? sentBody = null;
        var handler = new StubHandler(async request =>
        {
            sentBody = await request.Content!.ReadAsStringAsync();
            return Json("""{"embeddings":[[1,0]]}""");
        });
        var embedder = new OllamaEmbedder(new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") }, "bge-m3");

        await embedder.EmbedAsync(["hello"], EmbeddingInput.Document);
        Assert.Contains("\"input\":[\"hello\"]", sentBody);
    }

    [Fact]
    public async Task Unreachable_server_gives_an_actionable_error()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var embedder = new OllamaEmbedder(new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedAsync(["x"], EmbeddingInput.Document));
        Assert.Contains("ollama serve", error.Message);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}

public class EmbeddingGeneratorEmbedderTests
{
    [Fact]
    public async Task Adapts_a_Microsoft_Extensions_AI_generator_and_applies_prefixes()
    {
        var generator = new FakeGenerator();
        var embedder = new EmbeddingGeneratorEmbedder(generator, "fake", queryPrefix: "q: ");

        var vectors = await embedder.EmbedAsync(["hi"], EmbeddingInput.Query);

        Assert.Equal(["q: hi"], generator.Received);
        Assert.Equal([1f, 0f], vectors[0]);
    }

    private sealed class FakeGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Received { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Received.AddRange(values);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(Received.Select(_ => new Embedding<float>(new float[] { 5, 0 }))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
