using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetDefaultsTests
{
    [Fact]
    public void 內建JSON有四十筆且識別碼與捷徑唯一()
    {
        var defaults = SqlSnippetDefaults.Current;

        Assert.Equal(40, defaults.Count);
        Assert.Equal(
            defaults.Count,
            defaults.Snippets.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            defaults.Count,
            defaults.Snippets.Select(item => item.Shortcut).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(defaults.Snippets, snippet =>
        {
            Assert.True(SqlSnippetIdentity.IsValid(snippet.Id), snippet.Id);
            Assert.True(SqlSnippetLibrary.Empty.ValidateShortcut(snippet.Shortcut, null, out var error), error);
            Assert.False(SqlKeywordCatalog.TryGetCanonical(snippet.Shortcut, out _), snippet.Shortcut);
            Assert.DoesNotContain("$CURSOR$", snippet.Code, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 內建佔位符由程式碼順序推導且Tab模式至少有一欄()
    {
        foreach (var snippet in SqlSnippetDefaults.Current.Snippets)
        {
            var extracted = SqlSnippetPlaceholders.Extract(snippet.Code);

            Assert.Equal(extracted, snippet.Placeholders.Select(item => item.Id).ToArray());
            Assert.True(Count(snippet.Code, SqlSnippet.CaretMarker) <= 1, snippet.Shortcut);

            if (snippet.ExpansionMode == SqlSnippetExpansionMode.TabStops)
            {
                Assert.NotEmpty(snippet.Placeholders);
            }
        }
    }

    /// <remarks>
    /// 這三筆的價值來自連線中繼資料，不是靜態骨架：<c>ap</c> 之後選到程序才會由
    /// <c>SqlCommitExpander</c> 放進完整定義。改成 Tab Stop 會把這條鏈整個切斷，
    /// 而症狀只是「清單沒有跳出來」，沒有任何錯誤。
    /// </remarks>
    [Theory]
    [InlineData("ssf", CompletionTarget.DataSource)]
    [InlineData("ap", CompletionTarget.Procedure)]
    [InlineData("af", CompletionTarget.Function)]
    public void 接續片段展開後落在會列出該類物件的位置(string shortcut, CompletionTarget target)
    {
        Assert.True(SqlSnippetDefaults.Current.TryGet(shortcut, out var snippet));
        Assert.Equal(SqlSnippetExpansionMode.Caret, snippet.ExpansionMode);
        Assert.True(snippet.TriggerFollowUp, shortcut);

        var expanded = snippet.Expand(out var caret);

        Assert.Equal(expanded.Length, caret);
        Assert.Equal(target, SqlCompletionContextAnalyzer.Analyze(expanded).Target);
    }

    /// <remarks>
    /// 這個 repo 是公開的，識別字本身就是使用者的私有資產（見 CLAUDE.md）。
    /// 內建片段是最容易不小心把真實 schema 名稱帶進來的地方：寫樣板時手邊
    /// 正好開著一份真的指令碼。這裡只認一組通用佔位名稱，要加新的就得先想過。
    /// </remarks>
    [Fact]
    public void 內建片段只用通用的佔位名稱()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "dbo", "TableName", "ColumnName", "SchemaName", "DatabaseName",
            "ProcedureName", "FunctionName", "ViewName", "IndexName",
            "TargetTable", "SourceTable", "TargetColumn", "SourceColumn",
            "KeyColumn", "UpdateColumn", "InsertColumn", "TempTable",
            "IX_TableName_ColumnName",
            "Value", "Name", "cte", "item_cursor", "t"
        };

        foreach (var snippet in SqlSnippetDefaults.Current.Snippets)
        {
            foreach (var placeholder in snippet.Placeholders)
            {
                // 空白、數字、SQL 常值與型別名稱都不是識別字，不必列進白名單。
                if (placeholder.DefaultValue.Length == 0 ||
                    !LooksLikeIdentifier(placeholder.DefaultValue))
                {
                    continue;
                }

                Assert.True(
                    allowed.Contains(placeholder.DefaultValue),
                    $"{snippet.Shortcut} 的 ${placeholder.Id}$ 預設值「{placeholder.DefaultValue}」" +
                    "不在通用佔位名稱清單裡；真實系統的名稱不能進這個 repo。");
            }
        }
    }

    /// <summary>看起來像資料庫識別字（會被誤認成真實名稱）的預設值。</summary>
    private static bool LooksLikeIdentifier(string value)
    {
        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        // 全大寫的是 T-SQL 型別與關鍵字（INT、NULL、TABLE…），那是語言事實。
        return value.ToUpperInvariant() != value;
    }

    [Fact]
    public void CASE捷徑不與關鍵字撞名()
    {
        Assert.False(SqlSnippetDefaults.Current.TryGet("case", out _));
        Assert.True(SqlSnippetDefaults.Current.TryGet("cs", out _));
    }

    [Fact]
    public void 沒有前綴時Snippet不會佔據第一順位()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[]
            {
                new SqlSuggestion(
                    "CopyNo",
                    "[CopyNo]",
                    "INT",
                    "CopyNo",
                    SuggestionKind.Column)
            });

        var first = candidates
            .OrderByDescending(SuggestionMatcher.ComposeStandingScore)
            .First();

        Assert.NotEqual(SuggestionKind.Snippet, first.Kind);
    }

    [Fact]
    public void 最近使用過的Snippet仍不佔據空前綴首頁()
    {
        SqlSuggestionUsage.Clear();

        try
        {
            var snippet = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
                .Single(item => item.DisplayText == "ssf");
            var column = new SqlSuggestion("CopyNo", "CopyNo", "INT", "CopyNo", SuggestionKind.Column);
            SqlSuggestionUsage.Record(snippet);

            Assert.True(
                SuggestionMatcher.ComposeStandingScore(column) >
                SuggestionMatcher.ComposeStandingScore(snippet));
        }
        finally
        {
            SqlSuggestionUsage.Clear();
        }
    }

    [Fact]
    public void 輸入libr時資料表排在任何Snippet之前()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[]
            {
                new SqlSuggestion(
                    "Lib_Reader",
                    "[dbo].[Lib_Reader]",
                    "Table · dbo",
                    "Table Lib_Reader",
                    SuggestionKind.Table,
                    schemaName: "dbo")
            });

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("libr"));

        Assert.Equal("Lib_Reader", ranked[0].Suggestion.DisplayText);
    }

    [Fact]
    public void 危險片段只在空前綴首頁隱藏()
    {
        var destructive = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Single(item => item.DisplayText == "df");

        Assert.False(SuggestionMatcher.IsVisibleWithoutPrefix(destructive, categorySelected: false));
        Assert.True(SuggestionMatcher.IsVisibleWithoutPrefix(destructive, categorySelected: true));
    }

    [Fact]
    public void DDL片段不會混進SELECT欄位位置()
    {
        var filtered = Available("SELECT ");

        Assert.DoesNotContain("ctb", filtered);
        Assert.Contains("cs", filtered);
    }

    /// <remarks>
    /// <c>positions</c> 給得太緊的症狀是全靜默的：使用者只覺得「這個片段有時候
    /// 有、有時候沒有」。每一筆都要有一個「一定找得到」的位置守著。
    /// </remarks>
    [Theory]
    // 語句級：語句開頭與 BEGIN…END 區塊裡都要在。曾經只給 StatementStart，
    // 於是 BEGIN 之後（分析器只回報 BlockStart）整批語句片段全部消失。
    [InlineData("SELECT 1;\n", "ssf,st100,st1,ssc,sd,ii,iis,ui,df,mg,cdb,ctb,cv,cp,cf,citvf,cix,at,dt,ap,af,beg,bt,ct,rt,ife,ifne,wl,tc,cur,trn,cte,sno,ptt,tt")]
    [InlineData("BEGIN\n    ", "ssf,st100,st1,ssc,sd,ii,iis,ui,df,mg,cdb,ctb,cv,cp,cf,citvf,cix,at,dt,ap,af,beg,bt,ct,rt,ife,ifne,wl,tc,cur,trn,cte,sno,ptt,tt")]
    // 運算式級：CASE 在選取清單、逗號之後與述詞裡都要在。
    [InlineData("SELECT ", "cs")]
    [InlineData("SELECT a, ", "cs")]
    [InlineData("SELECT * FROM Loan WHERE ", "cs")]
    [InlineData("SELECT * FROM Loan a INNER JOIN Copy b ON ", "cs")]
    // 資料來源之後：JOIN 與排序、分組子句。
    [InlineData("SELECT * FROM Loan AS a ", "ij,lj,ob,gb")]
    public void 內建片段在它自然的位置找得到(string prefix, string shortcuts)
    {
        var available = Available(prefix);

        foreach (var shortcut in shortcuts.Split(','))
        {
            Assert.Contains(shortcut, available);
        }
    }

    private static IReadOnlyCollection<string> Available(string prefix)
    {
        return SuggestionMatcher
            .Filter(
                BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current),
                SqlCompletionContextAnalyzer.Analyze(prefix))
            .Where(item => item.Kind == SuggestionKind.Snippet)
            .Select(item => item.DisplayText)
            .ToArray();
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var offset = 0;

        while ((offset = text.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
