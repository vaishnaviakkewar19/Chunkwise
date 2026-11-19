namespace Chunkwise.Core.Chunking;

/// <summary>
/// A faithful C# port of LangChain's <c>RecursiveCharacterTextSplitter</c>
/// (langchain_text_splitters/character.py) with its defaults: <c>keep_separator=True</c> (separator
/// attached to the start of the following piece), <c>strip_whitespace=True</c> and <c>len()</c> as
/// the length function.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm: find the first separator (coarsest first: paragraph, line, word, character) that
/// occurs in the text, split on it, then greedily merge the pieces back together until adding one more
/// would exceed <c>chunkSize</c>. Any single piece that is still too big is split again with the
/// next, finer separator. The result is "as big as possible, but broken at the most natural place".
/// </para>
/// <para>
/// Unlike the Python original, which works on copied strings and has to search for each chunk to
/// recover its offset, this port works on <see cref="TextSpan"/>s. Because separators are kept, every
/// merged chunk is a contiguous slice of the input, so the offsets are exact and come for free.
/// </para>
/// </remarks>
public sealed class RecursiveCharacterTextSplitter
{
    /// <summary>LangChain's default separators: paragraph, line, word, character.</summary>
    public static readonly IReadOnlyList<string> DefaultSeparators = ["\n\n", "\n", " ", ""];

    private readonly int _chunkSize;
    private readonly int _chunkOverlap;
    private readonly IReadOnlyList<string> _separators;

    public RecursiveCharacterTextSplitter(int chunkSize, int chunkOverlap, IReadOnlyList<string>? separators = null)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (chunkOverlap < 0 || chunkOverlap > chunkSize) throw new ArgumentOutOfRangeException(nameof(chunkOverlap));

        _chunkSize = chunkSize;
        _chunkOverlap = chunkOverlap;
        _separators = separators is { Count: > 0 } ? separators : DefaultSeparators;
    }

    /// <summary>Convenience overload mirroring LangChain's <c>split_text</c>, for tests and learning.</summary>
    public IReadOnlyList<string> SplitText(string text) =>
        Split(text, new TextSpan(0, text.Length)).Select(s => s.Slice(text)).ToList();

    /// <summary>Splits the <paramref name="range"/> of <paramref name="text"/> into chunk spans.</summary>
    public IReadOnlyList<TextSpan> Split(string text, TextSpan range)
    {
        var results = new List<TextSpan>();
        SplitRecursive(text, range, _separators, results);
        return results;
    }

    private void SplitRecursive(string text, TextSpan range, IReadOnlyList<string> separators, List<TextSpan> results)
    {
        // 1. Pick the coarsest separator that actually occurs in this range.
        //    "" (split into characters) is the guaranteed last resort.
        string separator = separators[^1];
        IReadOnlyList<string> finerSeparators = [];
        for (int i = 0; i < separators.Count; i++)
        {
            var candidate = separators[i];
            if (candidate.Length == 0)
            {
                separator = candidate;
                break;
            }

            if (text.IndexOf(candidate, range.Start, range.Length, StringComparison.Ordinal) >= 0)
            {
                separator = candidate;
                finerSeparators = separators.Skip(i + 1).ToList();
                break;
            }
        }

        // 2. Split, then merge the small pieces. Pieces that are too large recurse with finer separators.
        var goodSplits = new List<TextSpan>();
        foreach (var piece in SplitKeepingSeparator(text, range, separator))
        {
            // Note the strict "<", copied from LangChain: a piece of exactly chunkSize is recursed into.
            if (piece.Length < _chunkSize)
            {
                goodSplits.Add(piece);
                continue;
            }

            if (goodSplits.Count > 0)
            {
                MergeSplits(text, goodSplits, results);
                goodSplits.Clear();
            }

            if (finerSeparators.Count == 0)
                AddStripped(text, piece, results);
            else
                SplitRecursive(text, piece, finerSeparators, results);
        }

        if (goodSplits.Count > 0)
            MergeSplits(text, goodSplits, results);
    }

    /// <summary>
    /// Equivalent of Python's <c>re.split(f"({sep})", text)</c> followed by gluing each separator onto
    /// the start of the next piece. Empty pieces are dropped, exactly as LangChain does.
    /// </summary>
    private static IEnumerable<TextSpan> SplitKeepingSeparator(string text, TextSpan range, string separator)
    {
        if (separator.Length == 0)
        {
            for (int i = range.Start; i < range.End; i++)
                yield return new TextSpan(i, i + 1);
            yield break;
        }

        int pieceStart = range.Start;
        int searchFrom = range.Start;
        while (searchFrom < range.End)
        {
            int hit = text.IndexOf(separator, searchFrom, range.End - searchFrom, StringComparison.Ordinal);
            if (hit < 0) break;

            if (hit > pieceStart)
                yield return new TextSpan(pieceStart, hit);

            // The separator becomes the beginning of the next piece.
            pieceStart = hit;
            searchFrom = hit + separator.Length;
        }

        if (range.End > pieceStart)
            yield return new TextSpan(pieceStart, range.End);
    }

    /// <summary>
    /// LangChain's <c>_merge_splits</c>. Pieces are accumulated until the next one would overflow;
    /// then a chunk is emitted and pieces are popped from the front until what remains is no larger
    /// than the overlap. Those leftover pieces start the next chunk, which is where overlap comes from.
    /// Because overlap is made of whole pieces, actual overlap is <i>at most</i> chunkOverlap.
    /// </summary>
    private void MergeSplits(string text, List<TextSpan> splits, List<TextSpan> results)
    {
        // With keep_separator=True the join separator is "", so its length is 0 and is omitted below.
        var current = new List<TextSpan>();
        int total = 0;

        foreach (var split in splits)
        {
            int length = split.Length;
            if (total + length > _chunkSize)
            {
                if (current.Count > 0)
                {
                    AddStripped(text, new TextSpan(current[0].Start, current[^1].End), results);

                    while (total > _chunkOverlap || (total + length > _chunkSize && total > 0))
                    {
                        total -= current[0].Length;
                        current.RemoveAt(0);
                    }
                }
            }

            current.Add(split);
            total += length;
        }

        if (current.Count > 0)
            AddStripped(text, new TextSpan(current[0].Start, current[^1].End), results);
    }

    private static void AddStripped(string text, TextSpan span, List<TextSpan> results)
    {
        var trimmed = span.Trim(text);
        if (!trimmed.IsEmpty)
            results.Add(trimmed);
    }
}
