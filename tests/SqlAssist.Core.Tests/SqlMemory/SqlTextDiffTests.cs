using System;
using System.Linq;
using System.Threading;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlTextDiffTests
{
    private static SqlTextDiffResult Diff(string oldText, string newText, SqlTextDiffLimits? limits = null) =>
        SqlTextDiff.Compute(oldText, newText, TestContext.Current.CancellationToken, limits);

    private static string Render(SqlTextDiffResult result) => string.Join("\n", result.Lines.Select(line =>
        (line.Kind switch { SqlTextDiffLineKind.Added => "+", SqlTextDiffLineKind.Removed => "-", _ => " " }) + line.Text));

    [Fact]
    public void IdenticalTextsHaveNoChanges()
    {
        var result = Diff("SELECT *\nFROM Lib_Reader;", "SELECT *\nFROM Lib_Reader;");
        Assert.True(result.TextsEqual);
        Assert.Equal(0, result.Added + result.Removed);
        Assert.Equal(-1, result.FirstChange);
        Assert.Equal(SqlTextDiffFallback.None, result.Fallback);
        Assert.Equal(new int?[] { 1, 2 }, result.Lines.Select(line => line.NewNumber));
    }

    [Fact]
    public void MarksInsertedDeletedAndReplacedLinesWithBothLineNumbers()
    {
        var result = Diff(
            "SELECT CopyNo\nFROM Cat_BookCopy\nWHERE CopyNo > 0\nORDER BY CopyNo;",
            "SELECT CopyNo, Branch\nFROM Cat_BookCopy\nORDER BY CopyNo;\nGO");
        Assert.Equal("-SELECT CopyNo\n+SELECT CopyNo, Branch\n FROM Cat_BookCopy\n-WHERE CopyNo > 0\n ORDER BY CopyNo;\n+GO", Render(result));
        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.FirstChange);
        var from = result.Lines[2];
        Assert.Equal((2, 2), (from.OldNumber!.Value, from.NewNumber!.Value));
        var go = result.Lines[5];
        Assert.Null(go.OldNumber);
        Assert.Equal(4, go.NewNumber);
    }

    [Fact]
    public void ProducesAMinimalScriptForScatteredEdits()
    {
        var old = string.Join("\n", Enumerable.Range(0, 200).Select(i => "SELECT " + i + " FROM Loan;"));
        var changed = Enumerable.Range(0, 200).Select(i => "SELECT " + i + " FROM Loan;").ToList();
        changed[20] = "SELECT 20 FROM LoanDetail;";
        changed.RemoveAt(100);
        changed.Insert(150, "SELECT NULL FROM Copy;");
        var result = Diff(old, string.Join("\n", changed));
        Assert.Equal(SqlTextDiffFallback.None, result.Fallback);
        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.Removed);
        Assert.Equal(20, result.FirstChange);
        // 逐行重組兩側必須還原成原文的行。
        Assert.Equal(SqlTextDiff.SplitLines(old), result.Lines.Where(l => l.Kind != SqlTextDiffLineKind.Added).Select(l => l.Text));
        Assert.Equal(changed, result.Lines.Where(l => l.Kind != SqlTextDiffLineKind.Removed).Select(l => l.Text));
    }

    [Fact]
    public void EmptySidesAreWholeInsertionsOrDeletions()
    {
        Assert.Equal("+SELECT 1;", Render(Diff("", "SELECT 1;")));
        Assert.Equal("-SELECT 1;", Render(Diff("SELECT 1;", "")));
        Assert.Empty(Diff("", "").Lines);
    }

    [Fact]
    public void LineEndingOnlyDifferencesAreExplainedInsteadOfCalledEqual()
    {
        var result = Diff("SELECT 1;\r\nGO\r\n", "SELECT 1;\nGO");
        Assert.False(result.TextsEqual);
        Assert.True(result.OnlyLineEndingsDiffer);
        Assert.Equal(" SELECT 1;\n GO", Render(result));
    }

    [Fact]
    public void SplitsOnEveryLineBreakKindWithoutTrailingEmptyLine()
    {
        Assert.Equal(new[] { "a", "b", "", "c" }, SqlTextDiff.SplitLines("a\r\nb\r\rc"));
        Assert.Equal(new[] { "a", "" }, SqlTextDiff.SplitLines("a\n\n"));
        Assert.Empty(SqlTextDiff.SplitLines(""));
    }

    [Fact]
    public void ComparisonIsOrdinalAndKeepsWhitespace()
    {
        var result = Diff("select 1;", "SELECT  1;");
        Assert.Equal("-select 1;\n+SELECT  1;", Render(result));
    }

    [Fact]
    public void OversizedInputFallsBackToWholeMiddleReplacementButKeepsCommonEdges()
    {
        var limits = new SqlTextDiffLimits(maxLines: 2);
        var result = Diff("-- head\na\nb\nc\n-- tail", "-- head\nx\ny\nz\n-- tail", limits: limits);
        Assert.Equal(SqlTextDiffFallback.TooLarge, result.Fallback);
        Assert.Equal(" -- head\n-a\n-b\n-c\n+x\n+y\n+z\n -- tail", Render(result));

        var characters = Diff("SELECT 1;", "SELECT 2;", limits: new SqlTextDiffLimits(maxCharacters: 4));
        Assert.Equal(SqlTextDiffFallback.TooLarge, characters.Fallback);
    }

    [Fact]
    public void TooManyChangesStopsAtTheEditDistanceBound()
    {
        var old = string.Join("\n", Enumerable.Range(0, 50).Select(i => "a" + i));
        var changed = string.Join("\n", Enumerable.Range(0, 50).Select(i => "b" + i));
        var result = Diff(old, changed, limits: new SqlTextDiffLimits(maxEditDistance: 10));
        Assert.Equal(SqlTextDiffFallback.TooManyChanges, result.Fallback);
        Assert.Equal(50, result.Added);
        Assert.Equal(50, result.Removed);
        Assert.Equal(SqlTextDiffFallback.None, Diff(old, changed, limits: new SqlTextDiffLimits(maxEditDistance: 100)).Fallback);
    }

    [Fact]
    public void LargeRealisticScriptDiffsWithinDefaultLimits()
    {
        var lines = Enumerable.Range(0, 15_000).Select(i => "INSERT INTO Loan VALUES (" + i + ");").ToList();
        var changed = lines.ToList();
        for (var i = 0; i < changed.Count; i += 1_000) changed[i] = "-- " + changed[i];
        var result = Diff(string.Join("\n", lines), string.Join("\n", changed));
        Assert.Equal(SqlTextDiffFallback.None, result.Fallback);
        Assert.Equal(15, result.Added);
        Assert.Equal(15, result.Removed);
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => SqlTextDiff.Compute("a", "b", source.Token, null));
    }

    [Fact]
    public void RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(() => Diff(null!, ""));
        Assert.Throws<ArgumentNullException>(() => Diff("", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlTextDiffLimits(maxEditDistance: -1));
    }
}
