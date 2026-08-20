using System;
using System.IO;
using System.Linq;
using SqlAssist.Core;

var expander = new SqlSnippetExpander();
var passed = 0;

AssertExpansion("ssf", "SELECT * FROM ", ExpansionKind.Snippet);
AssertExpansion("SELECT ssf", "SELECT * FROM ", ExpansionKind.Snippet, replacementStart: 7);
AssertExpansion("ap", "ALTER PROCEDURE ", ExpansionKind.Snippet);
AssertExpansion("af", "ALTER FUNCTION ", ExpansionKind.Snippet);
AssertExpansion("select", "SELECT", ExpansionKind.Keyword);
AssertNoExpansion("SELECT");
AssertNoExpansion("SELECT 'ssf");
AssertNoExpansion("-- ssf");
AssertNoExpansion("/* ssf");
AssertExpansion("-- 註解\r\nssf", "SELECT * FROM ", ExpansionKind.Snippet, replacementStart: 7);
TestSettingsPersistence();
TestSuggestionFlow();

Console.WriteLine($"核心測試通過：{passed} 項");
return;

void AssertExpansion(
    string input,
    string expectedText,
    ExpansionKind expectedKind,
    int? replacementStart = null)
{
    if (!expander.TryExpand(input, out var result) || result is null)
    {
        throw new InvalidOperationException($"預期 '{input}' 可以展開，但實際未展開。");
    }

    if (result.ReplacementText != expectedText ||
        result.Kind != expectedKind ||
        result.ReplacementStart != (replacementStart ?? 0))
    {
        throw new InvalidOperationException(
            $"'{input}' 展開結果不符：{result.ReplacementText}, {result.Kind}, {result.ReplacementStart}");
    }

    passed++;
}

void AssertNoExpansion(string input)
{
    if (expander.TryExpand(input, out _))
    {
        throw new InvalidOperationException($"預期 '{input}' 不展開，但實際已展開。");
    }

    passed++;
}

void TestSettingsPersistence()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SqlAssist.Tests.{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "settings.json");

    try
    {
        var service = new SettingsService(path);
        var defaults = service.GetSnapshot();

        if (!defaults.Enabled || !defaults.Features.TabExpansion || defaults.DiagnosticsEnabled ||
            defaults.Suggestions.TriggerAfterCharacters != 1 || !defaults.Suggestions.ShowPreview ||
            defaults.Suggestions.QualifyObjectNames || defaults.Suggestions.UseSquareBrackets)
        {
            throw new InvalidOperationException("設定預設值不正確。");
        }

        service.ToggleEnabled();
        service.ToggleFeature(SqlAssistFeature.KeywordUppercase);
        service.ToggleDiagnostics();

        var reloaded = new SettingsService(path).GetSnapshot();

        if (reloaded.Enabled || reloaded.Features.KeywordUppercase || !reloaded.DiagnosticsEnabled)
        {
            throw new InvalidOperationException("設定沒有正確永久保存。");
        }

        if (Directory.GetFiles(directory, "*.tmp").Length != 0)
        {
            throw new InvalidOperationException("原子寫入留下了暫存檔。");
        }

        passed++;
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

void TestSuggestionFlow()
{
    var catalog = BuiltInSuggestionCatalog.Create();
    var oneCharacter = SuggestionMatcher.Match(catalog, SqlCompletionContextAnalyzer.Analyze("s"));

    if (oneCharacter.Count == 0 || oneCharacter[0].Kind != SuggestionKind.Keyword)
    {
        throw new InvalidOperationException("輸入單一字母時，應優先顯示 SQL 關鍵字。");
    }

    var snippet = SuggestionMatcher.Match(catalog, SqlCompletionContextAnalyzer.Analyze("ssf"));

    if (snippet.Count == 0 || snippet[0].DisplayText != "ssf" || !snippet[0].TriggerFollowUp)
    {
        throw new InvalidOperationException("ssf 應顯示可接續資料表建議的 Snippet。");
    }

    var table = new SqlSuggestion(
        "dbo.Publisher",
        "[dbo].[Publisher]",
        "Table",
        "Publisher table",
        SuggestionKind.Table);
    var afterSnippet = SuggestionMatcher.Match(
        catalog.Concat(new[] { table }),
        SqlCompletionContextAnalyzer.Analyze("SELECT * FROM "));

    if (afterSnippet.Count != 1 || afterSnippet[0].Kind != SuggestionKind.Table)
    {
        throw new InvalidOperationException("SELECT * FROM 後應只顯示資料表或 View。");
    }

    var afterAlterProcedure = SuggestionMatcher.Match(
        new[]
        {
            new SqlSuggestion("dbo.usp_Test", "[dbo].[usp_Test]", "Procedure", "Preview", SuggestionKind.Procedure),
            table
        },
        SqlCompletionContextAnalyzer.Analyze("ALTER PROCEDURE "));

    if (afterAlterProcedure.Count != 1 || afterAlterProcedure[0].Kind != SuggestionKind.Procedure)
    {
        throw new InvalidOperationException("ALTER PROCEDURE 後應只顯示 Procedure。");
    }

    var schemaContext = SqlCompletionContextAnalyzer.Analyze("SELECT * FROM [dbo].");

    if (!schemaContext.IsValid || schemaContext.SchemaQualifier != "dbo" ||
        schemaContext.Target != CompletionTarget.DataSource)
    {
        throw new InvalidOperationException("Schema 後方應接續顯示該 Schema 的資料來源物件。");
    }

    var sysUser = new SqlSuggestion(
        "Lib_Reader",
        "[dbo].[Lib_Reader]",
        "Table · dbo",
        "Table preview",
        SuggestionKind.Table,
        schemaName: "dbo");
    var afterTypingS = SuggestionMatcher.Match(
        catalog.Concat(new[] { sysUser }),
        SqlCompletionContextAnalyzer.Analyze("SELECT * FROM s"));

    if (afterTypingS.Count != 1 || afterTypingS[0].DisplayText != "Lib_Reader")
    {
        throw new InvalidOperationException("ssf 提交後輸入 s 應只顯示符合的 Table／View。");
    }

    passed++;
}
