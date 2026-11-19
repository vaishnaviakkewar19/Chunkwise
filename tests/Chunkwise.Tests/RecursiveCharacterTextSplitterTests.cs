using Chunkwise.Core;
using Chunkwise.Core.Chunking;

namespace Chunkwise.Tests;

public class RecursiveCharacterTextSplitterTests
{
    /// <summary>
    /// Input and expected output are taken verbatim from LangChain's own test suite
    /// (<c>test_iterative_text_splitter</c> in libs/text-splitters/tests/unit_tests/test_text_splitters.py),
    /// using <c>RecursiveCharacterTextSplitter(chunk_size=10, chunk_overlap=1)</c>.
    /// If this passes, the port behaves the same as the Python original on its reference case.
    /// </summary>
    [Fact]
    public void Matches_LangChain_reference_output()
    {
        const string text = "Hi.\n\nI'm Harrison.\n\nHow? Are? You?\nOkay then f f f f.\n" +
                            "This is a weird text to write, but gotta test the splittingggg some how.\n\n" +
                            "Bye!\n\n-H.";

        var output = new RecursiveCharacterTextSplitter(chunkSize: 10, chunkOverlap: 1).SplitText(text);

        string[] expected =
        [
            "Hi.", "I'm", "Harrison.", "How? Are?", "You?", "Okay then", "f f f f.",
            "This is a", "weird", "text to", "write,", "but gotta", "test the",
            "splitting", "gggg", "some how.", "Bye!", "-H.",
        ];
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Text_that_fits_is_returned_as_a_single_stripped_chunk()
    {
        var output = new RecursiveCharacterTextSplitter(100, 10).SplitText("  short text\n\n");
        Assert.Equal(["short text"], output);
    }

    [Fact]
    public void Overlap_is_made_of_whole_words_and_never_exceeds_the_limit()
    {
        const string text = "one two three four five six seven eight nine ten eleven twelve";
        var splitter = new RecursiveCharacterTextSplitter(chunkSize: 20, chunkOverlap: 8);

        var spans = splitter.Split(text, new TextSpan(0, text.Length));

        Assert.True(spans.Count > 2);
        for (int i = 1; i < spans.Count; i++)
        {
            int overlap = spans[i - 1].End - spans[i].Start;
            Assert.InRange(overlap, int.MinValue, 8);
            Assert.True(spans[i].Length <= 20);
        }
        // Overlap actually happens: at least one pair shares a word.
        Assert.Contains(Enumerable.Range(1, spans.Count - 1), i => spans[i - 1].Overlaps(spans[i]));
    }

    [Fact]
    public void Custom_separators_are_respected()
    {
        // Splitting only on ";" with a size that forces a split at every separator.
        var splitter = new RecursiveCharacterTextSplitter(8, 0, [";", ""]);
        Assert.Equal(["alpha", ";beta", ";gamma"], splitter.SplitText("alpha;beta;gamma"));
    }

    [Fact]
    public void Spans_point_back_into_the_original_text()
    {
        const string text = "Para one is here.\n\nPara two is a little bit longer than one.\n\nThree.";
        var spans = new RecursiveCharacterTextSplitter(25, 5).Split(text, new TextSpan(0, text.Length));
        var strings = new RecursiveCharacterTextSplitter(25, 5).SplitText(text);

        Assert.Equal(strings, spans.Select(s => s.Slice(text)));
    }
}
