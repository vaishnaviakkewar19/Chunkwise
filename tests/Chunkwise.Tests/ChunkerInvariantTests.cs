using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;

namespace Chunkwise.Tests;

/// <summary>
/// Properties that must hold for <b>every</b> chunker on <b>every</b> corpus document at several sizes.
/// New strategies get these checks automatically by being added to <see cref="Chunkers"/>.
/// </summary>
[Collection(CorpusCollection.Name)]
public class ChunkerInvariantTests(CorpusFixture corpus)
{
    public static IEnumerable<IChunker> Chunkers() => ChunkerRegistry.CreateDefault().All;

    public static TheoryData<string, int, int> Cases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var chunker in Chunkers())
            foreach (var (size, overlap) in new[] { (150, 0), (300, 30), (500, 50), (1000, 200) })
                data.Add(chunker.Name, size, overlap);
        return data;
    }

    private static IChunker Get(string name) => Chunkers().Single(c => c.Name == name);

    private IEnumerable<(Document Doc, IReadOnlyList<Chunk> Chunks)> Run(string strategy, int size, int overlap)
    {
        var options = new ChunkerOptions { ChunkSize = size, ChunkOverlap = overlap };
        return corpus.Documents.Select(d => (d, Get(strategy).Split(d, options)));
    }

    [Theory, MemberData(nameof(Cases))]
    public void No_chunk_is_empty_or_whitespace(string strategy, int size, int overlap)
    {
        foreach (var (_, chunks) in Run(strategy, size, overlap))
        {
            Assert.NotEmpty(chunks);
            Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text), c.Id));
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void Offsets_match_text_and_are_ordered(string strategy, int size, int overlap)
    {
        foreach (var (doc, chunks) in Run(strategy, size, overlap))
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                Assert.Equal(doc.Text[c.Start..c.End], c.Text);
                Assert.Equal(i, c.Index);
                if (i > 0) Assert.True(chunks[i - 1].Start <= c.Start, $"{c.Id} starts before its predecessor");
            }
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void No_text_is_lost(string strategy, int size, int overlap)
    {
        // Every non-whitespace character must land in at least one chunk; otherwise some facts
        // in the knowledge base could never be retrieved, whatever the question.
        foreach (var (doc, chunks) in Run(strategy, size, overlap))
        {
            var covered = new bool[doc.Text.Length];
            foreach (var c in chunks)
                for (int i = c.Start; i < c.End; i++) covered[i] = true;

            int lost = Enumerable.Range(0, doc.Text.Length).FirstOrDefault(i => !covered[i] && !char.IsWhiteSpace(doc.Text[i]), -1);
            Assert.True(lost < 0, $"{strategy} lost text in {doc.Id} at offset {lost}");
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void Max_size_is_respected(string strategy, int size, int overlap)
    {
        foreach (var (doc, chunks) in Run(strategy, size, overlap))
        {
            foreach (var c in chunks.Where(c => c.Length > size))
            {
                // The only permitted exception: the Markdown chunker keeping an oversized code block
                // or table in one piece (see MarkdownHeaderChunker remarks).
                Assert.True(strategy == "markdown", $"{c.Id} is {c.Length} chars, limit {size}");
                Assert.Contains(doc.Structure.AtomicBlocks, b => c.Span.Contains(b.Span) && b.Span.Length > size);
            }
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void Overlap_never_exceeds_the_configured_amount(string strategy, int size, int overlap)
    {
        foreach (var (_, chunks) in Run(strategy, size, overlap))
            for (int i = 1; i < chunks.Count; i++)
                Assert.True(chunks[i - 1].End - chunks[i].Start <= overlap,
                    $"{chunks[i].Id} overlaps its predecessor by {chunks[i - 1].End - chunks[i].Start} (limit {overlap})");
    }

    [Theory, MemberData(nameof(Cases))]
    public void Section_path_matches_the_chunk_start(string strategy, int size, int overlap)
    {
        foreach (var (doc, chunks) in Run(strategy, size, overlap))
            Assert.All(chunks, c => Assert.Equal(doc.Structure.SectionPathAt(c.Span.Trim(doc.Text).Start), c.SectionPath));
    }
}
