using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.KeywordGenerator.Data;
using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>宣告在探測之前一次驗完：錯的宣告不必等前面的片語探了好幾分鐘才中止。</summary>
public sealed class PhraseDeclarationTests
{
    private static readonly IReadOnlyDictionary<string, string[]> Templates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["StatementStart"] = ["", "SELECT 1; "],
        ["DataSource"] = ["SELECT * FROM "],
    };

    [Fact]
    public void 現有的宣告全部通過()
    {
        var templates = PositionTemplates.All.ToDictionary(position => position.Position, position => position.Templates, StringComparer.OrdinalIgnoreCase);

        PhraseDeclaration.Validate(ClausePhrases.All, templates);
    }

    [Fact]
    public void 每個領域的宣告都排進探測順序()
    {
        var segments = typeof(ClausePhrases).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(ClausePhrases).Namespace && type.Name.EndsWith("Phrases", StringComparison.Ordinal) && type != typeof(ClausePhrases))
            .SelectMany(type => type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
            .Where(field => field.FieldType == typeof(PhraseDeclaration[]))
            .Sum(field => ((PhraseDeclaration[])field.GetValue(null)!).Length);

        Assert.Equal(segments, ClausePhrases.All.Length);
    }

    [Theory]
    [InlineData("...")]
    [InlineData("Lead 與 After")]
    [InlineData("不存在的位置")]
    [InlineData("Template")]
    [InlineData("不收 Expand、Values、Closed")]
    [InlineData("要以 After 交代位置")]
    [InlineData("Group")]
    [InlineData("Items")]
    [InlineData("Clause")]
    [InlineData(",* 只能是最後一項")]
    public void 錯的宣告一次全部報出來(string problem)
    {
        PhraseDeclaration[] phrases =
        [
            new("EXEC ... WITH ,*"),
            new("FOR") { Lead = "SELECT ", After = ["DataSource"] },
            new("SET") { After = ["Nowhere"] },
            new("SET") { After = ["DataSource"], Template = 1 },
            new("ALTER USER {name} WITH ,*") { Closed = false },
            new("CREATE LOGIN {name} WITH ,*") { Lead = "SELECT 1; " },
            new("OPENROWSET (*") { Group = "(a)" },
            new("OPENROWSET (*") { Items = "BULK 'x', " },
            new("OPENROWSET ()") { Clause = true },
            new("FOR XML ,* AUTO"),
        ];

        var exception = Assert.Throws<InvalidOperationException>(() => PhraseDeclaration.Validate(phrases, Templates));

        Assert.Contains(problem, exception.Message);
        Assert.StartsWith("片語宣告有 10 處錯誤", exception.Message);
    }

    [Fact]
    public void 帶Gap的片語與括號清單的Items照寫法通過()
    {
        PhraseDeclaration[] phrases =
        [
            new("DROP INDEX ... WITH (*") { Gap = "i ON t" },
            new("WAIT_AT_LOW_PRIORITY (* ABORT_AFTER_WAIT =") { Lead = "ALTER TABLE t SWITCH TO t WITH (", Items = "MAX_DURATION = 1 MINUTES, " },
            new("") { After = ["DataSource"] },
        ];

        PhraseDeclaration.Validate(phrases, Templates);
    }
}
