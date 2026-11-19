using Chunkwise.Core.Chunking;
using Chunkwise.Core.Evaluation;
using Chunkwise.Core.Retrieval;

namespace Chunkwise.Web;

// Request and response shapes for the JSON API. Kept separate from the Core records so the wire
// format can stay stable (and small) while the domain model evolves.

public sealed record ChunkSettings(int ChunkSize = 500, int ChunkOverlap = 50, double? SemanticThreshold = null)
{
    public const int MinSize = 50, MaxSize = 2000;

    /// <summary>Clamps untrusted input into a safe range instead of rejecting it, so a slider can never break the UI.</summary>
    public ChunkerOptions ToOptions()
    {
        int size = Math.Clamp(ChunkSize, MinSize, MaxSize);
        int overlap = Math.Clamp(ChunkOverlap, 0, size / 2);
        return new ChunkerOptions
        {
            ChunkSize = size,
            ChunkOverlap = overlap,
            SemanticThreshold = Math.Clamp(SemanticThreshold ?? ChunkerOptions.Default.SemanticThreshold, 0, 1),
            ParentChunkSize = Math.Max(size * 3, ChunkerOptions.Default.ParentChunkSize),
        };
    }
}

public sealed record ChunkRequest(string DocumentId, string Strategy, int ChunkSize = 500, int ChunkOverlap = 50, double? SemanticThreshold = null)
{
    public ChunkerOptions ToOptions() => new ChunkSettings(ChunkSize, ChunkOverlap, SemanticThreshold).ToOptions();
}

public sealed record AskRequest(string Question, int ChunkSize = 500, int ChunkOverlap = 50, bool Enrich = false, int TopK = 3)
{
    public ChunkerOptions ToOptions() => new ChunkSettings(ChunkSize, ChunkOverlap).ToOptions();
}

/// <summary>Chunk metadata without the text: the browser already has the document, so offsets are enough.</summary>
public sealed record ChunkDto(int Index, int Start, int End, int Length, string SectionPath, int? ParentStart, int? ParentEnd)
{
    public static ChunkDto From(Chunk c) =>
        new(c.Index, c.Start, c.End, c.Length, c.SectionPath, c.ParentSpan?.Start, c.ParentSpan?.End);
}

public sealed record HitDto(int Rank, float Score, string DocumentId, ChunkDto Chunk, string Text, int RetrievedStart, int RetrievedEnd)
{
    public static HitDto From(SearchHit hit) => new(
        hit.Rank, hit.Score, hit.Chunk.DocumentId, ChunkDto.From(hit.Chunk), hit.Chunk.Text,
        hit.Chunk.RetrievalSpan.Start, hit.Chunk.RetrievalSpan.End);
}

public sealed record ReportDto(
    string Strategy, string Label, bool Enriched, int ChunkCount, double AverageChunkLength, double AverageRetrievedLength,
    double RecallAt1, double RecallAt3, double Mrr, double CoverageAt3,
    IReadOnlyList<QuestionRankDto> Questions)
{
    public static ReportDto From(StrategyReport r) => new(
        r.Strategy, r.Label, r.Enriched, r.ChunkCount, r.AverageChunkLength, r.AverageRetrievedLength,
        r.RecallAt1, r.RecallAt3, r.Mrr, r.CoverageAt3,
        r.Questions.Select(q => new QuestionRankDto(q.QuestionId, q.FirstHitRank, q.CoverageAt3)).ToList());
}

public sealed record QuestionRankDto(string Id, int? Rank, double Coverage);
