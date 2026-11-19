namespace Chunkwise.Core.Chunking;

/// <summary>
/// A small rule-based sentence segmenter. A sentence ends at a newline, or at <c>. ! ?</c> (optionally
/// followed by closing quotes, brackets, backticks or <c>**</c>) when the next word does not start with
/// a lowercase letter.
/// </summary>
/// <remarks>
/// Real segmenters (spaCy, ICU) handle abbreviations far better; this one is good enough for the
/// corpus and has no dependencies. Treating every newline as a boundary means table rows, code lines
/// and FAQ "Q:"/"A:" lines each count as a sentence, which suits Markdown.
/// </remarks>
public static class SentenceSplitter
{
    private const string Closers = "\"')]`*";

    public static List<TextSpan> Split(string text, TextSpan range)
    {
        var sentences = new List<TextSpan>();
        int segmentStart = range.Start;

        for (int i = range.Start; i < range.End; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                Add(text, new TextSpan(segmentStart, i), sentences);
                segmentStart = i + 1;
                continue;
            }

            if (c is not ('.' or '!' or '?')) continue;

            int j = i + 1;
            while (j < range.End && Closers.Contains(text[j])) j++;

            // "3.5" or "example.com" have no whitespace after the dot, so they never end a sentence.
            if (j < range.End && !char.IsWhiteSpace(text[j])) continue;

            // "e.g. something": a lowercase continuation strongly suggests we are mid-sentence.
            int next = j;
            while (next < range.End && text[next] is ' ' or '\t') next++;
            if (next < range.End && char.IsLower(text[next])) continue;

            Add(text, new TextSpan(segmentStart, j), sentences);
            segmentStart = j;
            i = j - 1;
        }

        Add(text, new TextSpan(segmentStart, range.End), sentences);
        return sentences;
    }

    private static void Add(string text, TextSpan span, List<TextSpan> sentences)
    {
        var trimmed = span.Trim(text);
        if (!trimmed.IsEmpty) sentences.Add(trimmed);
    }
}
