using Chunkwise.Core;
using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;

namespace Chunkwise.Tests;

/// <summary>Strategy-specific behaviour: what makes each chunker different from the others.</summary>
[Collection(CorpusCollection.Name)]
public class ChunkerBehaviourTests(CorpusFixture corpus)
{
    private static readonly ChunkerOptions Small = new() { ChunkSize = 200, ChunkOverlap = 20 };

    [Fact]
    public void Fixed_size_windows_are_exact_and_overlap_exactly()
    {
        var doc = corpus.Get("employee-handbook.md");
        var options = new ChunkerOptions { ChunkSize = 300, ChunkOverlap = 60 };
        var chunks = new FixedSizeChunker().Split(doc, options);

        Assert.All(chunks.SkipLast(1), c => Assert.Equal(300, c.Length));
        for (int i = 1; i < chunks.Count; i++)
            Assert.Equal(60, chunks[i - 1].End - chunks[i].Start);
        Assert.Equal(doc.Text.Length, chunks[^1].End);
    }

    [Fact]
    public void Fixed_size_with_zero_overlap_tiles_the_document()
    {
        var doc = corpus.Get("it-faq.md");
        var chunks = new FixedSizeChunker().Split(doc, new ChunkerOptions { ChunkSize = 250, ChunkOverlap = 0 });
        for (int i = 1; i < chunks.Count; i++)
            Assert.Equal(chunks[i - 1].End, chunks[i].Start);
    }

    [Theory]
    [InlineData("api-docs.md")]
    [InlineData("benefits-guide.md")]
    public void Markdown_chunker_never_splits_code_blocks_or_tables(string documentId)
    {
        var doc = corpus.Get(documentId);
        Assert.NotEmpty(doc.Structure.AtomicBlocks);

        // 120 characters is far smaller than most blocks, so a naive splitter would cut every one of them.
        var chunks = new MarkdownHeaderChunker().Split(doc, new ChunkerOptions { ChunkSize = 120, ChunkOverlap = 10 });

        foreach (var block in doc.Structure.AtomicBlocks)
        {
            Assert.Contains(chunks, c => c.Span.Contains(block.Span));
            Assert.DoesNotContain(chunks, c => doc.Structure.AtomicBlockAt(c.Start) == block || doc.Structure.AtomicBlockAt(c.End) == block);
        }
    }

    [Fact]
    public void Recursive_chunker_does_split_tables_which_is_why_markdown_awareness_matters()
    {
        // The contrast that motivates the Markdown chunker, captured as a test so it stays true.
        var doc = corpus.Get("benefits-guide.md");
        var chunks = new RecursiveChunker().Split(doc, new ChunkerOptions { ChunkSize = 120, ChunkOverlap = 10 });
        Assert.Contains(chunks, c => doc.Structure.AtomicBlockAt(c.Start) is not null || doc.Structure.AtomicBlockAt(c.End) is not null);
    }

    [Fact]
    public void Markdown_chunks_never_cross_a_heading()
    {
        foreach (var doc in corpus.Documents)
        {
            var chunks = new MarkdownHeaderChunker().Split(doc, Small);
            Assert.All(chunks, c => Assert.Contains(doc.Structure.Sections, s => s.Span.Contains(c.Span)));
        }
    }

    [Fact]
    public void Markdown_chunker_keeps_small_sections_whole()
    {
        var doc = corpus.Get("employee-handbook.md");
        var section = doc.Structure.Sections.Single(s => s.Path == "Our Values > Own the Outcome");
        var chunks = new MarkdownHeaderChunker().Split(doc, ChunkerOptions.Default);

        Assert.Contains(chunks, c => c.Span == section.Span.Trim(doc.Text));
    }

    [Fact]
    public void Paragraph_chunker_keeps_faq_question_and_answer_together()
    {
        var doc = corpus.Get("it-faq.md");
        var chunks = new ParagraphChunker().Split(doc, ChunkerOptions.Default);
        var chunk = Assert.Single(chunks, c => c.Text.Contains("How do I reset my password?"));
        Assert.Contains("A: Go to the self-service portal", chunk.Text);
    }

    [Fact]
    public void Sentence_chunks_start_and_end_on_sentence_boundaries()
    {
        var doc = corpus.Get("employee-handbook.md");
        var sentences = SentenceSplitter.Split(doc.Text, new TextSpan(0, doc.Text.Length));
        var starts = sentences.Select(s => s.Start).ToHashSet();
        var ends = sentences.Select(s => s.End).ToHashSet();

        var chunks = new SentenceChunker().Split(doc, ChunkerOptions.Default);
        Assert.All(chunks, c =>
        {
            Assert.Contains(c.Start, starts);
            Assert.Contains(c.End, ends);
        });
    }

    [Theory]
    [InlineData("First one. Second one! Third?", new[] { "First one.", "Second one!", "Third?" })]
    [InlineData("Version 2.0 is out. Use example.com now.", new[] { "Version 2.0 is out.", "Use example.com now." })]
    [InlineData("Use e.g. this one. Done.", new[] { "Use e.g. this one.", "Done." })]
    [InlineData("**Q: Why?**\nA: Because.", new[] { "**Q: Why?**", "A: Because." })]
    public void Sentence_splitter_handles_common_cases(string input, string[] expected)
    {
        var spans = SentenceSplitter.Split(input, new TextSpan(0, input.Length));
        Assert.Equal(expected, spans.Select(s => s.Slice(input)));
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var doc = corpus.Get("it-faq.md");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FixedSizeChunker().Split(doc, new ChunkerOptions { ChunkSize = 100, ChunkOverlap = 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecursiveChunker().Split(doc, new ChunkerOptions { ChunkSize = 5 }));
    }
}
