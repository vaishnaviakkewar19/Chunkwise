using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;
using Chunkwise.Core.Retrieval;

namespace Chunkwise.Core.Evaluation;

/// <summary>How one strategy did on one golden question.</summary>
/// <param name="FirstHitRank">1-based rank of the first result that contains the answer, or null if none in the top 10.</param>
/// <param name="CoverageAt3">Fraction of the answer's characters present in the top 3 results combined.</param>
public sealed record QuestionResult(
    string QuestionId,
    string Question,
    int? FirstHitRank,
    double CoverageAt3,
    IReadOnlyList<SearchHit> TopHits);

/// <summary>One row of the leaderboard.</summary>
public sealed record StrategyReport(
    string Strategy,
    string DisplayName,
    bool Enriched,
    int ChunkCount,
    double AverageChunkLength,
    double AverageRetrievedLength,
    double RecallAt1,
    double RecallAt3,
    double Mrr,
    double CoverageAt3,
    IReadOnlyList<QuestionResult> Questions)
{
    public string Label => Enriched ? $"{DisplayName} + context" : DisplayName;
}

/// <summary>
/// Pure metric functions, separated from the retrieval code so they can be unit-tested with plain numbers.
/// </summary>
public static class RetrievalMetrics
{
    /// <summary>The cut-off for MRR: a hit at rank 11 or worse scores 0, as if it were never found.</summary>
    public const int MrrCutoff = 10;

    /// <summary>Recall@k: share of questions whose first hit is at rank k or better.</summary>
    public static double RecallAtK(IReadOnlyList<int?> firstHitRanks, int k) =>
        firstHitRanks.Count == 0 ? 0 : firstHitRanks.Count(r => r <= k) / (double)firstHitRanks.Count;

    /// <summary>
    /// Mean Reciprocal Rank: average of 1/rank of the first hit. Unlike Recall@k it rewards ranking the
    /// answer <i>higher</i>: rank 1 scores 1.0, rank 2 scores 0.5, rank 3 scores 0.33.
    /// </summary>
    public static double MeanReciprocalRank(IReadOnlyList<int?> firstHitRanks) =>
        firstHitRanks.Count == 0 ? 0 : firstHitRanks.Sum(r => r is { } rank && rank <= MrrCutoff ? 1.0 / rank : 0) / firstHitRanks.Count;

    /// <summary>
    /// A result is a hit when its retrieved span overlaps the expected answer passage, in the same document.
    /// </summary>
    /// <remarks>
    /// "Any overlap" is generous: a fixed-size chunk that holds only the last few words of the answer
    /// still counts. That is why the report also has <see cref="Coverage"/>, which measures how much of the answer was retrieved.
    /// </remarks>
    public static bool IsHit(SearchHit hit, GoldenQuestion question) =>
        hit.Chunk.DocumentId.Equals(question.Document, StringComparison.OrdinalIgnoreCase)
        && hit.Chunk.RetrievalSpan.Overlaps(question.AnswerSpan);

    /// <summary>Fraction of the answer's characters that appear in the union of the given hits.</summary>
    public static double Coverage(IEnumerable<SearchHit> hits, GoldenQuestion question)
    {
        var answer = question.AnswerSpan;
        if (answer.Length == 0) return 0;

        var covered = new bool[answer.Length];
        foreach (var hit in hits.Where(h => h.Chunk.DocumentId.Equals(question.Document, StringComparison.OrdinalIgnoreCase)))
        {
            var span = hit.Chunk.RetrievalSpan;
            for (int i = Math.Max(span.Start, answer.Start); i < Math.Min(span.End, answer.End); i++)
                covered[i - answer.Start] = true;
        }
        return covered.Count(c => c) / (double)answer.Length;
    }
}

/// <summary>
/// Runs the golden questions against each strategy and produces the leaderboard.
/// </summary>
public sealed class Evaluator(IEmbedder embedder)
{
    // Questions are embedded once and reused for every strategy; only the chunks change between runs.
    private readonly Dictionary<string, float[]> _questionVectors = new(StringComparer.Ordinal);

    public IEmbedder Embedder => embedder;

    public async Task<StrategyReport> EvaluateAsync(StrategyIndex index, IReadOnlyList<GoldenQuestion> questions, CancellationToken cancellationToken = default)
    {
        await EnsureQuestionVectorsAsync(questions, cancellationToken);

        var results = questions.Select(q =>
        {
            var hits = index.Search(_questionVectors[q.Question], RetrievalMetrics.MrrCutoff);
            var firstHit = hits.FirstOrDefault(h => RetrievalMetrics.IsHit(h, q));
            return new QuestionResult(q.Id, q.Question, firstHit?.Rank, RetrievalMetrics.Coverage(hits.Take(3), q), hits.Take(3).ToList());
        }).ToList();

        var ranks = results.Select(r => r.FirstHitRank).ToList();
        return new StrategyReport(
            Strategy: index.Chunker.Name,
            DisplayName: index.Chunker.DisplayName,
            Enriched: index.Enriched,
            ChunkCount: index.Chunks.Count,
            AverageChunkLength: index.Chunks.Count == 0 ? 0 : index.Chunks.Average(c => c.Length),
            // For parent-child this is the parent size: what the LLM actually receives per result.
            AverageRetrievedLength: index.Chunks.Count == 0 ? 0 : index.Chunks.Average(c => c.RetrievalSpan.Length),
            RecallAt1: RetrievalMetrics.RecallAtK(ranks, 1),
            RecallAt3: RetrievalMetrics.RecallAtK(ranks, 3),
            Mrr: RetrievalMetrics.MeanReciprocalRank(ranks),
            CoverageAt3: results.Average(r => r.CoverageAt3),
            Questions: results);
    }

    /// <summary>Builds an index and evaluates it for every (strategy, enrichment) combination.</summary>
    public async Task<IReadOnlyList<StrategyReport>> RunLeaderboardAsync(
        IEnumerable<IChunker> chunkers,
        IReadOnlyList<Document> documents,
        IReadOnlyList<GoldenQuestion> questions,
        ChunkerOptions options,
        IReadOnlyList<bool> enrichmentModes,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reports = new List<StrategyReport>();
        foreach (var chunker in chunkers)
        {
            foreach (var enrich in enrichmentModes)
            {
                progress?.Report($"{chunker.DisplayName}{(enrich ? " + context" : "")}");
                var index = await StrategyIndex.BuildAsync(chunker, documents, options, enrich, embedder, cancellationToken);
                reports.Add(await EvaluateAsync(index, questions, cancellationToken));
            }
        }
        return reports;
    }

    private async Task EnsureQuestionVectorsAsync(IReadOnlyList<GoldenQuestion> questions, CancellationToken cancellationToken)
    {
        var missing = questions.Select(q => q.Question).Where(q => !_questionVectors.ContainsKey(q)).Distinct().ToList();
        if (missing.Count == 0) return;

        var vectors = await embedder.EmbedAsync(missing, EmbeddingInput.Query, cancellationToken);
        for (int i = 0; i < missing.Count; i++)
            _questionVectors[missing[i]] = vectors[i];
    }
}
