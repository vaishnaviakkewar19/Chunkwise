using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Chunking;

/// <summary>
/// Packs whole sentences into chunks of up to <c>ChunkSize</c> characters, carrying trailing sentences
/// (up to <c>ChunkOverlap</c> characters) into the next chunk. Chunks never end mid-sentence, but
/// sentence packing ignores headings, so a chunk can still straddle two sections.
/// </summary>
public sealed class SentenceChunker : IChunker
{
    public string Name => "sentence";
    public string DisplayName => "Sentence";
    public string Description => "Packs whole sentences up to the size limit, with sentence-level overlap.";

    public IReadOnlyList<Chunk> Split(Document document, ChunkerOptions options)
    {
        options.Validate();
        var text = document.Text;
        var fallback = new RecursiveCharacterTextSplitter(options.ChunkSize, options.ChunkOverlap, options.Separators);

        // A single sentence longer than the limit (a long code line, a wide table row) is pre-split
        // so the max-size guarantee still holds.
        var units = new List<TextSpan>();
        foreach (var sentence in SentenceSplitter.Split(text, new TextSpan(0, text.Length)))
        {
            if (sentence.Length <= options.ChunkSize) units.Add(sentence);
            else units.AddRange(fallback.Split(text, sentence));
        }

        return ChunkBuilder.Build(document, Name, Pack(units, options.ChunkSize, options.ChunkOverlap));
    }

    /// <summary>
    /// Greedy packing of ordered spans. A chunk's length runs from its first unit's start to its last
    /// unit's end, so the whitespace between sentences counts towards the limit.
    /// </summary>
    internal static List<TextSpan> Pack(IReadOnlyList<TextSpan> units, int maxSize, int overlap)
    {
        var chunks = new List<TextSpan>();
        int first = 0;   // index of the first unit in the current chunk

        while (first < units.Count)
        {
            int last = first;
            while (last + 1 < units.Count && units[last + 1].End - units[first].Start <= maxSize)
                last++;

            chunks.Add(new TextSpan(units[first].Start, units[last].End));
            if (last == units.Count - 1) break;

            // Walk back from the end of this chunk to find how many trailing units fit in the overlap.
            // Always advance at least one unit, otherwise a large overlap could loop forever.
            int nextFirst = last + 1;
            while (nextFirst - 1 > first && units[last].End - units[nextFirst - 1].Start <= overlap)
                nextFirst--;
            first = nextFirst;
        }

        return chunks;
    }
}
