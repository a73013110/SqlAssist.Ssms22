using System;
using System.Linq;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SqlTokenizerTests
{
    private static string[] Values(string sql)
    {
        return SqlTokenizer.Tokenize(sql).Select(token => token.Value).ToArray();
    }

    [Fact]
    public void 切出基本的敘述()
    {
        Assert.Equal(
            new[] { "SELECT", "*", "FROM", "dbo", ".", "Lib_Reader" },
            Values("SELECT * FROM dbo.Lib_Reader"));
    }

    [Fact]
    public void 略過空白與單行註解()
    {
        Assert.Equal(
            new[] { "SELECT", "1" },
            Values("SELECT -- 這裡是註解 FROM x\r\n  1"));
    }

    [Fact]
    public void 略過區塊註解()
    {
        Assert.Equal(new[] { "SELECT", "1" }, Values("SELECT /* FROM x */ 1"));
    }

    /// <summary>T-SQL 的區塊註解可以巢狀，找第一個 */ 會提早結束而把後面當成程式碼。</summary>
    [Fact]
    public void 區塊註解可巢狀()
    {
        Assert.Equal(new[] { "SELECT", "1" }, Values("SELECT /* 外層 /* 內層 */ 還在註解 */ 1"));
    }

    [Fact]
    public void 方括號識別字還原跳脫字元()
    {
        var token = SqlTokenizer.Tokenize("[Weird]]Name]").Single();

        Assert.Equal(SqlTokenKind.Identifier, token.Kind);
        Assert.True(token.IsQuoted);
        Assert.Equal("Weird]Name", token.Value);
        Assert.Equal("[Weird]]Name]", token.Text);
    }

    [Fact]
    public void 雙引號識別字還原跳脫字元()
    {
        var token = SqlTokenizer.Tokenize("\"a\"\"b\"").Single();

        Assert.Equal("a\"b", token.Value);
        Assert.True(token.IsQuoted);
    }

    /// <summary>加了引號就不是關鍵字，否則 FROM [FROM] 會被當成缺少資料表名稱。</summary>
    [Fact]
    public void 加引號的識別字不算關鍵字()
    {
        var tokens = SqlTokenizer.Tokenize("FROM [FROM]");

        Assert.True(tokens[0].IsKeyword("FROM"));
        Assert.False(tokens[1].IsKeyword("FROM"));
        Assert.Equal("FROM", tokens[1].Value);
    }

    [Fact]
    public void 字串常值不會被當成識別字()
    {
        var tokens = SqlTokenizer.Tokenize("WHERE Name = 'FROM dbo.X'");

        Assert.Equal(4, tokens.Count);
        Assert.Equal(SqlTokenKind.String, tokens[3].Kind);
        Assert.Equal("'FROM dbo.X'", tokens[3].Value);
    }

    [Fact]
    public void 字串內的兩個單引號是跳脫()
    {
        var tokens = SqlTokenizer.Tokenize("SELECT 'a''b', 1");

        Assert.Equal(SqlTokenKind.String, tokens[1].Kind);
        Assert.Equal("'a''b'", tokens[1].Text);
        Assert.Equal(",", tokens[2].Value);
    }

    /// <summary>N'...' 是 Unicode 字串，不是名為 N 的識別字後面接一個字串。</summary>
    [Fact]
    public void N前置詞屬於字串()
    {
        var tokens = SqlTokenizer.Tokenize("SELECT N'中文'");

        Assert.Equal(2, tokens.Count);
        Assert.Equal(SqlTokenKind.String, tokens[1].Kind);
        Assert.Equal("N'中文'", tokens[1].Text);
    }

    [Theory]
    [InlineData("@p", SqlTokenKind.Variable)]
    [InlineData("@@ROWCOUNT", SqlTokenKind.Variable)]
    [InlineData("#tmp", SqlTokenKind.Identifier)]
    [InlineData("##global", SqlTokenKind.Identifier)]
    [InlineData("123", SqlTokenKind.Number)]
    [InlineData("1.5", SqlTokenKind.Number)]
    [InlineData("0x1F", SqlTokenKind.Number)]
    [InlineData("1e-5", SqlTokenKind.Number)]
    public void 辨識各種詞法單元(string sql, SqlTokenKind expected)
    {
        var token = SqlTokenizer.Tokenize(sql).Single();

        Assert.Equal(expected, token.Kind);
        Assert.Equal(sql, token.Text);
    }

    [Fact]
    public void 兩字元運算子不會被拆開()
    {
        Assert.Equal(new[] { "a", "<=", "b" }, Values("a <= b"));
        Assert.Equal(new[] { "a", "<>", "b" }, Values("a <> b"));
        Assert.Equal(new[] { "a", "<", "b" }, Values("a < b"));
    }

    /// <summary>中文資料表名稱必須維持成一個識別字，不能被逐字切碎。</summary>
    [Fact]
    public void 支援非ASCII識別字()
    {
        var tokens = SqlTokenizer.Tokenize("SELECT * FROM 客戶資料");

        Assert.Equal(4, tokens.Count);
        Assert.Equal("客戶資料", tokens[3].Value);
        Assert.Equal(SqlTokenKind.Identifier, tokens[3].Kind);
    }

    [Fact]
    public void 每個詞法單元都帶原始位置()
    {
        const string sql = "SELECT * FROM dbo.Lib_Reader";
        var tokens = SqlTokenizer.Tokenize(sql);

        foreach (var token in tokens)
        {
            Assert.Equal(token.Text, sql.Substring(token.Start, token.Length));
        }
    }

    /// <summary>編輯中的敘述幾乎總是不完整，詞法器不能因此丟例外或漏掉已輸入的前綴。</summary>
    [Theory]
    [InlineData("SELECT * FROM [dbo", "dbo")]
    [InlineData("SELECT * FROM \"dbo", "dbo")]
    public void 未結束的識別字仍回報已輸入內容(string sql, string expected)
    {
        var tokens = SqlTokenizer.Tokenize(sql);

        Assert.Equal(expected, tokens[^1].Value);
        Assert.True(tokens[^1].IsQuoted);
    }

    [Fact]
    public void 未結束的字串不會丟例外()
    {
        var tokens = SqlTokenizer.Tokenize("SELECT 'abc");

        Assert.Equal(SqlTokenKind.String, tokens[1].Kind);
        Assert.Equal("'abc", tokens[1].Text);
    }

    [Fact]
    public void 空白輸入回傳空集合()
    {
        Assert.Empty(SqlTokenizer.Tokenize(string.Empty));
        Assert.Empty(SqlTokenizer.Tokenize("   \r\n  "));
        Assert.Empty(SqlTokenizer.Tokenize("-- 只有註解"));
    }

    [Fact]
    public void 可以只切出指定範圍()
    {
        const string sql = "SELECT * FROM dbo.Lib_Reader";
        var tokens = SqlTokenizer.Tokenize(sql, 14, sql.Length);

        Assert.Equal(new[] { "dbo", ".", "Lib_Reader" }, tokens.Select(token => token.Value));
        Assert.Equal(14, tokens[0].Start);
    }

    /// <summary>語法著色需要註解；語意分析不需要，因此預設仍然略過。</summary>
    [Fact]
    public void 指定保留註解時單行註解會成為詞法單元()
    {
        var tokens = SqlTokenizer.TokenizeWithComments("SELECT 1 -- 說明" + Environment.NewLine + "FROM t");
        var comments = tokens.Where(token => token.Kind == SqlTokenKind.Comment).ToArray();

        Assert.Single(comments);
        Assert.Equal("-- 說明", comments[0].Text);
        Assert.Equal(9, comments[0].Start);
    }

    [Fact]
    public void 指定保留註解時區塊註解會成為單一詞法單元()
    {
        var tokens = SqlTokenizer.TokenizeWithComments("SELECT /* 巢狀 /* 內層 */ 外層 */ 1");
        var comments = tokens.Where(token => token.Kind == SqlTokenKind.Comment).ToArray();

        Assert.Single(comments);
        Assert.Equal("/* 巢狀 /* 內層 */ 外層 */", comments[0].Text);
    }

    [Fact]
    public void 保留註解不影響其餘詞法單元()
    {
        var withComments = SqlTokenizer.TokenizeWithComments("SELECT /* x */ a FROM t");
        var withoutComments = SqlTokenizer.Tokenize("SELECT /* x */ a FROM t");

        Assert.Equal(
            withoutComments.Select(token => token.Value),
            withComments.Where(token => token.Kind != SqlTokenKind.Comment).Select(token => token.Value));
    }

    [Fact]
    public void 預設仍然略過註解()
    {
        Assert.Empty(SqlTokenizer.Tokenize("-- 只有註解"));
        Assert.Empty(SqlTokenizer.Tokenize("/* 只有註解 */"));
    }
}
