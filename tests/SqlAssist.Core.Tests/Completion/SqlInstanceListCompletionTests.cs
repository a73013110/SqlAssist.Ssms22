using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 執行個體名單（定序、語言、時區）的位置、指令碼已用值、排名分級與插入文字。
/// </summary>
/// <remarks>
/// 少了位置，那一格落在 <see cref="CompletionTarget.Any"/>：整個資料庫的物件、
/// 一百九十幾個關鍵字與全部片段一起進場，而文法上接得了的只有那份名單。
/// 三份名單走同一條規則，所以每一條案例都盡量三份一起測。
/// </remarks>
public sealed class SqlInstanceListCompletionTests
{
    private const string DatabaseCollation = "Chinese_Taiwan_Stroke_CI_AS";

    [Theory]
    // 三種 COLLATE：運算式之後、資料行定義、資料庫。
    [InlineData("SELECT * FROM dbo.Loan l JOIN dbo.Copy c ON l.CopyNo = c.CopyNo COLLATE ", CompletionTarget.Collation)]
    [InlineData("WHERE a.InternalCode = c.InternalCode COLLATE Chin", CompletionTarget.Collation)]
    [InlineData("ORDER BY r.ReaderName COLLATE ", CompletionTarget.Collation)]
    [InlineData("CREATE TABLE dbo.Lib_Reader (ReaderName NVARCHAR(50) COLLATE ", CompletionTarget.Collation)]
    [InlineData("ALTER TABLE dbo.Lib_Reader ALTER COLUMN ReaderName NVARCHAR(50) COLLATE ", CompletionTarget.Collation)]
    [InlineData("CREATE DATABASE LibArchive COLLATE ", CompletionTarget.Collation)]
    [InlineData("ALTER DATABASE LibArchive COLLATE ", CompletionTarget.Collation)]
    // 語言：SET LANGUAGE 與各種 DEFAULT_LANGUAGE = 是同一份名單。
    [InlineData("SET LANGUAGE ", CompletionTarget.Language)]
    [InlineData("SET LANGUAGE us", CompletionTarget.Language)]
    [InlineData("SELECT 1\nSET LANGUAGE ", CompletionTarget.Language)]
    [InlineData("BEGIN\n    SET LANGUAGE ", CompletionTarget.Language)]
    [InlineData("CREATE LOGIN LibReader WITH PASSWORD = 'x', DEFAULT_LANGUAGE = ", CompletionTarget.Language)]
    [InlineData("ALTER LOGIN LibReader WITH DEFAULT_LANGUAGE = ", CompletionTarget.Language)]
    [InlineData("CREATE USER LibReader WITH PASSWORD = 'x', DEFAULT_LANGUAGE = ", CompletionTarget.Language)]
    [InlineData("ALTER DATABASE CURRENT SET DEFAULT_LANGUAGE = ", CompletionTarget.Language)]
    // 時區：可以接在任何運算式之後，也可以連兩次。
    [InlineData("SELECT GETDATE() AT TIME ZONE ", CompletionTarget.TimeZone)]
    [InlineData("SELECT l.LoanDate AT TIME ZONE 'UTC' AT TIME ZONE ", CompletionTarget.TimeZone)]
    [InlineData("WHERE l.LoanDate AT TIME ZONE Tai", CompletionTarget.TimeZone)]
    public void 名單的位置(string textBeforeCaret, CompletionTarget expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(SqlCompletionSlot.Grammar, context.Slot);
        Assert.Equal(expected, context.Target);
        Assert.True(SqlCompletionPolicy.IsClosed(context));
    }

    /// <summary>值打完之後就不是了；<c>UPDATE t SET Language</c> 的 Language 是資料行。</summary>
    [Theory]
    [InlineData("WHERE a.Code = c.Code COLLATE Chinese_Taiwan_Stroke_CI_AS ")]
    [InlineData("SELECT COLLATE")]
    [InlineData("SELECT ")]
    [InlineData("SET LANGUAGE us_english ")]
    [InlineData("UPDATE r SET Language ")]
    [InlineData("UPDATE r SET Language = ")]
    [InlineData("SELECT l.LoanDate AT TIME ZONE 'UTC' ")]
    [InlineData("SELECT l.LoanDate AT TIME ")]
    public void 不是名單的位置(string textBeforeCaret)
    {
        Assert.Null(SqlInstanceList.For(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target));
    }

    /// <summary>
    /// 反過來，名單的位置不列資料表、關鍵字與片段。
    /// </summary>
    /// <remarks>
    /// 這一條才是整個目標存在的理由。少了它使用者看到的是一份看起來很正常的
    /// 清單，而裡面沒有一項在那個位置是合法的——順手按下 Enter 就寫出一句
    /// 執行不了的 SQL。
    /// </remarks>
    [Theory]
    [InlineData("WHERE a.Code = c.Code COLLATE ")]
    [InlineData("SET LANGUAGE ")]
    [InlineData("SELECT GETDATE() AT TIME ZONE ")]
    public void 名單的位置不列別的東西(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var list = SqlInstanceList.For(context.Target)!;
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[] { Table("Lib_Reader"), Table("Loan") })
            .Concat(list.Suggestions(context.ScriptSources, Server("Value_A", "Value_B")))
            .ToArray();

        var filtered = SuggestionContextFilter.Filter(candidates, context);

        Assert.NotEmpty(filtered);
        Assert.All(filtered, item => Assert.Contains(
            item.Kind,
            new[] { SuggestionKind.InstanceListValue, SuggestionKind.InstanceListValueInUse }));
    }

    /// <summary>名單反過來也不進一般清單：定序那五千多筆會把真正要找的東西淹掉。</summary>
    [Theory]
    [InlineData("SELECT * FROM Chin")]
    [InlineData("SELECT DAT")]
    public void 一般位置不列名單(string textBeforeCaret)
    {
        var candidates = SqlInstanceList.All
            .SelectMany(list => list.Suggestions(Array.Empty<SqlSuggestion>(), Server(DatabaseCollation)))
            .ToArray();

        Assert.Empty(SuggestionContextFilter.Filter(
            candidates,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret)));
    }

    /// <summary>查不到伺服器的名單時，這個位置仍然有文法上的字與指令碼已用值。</summary>
    [Fact]
    public void 問不到伺服器時仍然有東西可選()
    {
        var sql = SqlWithCaret.Parse("SELECT a COLLATE Latin1_General_CI_AS, b COLLATE |");
        var context = SqlCompletionContextAnalyzer.Analyze(sql.Text, sql.Caret);

        var names = SqlInstanceList.Collation
            .Suggestions(context.ScriptSources, SqlInstanceListData.Empty)
            .Select(item => item.DisplayText);

        Assert.Equal(new[] { "DATABASE_DEFAULT", "CATALOG_DEFAULT", "Latin1_General_CI_AS" }, names);
    }

    [Theory]
    [InlineData(
        "SELECT * FROM dbo.Loan l\nJOIN dbo.Copy c ON l.CopyNo = c.CopyNo COLLATE " + DatabaseCollation + "\nWHERE l.Branch = @branch COLLATE |",
        DatabaseCollation)]
    [InlineData("SET LANGUAGE N'Deutsch'\nALTER LOGIN LibReader WITH DEFAULT_LANGUAGE = |", "Deutsch")]
    [InlineData("SET LANGUAGE [Português (Brasil)]\nSET LANGUAGE |", "Português (Brasil)")]
    [InlineData("SELECT l.LoanDate AT TIME ZONE 'Taipei Standard Time' AT TIME ZONE |", "Taipei Standard Time")]
    public void 指令碼裡出現過的值會列出來(string sqlWithCaret, string expected)
    {
        var sql = SqlWithCaret.Parse(sqlWithCaret);

        var context = SqlCompletionContextAnalyzer.Analyze(sql.Text, sql.Caret);

        var source = Assert.Single(context.ScriptSources);
        Assert.Equal(expected, source.DisplayText);
        Assert.Equal(SuggestionKind.InstanceListValueInUse, source.Kind);
    }

    /// <summary>
    /// 形狀不像這份名單的值不收：<c>AT TIME ZONE</c> 後面的識別字是資料行，
    /// <c>COLLATE</c> 後面的方括號名稱是語法錯誤，變數與別的名單的值也不算。
    /// </summary>
    [Theory]
    [InlineData("SELECT l.LoanDate AT TIME ZONE r.TimeZoneName, l.LoanDate AT TIME ZONE |")]
    [InlineData("SELECT a COLLATE [Latin1_General_CI_AS], b COLLATE |")]
    [InlineData("SET LANGUAGE @lang\nSET LANGUAGE |")]
    [InlineData("SET LANGUAGE us_english\nSELECT l.LoanDate AT TIME ZONE |")]
    [InlineData("UPDATE r SET Language = N'Deutsch'\nSET LANGUAGE |")]
    public void 不是這份名單的值不收(string sqlWithCaret)
    {
        var sql = SqlWithCaret.Parse(sqlWithCaret);

        Assert.Empty(SqlCompletionContextAnalyzer.Analyze(sql.Text, sql.Caret).ScriptSources);
    }

    /// <summary>同一個名稱只列一次，文法上的字已經有的也不再重複。</summary>
    [Fact]
    public void 重複的值只列一次()
    {
        const string sql = "SELECT a COLLATE Latin1_General_CI_AS, b COLLATE LATIN1_GENERAL_CI_AS, c COLLATE DATABASE_DEFAULT";

        var source = Assert.Single(SqlInstanceList.Collation.ScriptValues(sql, SqlTokenizer.Tokenize(sql)));
        Assert.Equal("Latin1_General_CI_AS", source.DisplayText);
    }

    /// <summary>
    /// 在用的那一個排在名單之前，指令碼寫過的與它重複時只留一份；名單的細節寫在列尾。
    /// </summary>
    [Fact]
    public void 伺服器的回答分成兩級()
    {
        var script = new[] { InUse(SqlInstanceList.Language, "Deutsch") };
        var server = new SqlInstanceListData(
            new[]
            {
                new SqlInstanceListEntry("Deutsch", "German"),
                new SqlInstanceListEntry("us_english", "English"),
                new SqlInstanceListEntry("繁體中文", "Traditional Chinese")
            },
            "us_english");

        var suggestions = SqlInstanceList.Language.Suggestions(script, server);

        Assert.Equal(new[] { "Deutsch", "us_english", "繁體中文" }, suggestions.Select(item => item.DisplayText));
        Assert.Equal(
            new[] { SuggestionKind.InstanceListValueInUse, SuggestionKind.InstanceListValueInUse, SuggestionKind.InstanceListValue },
            suggestions.Select(item => item.Kind));
        Assert.Equal("登入的預設語言", suggestions[1].Description);
        Assert.Equal("Traditional Chinese", suggestions[2].Description);
        Assert.All(suggestions, item => Assert.Same(SqlInstanceList.Language, item.Tag));
    }

    /// <summary>
    /// 實際在用的值排在伺服器名單之前，打了前綴之後仍然如此。
    /// </summary>
    /// <remarks>
    /// 定序五千多個名稱長得幾乎一樣，模糊比對撈回來的順序沒有意義；名稱長度反而
    /// 會把短的那一個推到前面，而使用者要的是這個資料庫的那一個。
    /// </remarks>
    [Theory]
    [InlineData("WHERE a.Code = c.Code COLLATE ")]
    [InlineData("WHERE a.Code = c.Code COLLATE Chin")]
    public void 實際在用的值排在前面(string textBeforeCaret)
    {
        var candidates = SqlInstanceList.Collation.Suggestions(
            Array.Empty<SqlSuggestion>(),
            Server(
                DatabaseCollation,
                "Chinese_Taiwan_Stroke_CI_AI",
                DatabaseCollation,
                "Chinese_Taiwan_Stroke_CS_AS",
                "Latin1_General_CI_AS",
                "SQL_Latin1_General_CP1_CI_AS"));

        var ranked = SuggestionListProbe.Match(candidates, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret));

        Assert.Equal(DatabaseCollation, ranked[0].DisplayText);
    }

    /// <summary>
    /// 三份名單寫進指令碼的樣子各不相同，且都不歸「加上方括號」的設定管。
    /// </summary>
    /// <remarks>
    /// <c>COLLATE [Latin1_General_CI_AS]</c> 是語法錯誤；<c>AT TIME ZONE Taipei</c> 剖析得過，
    /// 卻是一個資料行參考。兩者都不報錯地寫進編輯器，直到執行才失敗。
    /// </remarks>
    [Theory]
    [InlineData("WHERE a.Code = c.Code COLLATE |", "Latin1_General_CI_AS", "Latin1_General_CI_AS")]
    [InlineData("SET LANGUAGE |", "us_english", "us_english")]
    [InlineData("SET LANGUAGE |", "Português (Brasil)", "[Português (Brasil)]")]
    [InlineData("ALTER LOGIN LibReader WITH DEFAULT_LANGUAGE = |", "繁體中文", "繁體中文")]
    [InlineData("SELECT GETDATE() AT TIME ZONE |", "Taipei Standard Time", "'Taipei Standard Time'")]
    [InlineData("SELECT GETDATE() AT TIME ZONE Tai|", "Taipei Standard Time", "'Taipei Standard Time'")]
    public void 插入文字依名單而定(string sqlWithCaret, string name, string expected)
    {
        var sql = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(sql.Text, sql.Caret);
        var list = SqlInstanceList.For(context.Target)!;
        var suggestion = list
            .Suggestions(Array.Empty<SqlSuggestion>(), Server(name))
            .Single(item => item.DisplayText == name);

        foreach (var useSquareBrackets in new[] { false, true })
        {
            var settings = new SqlAssistSettings { UseSquareBrackets = useSquareBrackets };

            Assert.Equal(expected, SqlInsertionText.Build(suggestion, context, settings));
        }
    }

    [Theory]
    [InlineData("O'Brien Time", SqlInstanceValueForm.String, "'O''Brien Time'")]
    [InlineData("台北", SqlInstanceValueForm.String, "N'台北'")]
    [InlineData("Order", SqlInstanceValueForm.Identifier, "[Order]")]
    [InlineData("Order", SqlInstanceValueForm.Bare, "Order")]
    public void 值的寫法(string name, SqlInstanceValueForm form, string expected)
    {
        Assert.Equal(expected, SqlInsertionText.InstanceListValue(name, form));
    }

    [Theory]
    [InlineData("'UTC'", true, "UTC")]
    [InlineData("N'O''Brien'", true, "O'Brien")]
    [InlineData("n''", true, "")]
    [InlineData("'Taipei", false, "")]
    [InlineData("'a'b'", false, "")]
    [InlineData("UTC", false, "")]
    public void 讀出字串常值(string text, bool expected, string value)
    {
        Assert.Equal(expected, SqlStringLiteral.TryUnquote(text, out var actual));
        Assert.Equal(value, actual);
    }

    [Fact]
    public void 說明跟著介面語言()
    {
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            var suggestions = SqlInstanceList.TimeZone.Suggestions(
                Array.Empty<SqlSuggestion>(),
                new SqlInstanceListData(new[] { new SqlInstanceListEntry("UTC") }, "Taipei Standard Time"));

            Assert.Equal(new[] { "Server time zone", "Time zone" }, suggestions.Select(item => item.Description));
            Assert.Equal(
                "Use current database collation",
                SqlInstanceList.Collation.Defaults[0].Description);
        }
    }

    /// <summary>每一份名單都有自己的目標，而且目標認得回名單。</summary>
    [Fact]
    public void 目標與名單一對一()
    {
        Assert.All(SqlInstanceList.All, list => Assert.Same(list, SqlInstanceList.For(list.Target)));
        Assert.Equal(SqlInstanceList.All.Count, SqlInstanceList.All.Select(list => list.Target).Distinct().Count());
    }

    private static SqlInstanceListData Server(string inUse, params string[] names)
    {
        return new SqlInstanceListData(names.Select(name => new SqlInstanceListEntry(name)).ToArray(), inUse);
    }

    private static SqlSuggestion InUse(SqlInstanceList list, string name)
    {
        return new SqlSuggestion(name, name, "script", "script", SuggestionKind.InstanceListValueInUse, tag: list);
    }

    private static SqlSuggestion Table(string name)
    {
        return new SqlSuggestion(
            name,
            $"[dbo].[{name}]",
            "Table · dbo",
            $"Table {name}",
            SuggestionKind.Table,
            schemaName: "dbo");
    }
}
