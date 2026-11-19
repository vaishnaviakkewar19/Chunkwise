using System.Globalization;
using System.Text;
using Chunkwise.Core.Evaluation;

namespace Chunkwise.Cli;

/// <summary>Renders leaderboard reports as a coloured console table or as a Markdown table for the README.</summary>
public static class LeaderboardPrinter
{
    private sealed record Column(string Header, Func<StrategyReport, double> Value, string Format, bool HigherIsBetter, bool Ranked);

    private static readonly Column[] Columns =
    [
        new("Chunks", r => r.ChunkCount, "0", false, Ranked: false),
        new("Avg size", r => r.AverageChunkLength, "0", false, Ranked: false),
        new("Returned", r => r.AverageRetrievedLength, "0", false, Ranked: false),
        new("Recall@1", r => r.RecallAt1, "0.00", true, Ranked: true),
        new("Recall@3", r => r.RecallAt3, "0.00", true, Ranked: true),
        new("MRR", r => r.Mrr, "0.00", true, Ranked: true),
        new("Coverage@3", r => r.CoverageAt3, "0.00", true, Ranked: true),
    ];

    private static string Format(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// A value counts as "best" if it equals the column maximum after rounding to the displayed precision,
    /// so two strategies that both show 0.83 are both highlighted.
    /// </summary>
    private static bool IsBest(Column column, StrategyReport report, IReadOnlyList<StrategyReport> all) =>
        column.Ranked && Format(column.Value(report), column.Format) == Format(all.Max(column.Value), column.Format);

    public static void PrintConsole(IReadOnlyList<StrategyReport> reports)
    {
        int labelWidth = Math.Max("Strategy".Length, reports.Max(r => r.Label.Length));
        int[] widths = Columns.Select(c => Math.Max(c.Header.Length, 6)).ToArray();

        Console.Write("Strategy".PadRight(labelWidth));
        for (int i = 0; i < Columns.Length; i++) Console.Write("  " + Columns[i].Header.PadLeft(widths[i]));
        Console.WriteLine();
        Console.WriteLine(new string('-', labelWidth + widths.Sum(w => w + 2)));

        foreach (var report in reports)
        {
            Console.Write(report.Label.PadRight(labelWidth));
            for (int i = 0; i < Columns.Length; i++)
            {
                var column = Columns[i];
                var text = Format(column.Value(report), column.Format).PadLeft(widths[i]);
                Console.Write("  ");
                if (IsBest(column, report, reports))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(text);
                    Console.ResetColor();
                }
                else
                {
                    Console.Write(text);
                }
            }

            // A small inline bar makes the Recall@3 ranking readable at a glance.
            Console.Write("  " + new string('█', (int)Math.Round(report.RecallAt3 * 20)));
            Console.WriteLine();
        }
    }

    public static string ToMarkdown(IReadOnlyList<StrategyReport> reports)
    {
        var sb = new StringBuilder();
        sb.Append("| Strategy | ").Append(string.Join(" | ", Columns.Select(c => c.Header))).AppendLine(" |");
        sb.Append("|---|").Append(string.Concat(Columns.Select(_ => "---:|"))).AppendLine();
        foreach (var report in reports)
        {
            sb.Append("| ").Append(report.Label);
            foreach (var column in Columns)
            {
                var text = Format(column.Value(report), column.Format);
                sb.Append(" | ").Append(IsBest(column, report, reports) ? $"**{text}**" : text);
            }
            sb.AppendLine(" |");
        }
        return sb.ToString();
    }

    /// <summary>A question-by-strategy grid of first-hit ranks: the quickest way to see <i>why</i> a strategy wins.</summary>
    public static void PrintQuestionGrid(IReadOnlyList<StrategyReport> reports)
    {
        Console.WriteLine();
        Console.WriteLine("First-hit rank per question (- = not in top 10)");
        var codes = reports.Select((r, i) => $"S{i + 1}").ToList();
        for (int i = 0; i < reports.Count; i++)
            Console.WriteLine($"  {codes[i]} = {reports[i].Label}");

        Console.WriteLine();
        Console.Write("Question".PadRight(10));
        foreach (var code in codes) Console.Write(code.PadLeft(4));
        Console.WriteLine();

        foreach (var question in reports[0].Questions)
        {
            Console.Write(question.QuestionId.PadRight(10));
            foreach (var report in reports)
            {
                var rank = report.Questions.Single(q => q.QuestionId == question.QuestionId).FirstHitRank;
                Console.ForegroundColor = rank switch { 1 => ConsoleColor.Green, <= 3 => ConsoleColor.Yellow, _ => ConsoleColor.DarkGray };
                Console.Write((rank?.ToString(CultureInfo.InvariantCulture) ?? "-").PadLeft(4));
                Console.ResetColor();
            }
            Console.WriteLine($"  {question.Question}");
        }
    }
}
