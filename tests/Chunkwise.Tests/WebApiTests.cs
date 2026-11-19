using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chunkwise.Tests;

/// <summary>
/// End-to-end tests through the real HTTP pipeline (in memory, no port). They protect the contract
/// the browser UI depends on, which unit tests of Core can't see.
/// </summary>
public class WebApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Config_lists_documents_strategies_and_questions()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("/api/config"));
        var root = json.RootElement;

        Assert.StartsWith("hashing", root.GetProperty("embedder").GetString());
        Assert.Equal(4, root.GetProperty("documents").GetArrayLength());
        Assert.Contains(root.GetProperty("strategies").EnumerateArray(), s => s.GetProperty("name").GetString() == "markdown");
        Assert.InRange(root.GetProperty("questions").GetArrayLength(), 25, 30);
    }

    [Fact]
    public async Task Chunk_returns_offsets_that_match_the_document()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/documents/it-faq.md"));
        var text = doc.RootElement.GetProperty("text").GetString()!;

        var response = await _client.PostAsJsonAsync("/api/chunk", new { documentId = "it-faq.md", strategy = "recursive", chunkSize = 300, chunkOverlap = 30 });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var chunks = json.RootElement.GetProperty("chunks").EnumerateArray().ToList();
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            int start = c.GetProperty("start").GetInt32(), end = c.GetProperty("end").GetInt32();
            Assert.InRange(end - start, 1, 300);
            Assert.Equal(end - start, c.GetProperty("length").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(text[start..end]));
        });
    }

    [Fact]
    public async Task Ask_returns_top_hits_for_every_strategy()
    {
        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "How long do Secure Share links stay valid?", enrich = true });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var strategies = json.RootElement.GetProperty("strategies").EnumerateArray().ToList();
        Assert.True(strategies.Count >= 5);
        Assert.All(strategies, s =>
        {
            var hits = s.GetProperty("hits").EnumerateArray().ToList();
            Assert.Equal(3, hits.Count);
            Assert.Equal([1, 2, 3], hits.Select(h => h.GetProperty("rank").GetInt32()));
            Assert.Equal("it-faq.md", hits[0].GetProperty("documentId").GetString());
        });
    }

    [Fact]
    public async Task Leaderboard_has_a_plain_and_an_enriched_row_per_strategy()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("/api/leaderboard?chunkSize=400&chunkOverlap=40"));
        var reports = json.RootElement.GetProperty("reports").EnumerateArray().ToList();

        Assert.Equal(reports.Count / 2, reports.Count(r => r.GetProperty("enriched").GetBoolean()));
        Assert.All(reports, r => Assert.InRange(r.GetProperty("recallAt3").GetDouble(), 0, 1));
    }

    [Theory]
    [InlineData("/api/documents/nope.md", HttpStatusCode.NotFound)]
    public async Task Unknown_resources_return_404_problem_details(string url, HttpStatusCode expected)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Bad_input_is_rejected_or_clamped_never_a_500()
    {
        var unknownStrategy = await _client.PostAsJsonAsync("/api/chunk", new { documentId = "it-faq.md", strategy = "magic" });
        Assert.Equal(HttpStatusCode.NotFound, unknownStrategy.StatusCode);

        var emptyQuestion = await _client.PostAsJsonAsync("/api/ask", new { question = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, emptyQuestion.StatusCode);

        // Slider values from a tampered request are clamped into range rather than crashing a chunker.
        var absurd = await _client.PostAsJsonAsync("/api/chunk", new { documentId = "it-faq.md", strategy = "fixed", chunkSize = -1, chunkOverlap = 1_000_000 });
        Assert.Equal(HttpStatusCode.OK, absurd.StatusCode);
    }

    [Fact]
    public async Task Static_ui_is_served_at_the_root()
    {
        var html = await _client.GetStringAsync("/");
        Assert.Contains("<title>Chunkwise", html);
    }
}
