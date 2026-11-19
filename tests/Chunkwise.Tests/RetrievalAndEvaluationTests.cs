using Chunkwise.Core;
using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;
using Chunkwise.Core.Evaluation;
using Chunkwise.Core.Retrieval;

namespace Chunkwise.Tests;

public class RetrievalMetricsTests
{
    [Fact]
    public void Recall_and_MRR_are_computed_from_first_hit_ranks()
    {
        int?[] ranks = [1, 3, null, 2];

        Assert.Equal(0.25, RetrievalMetrics.RecallAtK(ranks, 1));
        Assert.Equal(0.75, RetrievalMetrics.RecallAtK(ranks, 3));
        Assert.Equal((1 + 1.0 / 3 + 0 + 0.5) / 4, RetrievalMetrics.MeanReciprocalRank(ranks), precision: 10);
    }

    [Fact]
    public void Ranks_beyond_the_cutoff_score_zero_MRR() =>
        Assert.Equal(0, RetrievalMetrics.MeanReciprocalRank([RetrievalMetrics.MrrCutoff + 1]));

    [Fact]
    public void Empty_input_gives_zero_not_NaN()
    {
        Assert.Equal(0, RetrievalMetrics.RecallAtK([], 3));
        Assert.Equal(0, RetrievalMetrics.MeanReciprocalRank([]));
    }

    [Fact]
    public void Hit_requires_same_document_and_overlapping_span()
    {
        var question = Question("doc.md", new TextSpan(100, 200));

        Assert.True(RetrievalMetrics.IsHit(Hit("doc.md", 150, 300), question));
        Assert.True(RetrievalMetrics.IsHit(Hit("DOC.md", 199, 210), question));    // one character is enough
        Assert.False(RetrievalMetrics.IsHit(Hit("doc.md", 200, 300), question));   // end is exclusive
        Assert.False(RetrievalMetrics.IsHit(Hit("other.md", 100, 200), question));
    }

    [Fact]
    public void Parent_span_is_what_gets_scored()
    {
        var question = Question("doc.md", new TextSpan(500, 520));
        var child = Hit("doc.md", 0, 50) with { };
        var withParent = child with { Chunk = child.Chunk with { ParentSpan = new TextSpan(0, 1000) } };

        Assert.False(RetrievalMetrics.IsHit(child, question));
        Assert.True(RetrievalMetrics.IsHit(withParent, question));
    }

    [Fact]
    public void Coverage_is_the_union_of_hits_over_the_answer()
    {
        var question = Question("doc.md", new TextSpan(100, 200));
        var hits = new[] { Hit("doc.md", 50, 130), Hit("doc.md", 120, 150), Hit("other.md", 100, 200) };

        Assert.Equal(0.5, RetrievalMetrics.Coverage(hits, question), precision: 10);
    }

    internal static GoldenQuestion Question(string document, TextSpan answer) =>
        new() { Id = "q", Question = "?", Document = document, Answer = "", AnswerSpan = answer };

    internal static SearchHit Hit(string document, int start, int end) =>
        new(new Chunk($"{document}#{start}", document, "test", 0, start, end, "", ""), 1f, 1);
}

public class InMemoryVectorIndexTests
{
    [Fact]
    public void Returns_top_k_by_cosine_similarity_best_first()
    {
        var index = new InMemoryVectorIndex();
        index.Add(Chunk("a"), [1, 0]);
        index.Add(Chunk("b"), [0.7f, 0.7f]);
        index.Add(Chunk("c"), [0, 1]);
        index.Add(Chunk("d"), [-1, 0]);

        var hits = index.Search([2, 0.1f], k: 3);   // not normalised on purpose

        Assert.Equal(["a", "b", "c"], hits.Select(h => h.Chunk.Id));
        Assert.Equal([1, 2, 3], hits.Select(h => h.Rank));
        Assert.True(hits[0].Score > hits[1].Score && hits[1].Score > hits[2].Score);
        Assert.InRange(hits[0].Score, 0.99f, 1.0f);
    }

    [Fact]
    public void Mismatched_dimensions_fail_with_a_helpful_message()
    {
        var index = new InMemoryVectorIndex();
        index.Add(Chunk("a"), [1, 0, 0]);
        var error = Assert.Throws<ArgumentException>(() => index.Search([1, 0], 1));
        Assert.Contains("different embedder", error.Message);
    }

    private static Chunk Chunk(string id) => new(id, "doc.md", "test", 0, 0, 1, "x", "");
}

[Collection(CorpusCollection.Name)]
public class EvaluatorTests(CorpusFixture corpus)
{
    [Fact]
    public void Enrichment_prefixes_title_and_section_path()
    {
        var doc = corpus.Get("employee-handbook.md");
        var chunk = new ParagraphChunker().Split(doc, ChunkerOptions.Default).Single(c => c.Text.StartsWith("This policy provides"));

        Assert.StartsWith(
            "Brightwell Health Employee Handbook > Working Hours and Location > Remote Work Stipend: This policy provides",
            ContextualEnrichment.TextToEmbed(doc, chunk, enrich: true));
        Assert.Equal(chunk.Text, ContextualEnrichment.TextToEmbed(doc, chunk, enrich: false));
    }

    [Fact]
    public async Task Leaderboard_runs_every_strategy_and_produces_sane_metrics()
    {
        var evaluator = new Evaluator(new HashingEmbedder());
        var reports = await evaluator.RunLeaderboardAsync(
            ChunkerRegistry.CreateDefault().All, corpus.Documents, corpus.Questions, ChunkerOptions.Default, [false, true]);

        Assert.Equal(ChunkerRegistry.CreateDefault().All.Count * 2, reports.Count);
        foreach (var r in reports)
        {
            Assert.Equal(corpus.Questions.Count, r.Questions.Count);
            Assert.InRange(r.RecallAt1, 0, 1);
            Assert.True(r.RecallAt1 <= r.RecallAt3, $"{r.Label}: Recall@1 > Recall@3");
            Assert.InRange(r.Mrr, r.RecallAt1, r.RecallAt3 + 1e-9 + (1 - r.RecallAt3));
            Assert.True(r.ChunkCount > 0);
        }

        // Even a bag-of-words embedder should find most answers; if this drops, something is broken.
        Assert.Contains(reports, r => r.RecallAt3 >= 0.8);
    }

    [Fact]
    public async Task Contextual_enrichment_rescues_the_this_policy_question_for_paragraph_chunks()
    {
        // The coreference case from the corpus: the paragraph never says "remote work stipend".
        var question = corpus.Questions.Single(q => q.Id == "hb-02");
        var evaluator = new Evaluator(new HashingEmbedder());
        var chunker = new ParagraphChunker();

        var plain = await evaluator.EvaluateAsync(
            await StrategyIndex.BuildAsync(chunker, corpus.Documents, ChunkerOptions.Default, false, evaluator.Embedder), [question]);
        var enriched = await evaluator.EvaluateAsync(
            await StrategyIndex.BuildAsync(chunker, corpus.Documents, ChunkerOptions.Default, true, evaluator.Embedder), [question]);

        Assert.True(enriched.Mrr > plain.Mrr, $"enriched MRR {enriched.Mrr} should beat plain {plain.Mrr}");
    }

    [Fact]
    public async Task Search_returns_each_retrieval_span_once()
    {
        var doc = DocumentLoader.FromText("d.md", "# T\n\nalpha beta gamma.\n\nalpha beta delta.");
        var chunker = new DuplicateParentChunker();
        var index = await StrategyIndex.BuildAsync(chunker, [doc], ChunkerOptions.Default, false, new HashingEmbedder());

        var hits = await index.SearchAsync("alpha beta", 3);
        Assert.Single(hits);
    }

    /// <summary>Two different children pointing at the same parent, like parent-child chunking.</summary>
    private sealed class DuplicateParentChunker : IChunker
    {
        public string Name => "dup";
        public string DisplayName => "dup";
        public string Description => "";

        public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options) =>
            ChunkBuilder.Build(document, Name, [new TextSpan(5, 22), new TextSpan(24, 41)], parentOf: _ => new TextSpan(0, document.Text.Length));
    }
}
