using System.Text.Json;
using System.Text.Json.Serialization;
using Chunkwise.Core.Documents;

namespace Chunkwise.Core.Evaluation;

/// <summary>A test question with the exact passage that answers it.</summary>
public sealed record GoldenQuestion
{
    public required string Id { get; init; }
    public required string Question { get; init; }

    /// <summary>Document ID (file name) that contains the answer.</summary>
    public required string Document { get; init; }

    /// <summary>The answer passage, copied verbatim from the document.</summary>
    public required string Answer { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Note { get; init; }

    /// <summary>Resolved at load time by locating <see cref="Answer"/> in the document text.</summary>
    [JsonIgnore]
    public TextSpan AnswerSpan { get; init; }
}

public static class GoldenSet
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record GoldenFile(List<GoldenQuestion> Questions);

    /// <summary>Loads the questions and resolves each answer to a character span.</summary>
    /// <remarks>
    /// The JSON stores answer <i>text</i> rather than offsets because offsets break silently as soon
    /// as anyone edits the corpus. Finding the text at load time, and failing loudly if it is missing
    /// or ambiguous, turns that silent breakage into an obvious error.
    /// </remarks>
    public static IReadOnlyList<GoldenQuestion> Load(string path, IEnumerable<Document> documents)
    {
        var byId = documents.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var file = JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException($"'{path}' is empty.");

        return file.Questions.Select(q =>
        {
            if (!byId.TryGetValue(q.Document, out var doc))
                throw new InvalidDataException($"Question {q.Id} refers to unknown document '{q.Document}'.");

            var answer = q.Answer.Replace("\r\n", "\n");
            int index = doc.Text.IndexOf(answer, StringComparison.Ordinal);
            if (index < 0)
                throw new InvalidDataException($"Question {q.Id}: answer text not found in {q.Document}.");
            if (doc.Text.IndexOf(answer, index + 1, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException($"Question {q.Id}: answer text occurs more than once in {q.Document}.");

            return q with { Answer = answer, AnswerSpan = new TextSpan(index, index + answer.Length) };
        }).ToList();
    }
}
