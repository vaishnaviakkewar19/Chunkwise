using System.Text;
using System.Text.Json;
using Chunkwise.Cli;
using Chunkwise.Core.Chunking;
using Chunkwise.Core.Documents;
using Chunkwise.Core.Embeddings;
using Chunkwise.Core.Evaluation;
using Chunkwise.Core.Retrieval;

Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
    Chunkwise: compare chunking strategies for RAG.

    Usage:
      chunkwise eval  [options]                  Run the golden questions and print the leaderboard (default)
      chunkwise ask   "<question>" [options]     Show the top 3 chunks from every strategy
      chunkwise chunks <document> <strategy>     Print the chunks one strategy produces for one document
      chunkwise help

    Options:
      --size <n>            Chunk size in characters          (default 500)
      --overlap <n>         Overlap in characters             (default 50)
      --semantic-threshold <p>  Semantic: cut at the lowest p of boundary similarities (default 0.25)
      --parent-size <n>     Parent-child: maximum parent size (default 1500)
      --enrich on|off|both  Prepend "document > section:" before embedding (default both)
      --strategy <name>     Only run one strategy
      --embedder hashing|ollama                              (default hashing)
      --ollama-url <url>    (default http://localhost:11434)
      --ollama-model <m>    (default nomic-embed-text)
      --corpus <dir>        Folder with the .md files and golden-questions.json
      --markdown            Also print the leaderboard as a Markdown table
      --details             Also print the per-question rank grid
      --json <file>         Write the full reports (including per-question ranks) as JSON
    """;

try
{
    var cli = new CliArgs(args);
    if (cli.Command is "help" or "-h" or "--help" || cli.Has("help"))
    {
        Console.WriteLine(Usage);
        return 0;
    }

    var corpusPath = cli.Get("corpus") ?? CorpusLocator.Find(Directory.GetCurrentDirectory());
    var documents = DocumentLoader.LoadDirectory(corpusPath);
    var questions = GoldenSet.Load(Path.Combine(corpusPath, CorpusLocator.GoldenQuestionsFileName), documents);

    var options = new ChunkerOptions
    {
        ChunkSize = cli.GetInt("size", 500),
        ChunkOverlap = cli.GetInt("overlap", 50),
        SemanticThreshold = cli.GetDouble("semantic-threshold", ChunkerOptions.Default.SemanticThreshold),
        ParentChunkSize = cli.GetInt("parent-size", ChunkerOptions.Default.ParentChunkSize),
    };
    options.Validate();

    // The cache lives next to corpus/ (the repo root) so the CLI and web app share one cache.
    var repoRoot = Path.GetDirectoryName(Path.GetFullPath(corpusPath))!;
    var embedder = EmbedderFactory.Create(new EmbedderSettings
    {
        Provider = cli.Get("embedder", "hashing"),
        OllamaUrl = cli.Get("ollama-url", "http://localhost:11434"),
        OllamaModel = cli.Get("ollama-model", "nomic-embed-text"),
    }, repoRoot);

    var registry = ChunkerRegistry.CreateDefault();
    var chunkers = cli.Get("strategy") is { } only ? [registry.Get(only)] : registry.All;
    bool[] enrichModes = cli.Get("enrich", "both").ToLowerInvariant() switch
    {
        "on" => [true],
        "off" => [false],
        "both" => [false, true],
        var other => throw new ArgumentException($"--enrich expects on, off or both, got '{other}'."),
    };

    return cli.Command switch
    {
        "eval" => await EvalAsync(),
        "ask" => await AskAsync(),
        "chunks" => await PrintChunksAsync(),
        _ => Fail($"Unknown command '{cli.Command}'.\n\n{Usage}"),
    };

    async Task<int> EvalAsync()
    {
        Console.WriteLine($"Corpus: {documents.Count} documents, {questions.Count} golden questions");
        Console.WriteLine($"Embedder: {embedder.ModelId} | chunk size {options.ChunkSize} | overlap {options.ChunkOverlap}");
        Console.WriteLine();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var evaluator = new Evaluator(embedder);
        var reports = await evaluator.RunLeaderboardAsync(chunkers, documents, questions, options, enrichModes,
            new Progress<string>(name => Console.Error.WriteLine($"  evaluating {name}...")));
        Console.Error.WriteLine($"  done in {started.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine();

        LeaderboardPrinter.PrintConsole(reports);
        if (cli.Has("details")) LeaderboardPrinter.PrintQuestionGrid(reports);
        if (cli.Has("markdown"))
        {
            Console.WriteLine();
            Console.WriteLine(LeaderboardPrinter.ToMarkdown(reports));
        }

        if (cli.Get("json") is { } jsonPath)
        {
            var summary = reports.Select(r => new
            {
                r.Strategy, r.Label, r.Enriched, r.ChunkCount, r.AverageChunkLength, r.AverageRetrievedLength, r.RecallAt1, r.RecallAt3, r.Mrr, r.CoverageAt3,
                Questions = r.Questions.Select(q => new { q.QuestionId, q.FirstHitRank, q.CoverageAt3 }),
            });
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Wrote {jsonPath}");
        }
        return 0;
    }

    async Task<int> AskAsync()
    {
        var question = string.Join(' ', cli.Positional);
        if (string.IsNullOrWhiteSpace(question)) return Fail("Usage: chunkwise ask \"<question>\"");

        foreach (var chunker in chunkers)
        {
            foreach (var enrich in enrichModes)
            {
                var index = await StrategyIndex.BuildAsync(chunker, documents, options, enrich, embedder);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"== {chunker.DisplayName}{(enrich ? " + context" : "")} ==");
                Console.ResetColor();
                foreach (var hit in await index.SearchAsync(question, 3))
                {
                    var preview = hit.Chunk.Text.ReplaceLineEndings(" ");
                    if (preview.Length > 110) preview = preview[..110] + "...";
                    Console.WriteLine($"  #{hit.Rank} {hit.Score:0.000}  {hit.Chunk.DocumentId} > {hit.Chunk.SectionPath}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      {preview}");
                    Console.ResetColor();
                }
            }
        }
        return 0;
    }

    async Task<int> PrintChunksAsync()
    {
        if (cli.Positional.Count < 2) return Fail("Usage: chunkwise chunks <document> <strategy>");
        var document = documents.FirstOrDefault(d => d.Id.Equals(cli.Positional[0], StringComparison.OrdinalIgnoreCase)
                                                    || Path.GetFileNameWithoutExtension(d.Id).Equals(cli.Positional[0], StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"Unknown document '{cli.Positional[0]}'. Known: {string.Join(", ", documents.Select(d => d.Id))}.");
        var chunks = await registry.Get(cli.Positional[1]).SplitAsync(document, options, embedder);

        foreach (var chunk in chunks)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            var parent = chunk.ParentSpan is { } p ? $"  -> parent {p.Start}-{p.End}" : "";
            Console.WriteLine($"[{chunk.Index}] {chunk.Start}-{chunk.End} ({chunk.Length} chars)  {chunk.SectionPath}{parent}");
            Console.ResetColor();
            Console.WriteLine(chunk.Text);
            Console.WriteLine();
        }
        Console.WriteLine($"{chunks.Count} chunks, average {chunks.Average(c => c.Length):0} chars");
        return 0;
    }
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException or InvalidDataException or KeyNotFoundException)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine(message);
    Console.ResetColor();
    return 1;
}
