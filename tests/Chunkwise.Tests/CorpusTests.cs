using Chunkwise.Core.Documents;

namespace Chunkwise.Tests;

/// <summary>Guards the corpus and golden set: if these fail, evaluation numbers are meaningless.</summary>
[Collection(CorpusCollection.Name)]
public class CorpusTests(CorpusFixture corpus)
{
    [Fact]
    public void Corpus_has_the_four_documents_with_titles()
    {
        Assert.Equal(
            ["api-docs.md", "benefits-guide.md", "employee-handbook.md", "it-faq.md"],
            corpus.Documents.Select(d => d.Id));
        Assert.Equal("Brightwell Health Benefits Guide", corpus.Get("benefits-guide.md").Title);
    }

    [Fact]
    public void Golden_set_has_25_to_30_questions_with_unique_ids()
    {
        Assert.InRange(corpus.Questions.Count, 25, 30);
        Assert.Equal(corpus.Questions.Count, corpus.Questions.Select(q => q.Id).Distinct().Count());
    }

    [Fact]
    public void Every_answer_resolves_to_its_exact_passage()
    {
        // GoldenSet.Load already throws on missing/ambiguous answers; this double-checks the spans.
        foreach (var q in corpus.Questions)
            Assert.Equal(q.Answer, corpus.Get(q.Document).Text[q.AnswerSpan.Start..q.AnswerSpan.End]);
    }

    [Theory]
    [InlineData("multi-paragraph")]
    [InlineData("coreference")]
    [InlineData("no-keyword-overlap")]
    [InlineData("table")]
    [InlineData("code")]
    public void Golden_set_contains_the_tricky_cases(string tag) =>
        Assert.Contains(corpus.Questions, q => q.Tags.Contains(tag));

    [Fact]
    public void Multi_paragraph_answer_really_spans_a_blank_line() =>
        Assert.Contains("\n\n", corpus.Questions.Single(q => q.Tags.Contains("multi-paragraph")).Answer);

    [Fact]
    public void Line_endings_are_normalised()
    {
        var doc = DocumentLoader.FromText("x.md", "# T\r\n\r\nBody\r\n");
        Assert.Equal("# T\n\nBody\n", doc.Text);
    }
}

public class MarkdownStructureTests
{
    private const string Sample = """
        # Title

        Intro text.

        ## Alpha

        Alpha body.

        ```bash
        # not a heading
        echo hi
        ```

        ### Alpha Child

        | a | b |
        |---|---|
        | 1 | 2 |

        ## Beta

        Beta body.
        """;

    private static readonly MarkdownStructure Structure = MarkdownStructure.Parse(Sample);

    [Fact]
    public void Headings_inside_code_fences_are_ignored() =>
        Assert.Equal(["Title", "Alpha", "Alpha Child", "Beta"], Structure.Headings.Select(h => h.Text));

    [Fact]
    public void Section_paths_form_a_breadcrumb_without_the_title()
    {
        Assert.Equal("", Structure.SectionPathAt(Sample.IndexOf("Intro")));
        Assert.Equal("Alpha", Structure.SectionPathAt(Sample.IndexOf("Alpha body")));
        Assert.Equal("Alpha > Alpha Child", Structure.SectionPathAt(Sample.IndexOf("| 1 |")));
        Assert.Equal("Beta", Structure.SectionPathAt(Sample.IndexOf("Beta body")));
    }

    [Fact]
    public void Code_fences_and_tables_are_detected_as_atomic_blocks()
    {
        Assert.Collection(Structure.AtomicBlocks,
            code =>
            {
                Assert.Equal(BlockKind.CodeFence, code.Kind);
                Assert.StartsWith("```bash", code.Span.Slice(Sample));
                Assert.EndsWith("```", code.Span.Slice(Sample));
            },
            table =>
            {
                Assert.Equal(BlockKind.Table, table.Kind);
                Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |", table.Span.Slice(Sample).Replace("\r", ""));
            });
    }

    [Fact]
    public void Real_corpus_blocks_are_counted_correctly()
    {
        var api = DocumentLoader.LoadFile(Path.Combine(CorpusLocator.Find(), "api-docs.md")).Structure;
        Assert.Equal(7, api.AtomicBlocks.Count(b => b.Kind == BlockKind.CodeFence));
        Assert.Equal(2, api.AtomicBlocks.Count(b => b.Kind == BlockKind.Table));

        var benefits = DocumentLoader.LoadFile(Path.Combine(CorpusLocator.Find(), "benefits-guide.md")).Structure;
        Assert.Equal(3, benefits.AtomicBlocks.Count(b => b.Kind == BlockKind.Table));
    }

    [Fact]
    public void Remote_work_stipend_paragraph_knows_its_heading()
    {
        // This is the "This policy..." co-reference case: the sentence itself never names the stipend.
        var doc = DocumentLoader.LoadFile(Path.Combine(CorpusLocator.Find(), "employee-handbook.md"));
        int offset = doc.Text.IndexOf("This policy provides", StringComparison.Ordinal);
        Assert.Equal("Working Hours and Location > Remote Work Stipend", doc.Structure.SectionPathAt(offset));
    }
}
