namespace Chunkwise.Core;

/// <summary>
/// A half-open character range [Start, End) into a document's text.
/// </summary>
/// <remarks>
/// Everything in Chunkwise is expressed as spans into the *original* document rather than as copied
/// strings. That single decision is what makes the visualiser, overlap shading and the evaluation
/// "hit" test trivial: they are all just interval arithmetic on the same coordinate system.
/// </remarks>
public readonly record struct TextSpan(int Start, int End)
{
    public int Length => End - Start;

    public bool IsEmpty => End <= Start;

    /// <summary>True when the two spans share at least one character.</summary>
    public bool Overlaps(TextSpan other) => Start < other.End && other.Start < End;

    public bool Contains(TextSpan other) => Start <= other.Start && other.End <= End;

    public int OverlapLength(TextSpan other) => Math.Max(0, Math.Min(End, other.End) - Math.Max(Start, other.Start));

    /// <summary>Shrinks the span so it neither starts nor ends on whitespace. May return an empty span.</summary>
    public TextSpan Trim(string text)
    {
        int start = Start, end = End;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return new TextSpan(start, end);
    }

    public string Slice(string text) => text[Start..End];

    public override string ToString() => $"[{Start}, {End})";
}
