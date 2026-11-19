using Chunkwise.Core.Documents;
using Chunkwise.Core.Evaluation;

namespace Chunkwise.Tests;

/// <summary>Loads the real corpus once per test run; it is copied next to the test assembly by the csproj.</summary>
public sealed class CorpusFixture
{
    public CorpusFixture()
    {
        CorpusPath = CorpusLocator.Find();
        Documents = DocumentLoader.LoadDirectory(CorpusPath);
        Questions = GoldenSet.Load(Path.Combine(CorpusPath, CorpusLocator.GoldenQuestionsFileName), Documents);
    }

    public string CorpusPath { get; }
    public IReadOnlyList<Document> Documents { get; }
    public IReadOnlyList<GoldenQuestion> Questions { get; }

    public Document Get(string id) => Documents.Single(d => d.Id == id);
}

[CollectionDefinition(Name)]
public sealed class CorpusCollection : ICollectionFixture<CorpusFixture>
{
    public const string Name = "corpus";
}
