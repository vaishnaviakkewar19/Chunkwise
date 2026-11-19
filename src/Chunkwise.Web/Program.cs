using Microsoft.AspNetCore.Diagnostics;
using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;
using Chunkwise.Core.Evaluation;
using Chunkwise.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();

// ---- Corpus and embedder: resolved once at startup -------------------------------------------
var corpusPath = builder.Configuration["Chunkwise:CorpusPath"] ?? CorpusLocator.Find(builder.Environment.ContentRootPath);
var documents = DocumentLoader.LoadDirectory(corpusPath);
var questions = GoldenSet.Load(Path.Combine(corpusPath, CorpusLocator.GoldenQuestionsFileName), documents);
var embedderSettings = builder.Configuration.GetSection("Chunkwise:Embedder").Get<EmbedderSettings>() ?? new EmbedderSettings();
// Anchor the embedding cache at the repo root (the folder holding corpus/) so the CLI and web app share it.
var repoRoot = Path.GetDirectoryName(Path.GetFullPath(corpusPath))!;
var (embedder, embedderWarning) = await CreateEmbedderWithFallbackAsync(embedderSettings, repoRoot);

builder.Services.AddSingleton(new ChunkwiseService(documents, questions, ChunkerRegistry.CreateDefault(), embedder, embedderWarning));

var app = builder.Build();

// Translate domain exceptions into RFC 7807 problem responses: an unknown strategy is the
// caller's mistake (404/400), not a server crash (500).
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error switch
    {
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
    };
    var detail = status == StatusCodes.Status500InternalServerError ? "See the server log for details." : error?.Message;
    await Results.Problem(detail, statusCode: status, title: title).ExecuteAsync(context);
}));
app.UseDefaultFiles();
// "no-cache" means "revalidate", not "never cache": the ETag check is cheap, and a demo never runs a
// stale app.js after an update.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

if (embedderWarning is not null) app.Logger.LogWarning("{Warning}", embedderWarning);
app.Logger.LogInformation("Chunkwise: {Documents} documents, {Questions} golden questions, embedder {Embedder}",
    documents.Count, questions.Count, embedder.ModelId);

// ---- API ---------------------------------------------------------------------------------------
var api = app.MapGroup("/api");

api.MapGet("/config", (ChunkwiseService svc) => new
{
    Embedder = svc.Embedder.ModelId,
    svc.EmbedderWarning,
    Defaults = new { ChunkerOptions.Default.ChunkSize, ChunkerOptions.Default.ChunkOverlap, ChunkerOptions.Default.SemanticThreshold },
    Limits = new { ChunkSettings.MinSize, ChunkSettings.MaxSize },
    Strategies = svc.Registry.All.Select(c => new { c.Name, c.DisplayName, c.Description }),
    Documents = svc.Documents.Select(d => new { d.Id, d.Title, d.Text.Length }),
    Questions = svc.Questions.Select(q => new { q.Id, q.Question, q.Document, q.Tags, q.Note, AnswerStart = q.AnswerSpan.Start, AnswerEnd = q.AnswerSpan.End }),
});

api.MapGet("/documents/{id}", (string id, ChunkwiseService svc) =>
{
    var doc = svc.GetDocument(id);
    return new
    {
        doc.Id,
        doc.Title,
        doc.Text,
        Sections = doc.Structure.Sections.Select(s => new { s.Span.Start, s.Span.End, s.Path }),
        AtomicBlocks = doc.Structure.AtomicBlocks.Select(b => new { Kind = b.Kind.ToString(), b.Span.Start, b.Span.End }),
    };
});

api.MapPost("/chunk", async (ChunkRequest request, ChunkwiseService svc, CancellationToken ct) =>
{
    var options = request.ToOptions();
    var chunks = await svc.ChunkAsync(request.DocumentId, request.Strategy, options, ct);
    return new
    {
        request.DocumentId,
        request.Strategy,
        Options = new { options.ChunkSize, options.ChunkOverlap },
        Chunks = chunks.Select(ChunkDto.From),
        Stats = Stats(chunks),
    };
});

api.MapPost("/ask", async (AskRequest request, ChunkwiseService svc) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.Problem("Question must not be empty.", statusCode: StatusCodes.Status400BadRequest);

    var options = request.ToOptions();
    int topK = Math.Clamp(request.TopK, 1, 10);

    // Strategies are independent, so build/search them concurrently.
    var results = await Task.WhenAll(svc.Registry.All.Select(async chunker =>
    {
        var index = await svc.GetIndexAsync(chunker, options, request.Enrich);
        var hits = await index.SearchAsync(request.Question, topK);
        return new { chunker.Name, chunker.DisplayName, Hits = hits.Select(HitDto.From) };
    }));

    return Results.Ok(new { request.Question, request.Enrich, Strategies = results });
});

api.MapGet("/leaderboard", async (int? chunkSize, int? chunkOverlap, ChunkwiseService svc) =>
{
    var options = new ChunkSettings(chunkSize ?? ChunkerOptions.Default.ChunkSize, chunkOverlap ?? ChunkerOptions.Default.ChunkOverlap).ToOptions();
    var started = System.Diagnostics.Stopwatch.StartNew();
    var reports = await svc.GetLeaderboardAsync(options);
    return new
    {
        Embedder = svc.Embedder.ModelId,
        Options = new { options.ChunkSize, options.ChunkOverlap },
        ElapsedMs = started.ElapsedMilliseconds,
        Reports = reports.Select(ReportDto.From),
    };
});

app.Run();

// ---- Helpers -----------------------------------------------------------------------------------

static object Stats(IReadOnlyList<Chunk> chunks) => chunks.Count == 0
    ? new { Count = 0, Average = 0.0, Min = 0, Max = 0 }
    : new { chunks.Count, Average = chunks.Average(c => c.Length), Min = chunks.Min(c => c.Length), Max = chunks.Max(c => c.Length) };

// The demo must never fail to start: if Ollama is configured but not running, fall back to the
// offline embedder and show a banner instead of crashing.
static async Task<(IEmbedder, string?)> CreateEmbedderWithFallbackAsync(EmbedderSettings settings, string repoRoot)
{
    var embedder = EmbedderFactory.Create(settings, repoRoot);
    if (embedder is HashingEmbedder) return (embedder, null);

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await embedder.EmbedAsync(["warm-up"], EmbeddingInput.Query, timeout.Token);
        return (embedder, null);
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
    {
        return (new HashingEmbedder(), $"Embedder '{settings.Provider}' unavailable ({ex.Message}). Falling back to the offline hashing embedder.");
    }
}

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
