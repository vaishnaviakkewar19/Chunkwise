namespace Chunkwise.Core.Embeddings;

/// <summary>Configuration for choosing an embedder. Bound from appsettings.json in the web app and from flags in the CLI.</summary>
public sealed record EmbedderSettings
{
    /// <summary><c>hashing</c> (default, offline) or <c>ollama</c>.</summary>
    public string Provider { get; init; } = "hashing";

    public string OllamaUrl { get; init; } = "http://localhost:11434";

    public string OllamaModel { get; init; } = "nomic-embed-text";

    /// <summary>
    /// Where cached vectors are written. A relative path is resolved against the <c>baseDirectory</c>
    /// passed to <see cref="EmbedderFactory.Create"/> (the repo root), not the current directory.
    /// </summary>
    public string CacheDirectory { get; init; } = ".chunkwise-cache";
}

public static class EmbedderFactory
{
    /// <param name="baseDirectory">
    /// Anchor for a relative <see cref="EmbedderSettings.CacheDirectory"/>. Without it the CLI and the
    /// web app would each get their own cache, because <c>dotnet run --project</c> starts the web app in
    /// its project folder, and the second one would re-embed everything the first already paid for.
    /// </param>
    public static IEmbedder Create(EmbedderSettings settings, string? baseDirectory = null) => settings.Provider.ToLowerInvariant() switch
    {
        // Hashing is microseconds per text, so caching it would only add disk I/O.
        "hashing" => new HashingEmbedder(),
        "ollama" => new CachingEmbedder(
            new OllamaEmbedder(new Uri(settings.OllamaUrl.TrimEnd('/') + "/"), settings.OllamaModel),
            Path.GetFullPath(settings.CacheDirectory, baseDirectory ?? Directory.GetCurrentDirectory())),
        _ => throw new ArgumentException($"Unknown embedding provider '{settings.Provider}'. Use 'hashing' or 'ollama'."),
    };
}
