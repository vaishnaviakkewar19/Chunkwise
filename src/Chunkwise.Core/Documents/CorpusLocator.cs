namespace Chunkwise.Core.Documents;

/// <summary>Finds the <c>corpus</c> folder so the CLI, web app and tests all run with zero configuration.</summary>
public static class CorpusLocator
{
    public const string GoldenQuestionsFileName = "golden-questions.json";

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> (default: the app's base directory) looking for a
    /// <c>corpus</c> folder that contains the golden questions. Walking up means
    /// <c>dotnet run</c> works from the repo root, from a project folder, or from <c>bin/Debug</c>.
    /// </summary>
    public static string Find(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "corpus");
            if (File.Exists(Path.Combine(candidate, GoldenQuestionsFileName)))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find a 'corpus' folder containing {GoldenQuestionsFileName} above '{startDirectory ?? AppContext.BaseDirectory}'.");
    }
}
