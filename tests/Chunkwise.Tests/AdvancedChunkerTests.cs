using Chunkwise.Core;
using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;
using Chunkwise.Core.Evaluation;
using Chunkwise.Core.Retrieval;

namespace Chunkwise.Tests;

public class SemanticChunkerTests
{
    // Three sentences about dental cover, then three about API tokens. A topic-aware embedder
    // should make the only low-similarity boundary the one between the two topics.
    private const string TwoTopics =
        "Dental cleanings are free. Dental fillings are covered at 80 percent. Dental crowns are covered at 50 percent. " +
        "Tokens expire after one hour. Tokens are sent in a header. Tokens must be refreshed before expiry.";

    private static readonly Document Doc = DocumentLoader.FromText("topics.md", TwoTopics);

    [Fact]
    public async Task Splits_at_the_topic_shift()
    {
        var chunker = new SemanticChunker(windowSize: 0);
        var options = new ChunkerOptions { ChunkSize = 1000, ChunkOverlap = 0, SemanticThreshold = 0.25 };

        var chunks = await chunker.SplitAsync(Doc, options, new TopicEmbedder());

        Assert.Equal(2, chunks.Count);
        Assert.EndsWith("50 percent.", chunks[0].Text);
        Assert.StartsWith("Tokens expire", chunks[1].Text);
    }

    [Fact]
    public async Task Threshold_zero_disables_semantic_splits_so_only_the_size_cap_applies()
    {
        var chunker = new SemanticChunker(windowSize: 0);
        var options = new ChunkerOptions { ChunkSize = 1000, ChunkOverlap = 0, SemanticThreshold = 0 };

        var chunks = await chunker.SplitAsync(Doc, options, new TopicEmbedder());
        Assert.Single(chunks);
    }

    [Fact]
    public async Task Size_cap_still_applies_within_one_topic()
    {
        var options = new ChunkerOptions { ChunkSize = 60, ChunkOverlap = 0, SemanticThreshold = 0 };
        var chunks = await new SemanticChunker().SplitAsync(Doc, options, new TopicEmbedder());

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, c => Assert.True(c.Length <= 60));
    }

    [Fact]
    public void Sync_split_uses_the_offline_embedder_and_is_deterministic()
    {
        var chunker = new SemanticChunker();
        var a = chunker.Split(Doc, ChunkerOptions.Default);
        var b = chunker.Split(Doc, ChunkerOptions.Default);
        Assert.Equal(a.Select(c => c.Span), b.Select(c => c.Span));
    }

    [Theory]
    [InlineData(0.0, double.NegativeInfinity)]
    [InlineData(0.25, 2.0)]
    [InlineData(0.5, 3.0)]
    [InlineData(1.0, double.PositiveInfinity)]
    public void Percentile_picks_the_expected_cutoff(double p, double expected) =>
        Assert.Equal(expected, SemanticChunker.Percentile([5, 1, 4, 2, 3], p));

    /// <summary>Maps each sentence to one of two orthogonal "topic" vectors.</summary>
    private sealed class TopicEmbedder : IEmbedder
    {
        public string ModelId => "topic";

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(texts.Select(t => t.Contains("Dental") ? new[] { 1f, 0f } : new[] { 0f, 1f }).ToArray());
    }
}

[Collection(CorpusCollection.Name)]
public class ParentChildChunkerTests(CorpusFixture corpus)
{
    private static readonly ChunkerOptions Options = new() { ChunkSize = 200, ChunkOverlap = 20, ParentChunkSize = 1200 };

    [Fact]
    public void Every_child_lies_inside_its_parent_and_parents_never_overlap()
    {
        foreach (var doc in corpus.Documents)
        {
            var chunks = new ParentChildChunker().Split(doc, Options);
            Assert.All(chunks, c =>
            {
                Assert.NotNull(c.ParentSpan);
                Assert.True(c.ParentSpan!.Value.Contains(c.Span), $"{c.Id} escapes its parent");
            });

            var parents = chunks.Select(c => c.ParentSpan!.Value).Distinct().OrderBy(p => p.Start).ToList();
            for (int i = 1; i < parents.Count; i++)
                Assert.False(parents[i - 1].Overlaps(parents[i]), $"parents {parents[i - 1]} and {parents[i]} overlap in {doc.Id}");
        }
    }

    [Fact]
    public void Parents_follow_markdown_sections_and_never_cut_tables()
    {
        var doc = corpus.Get("benefits-guide.md");
        var parents = new ParentChildChunker().Split(doc, Options).Select(c => c.ParentSpan!.Value).Distinct().ToList();

        Assert.All(parents, p => Assert.Contains(doc.Structure.Sections, s => s.Span.Contains(p)));
        foreach (var table in doc.Structure.AtomicBlocks)
            Assert.Contains(parents, p => p.Contains(table.Span));
    }

    [Fact]
    public async Task Retrieval_returns_the_parent_so_the_two_paragraph_answer_is_covered()
    {
        // hb-07 needs two consecutive paragraphs. A 200-character child holds at most one of them,
        // but its parent (the whole Parental Leave section) holds both.
        var question = corpus.Questions.Single(q => q.Id == "hb-07");
        var evaluator = new Evaluator(new HashingEmbedder());
        var report = await evaluator.EvaluateAsync(
            await StrategyIndex.BuildAsync(new ParentChildChunker(), corpus.Documents, Options, false, evaluator.Embedder), [question]);

        Assert.Equal(1, report.Questions[0].FirstHitRank);
        Assert.Equal(1.0, report.Questions[0].CoverageAt3, precision: 6);
        Assert.True(report.AverageRetrievedLength > report.AverageChunkLength);
    }

    [Fact]
    public void Child_size_larger_than_parent_size_still_works()
    {
        var doc = corpus.Get("it-faq.md");
        var chunks = new ParentChildChunker().Split(doc, new ChunkerOptions { ChunkSize = 800, ChunkOverlap = 0, ParentChunkSize = 100 });
        Assert.All(chunks, c => Assert.True(c.ParentSpan!.Value.Contains(c.Span)));
    }
}
