using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Statements;
using Xunit;

namespace SqlAssist.Core.Tests.Statements;

public sealed class SqlMergeStatementTextTests
{
    private static readonly string[] BookCopy = { "CopyId", "CopyNo", "Barcode", "BranchId" };

    private static string Build(
        IReadOnlyList<string> keys,
        out int caretOffset,
        IReadOnlyList<string>? columns = null,
        string indent = "")
    {
        return SqlMergeStatementText.Build(
            "dbo.Cat_BookCopy",
            keys,
            columns ?? BookCopy,
            indent,
            "\r\n",
            out caretOffset);
    }

    [Fact]
    public void 三個子句一次填滿()
    {
        var text = Build(new[] { "CopyId" }, out _);

        Assert.Equal(
            "MERGE INTO dbo.Cat_BookCopy AS target\r\n" +
            "USING dbo.SourceTable AS source\r\n" +
            "    ON target.CopyId = source.CopyId\r\n" +
            "WHEN MATCHED AND 1 = 0 THEN\r\n" +
            "    UPDATE SET\r\n" +
            "        target.CopyNo = source.CopyNo,\r\n" +
            "        target.Barcode = source.Barcode,\r\n" +
            "        target.BranchId = source.BranchId\r\n" +
            "WHEN NOT MATCHED BY TARGET AND 1 = 0 THEN\r\n" +
            "    INSERT\r\n" +
            "    (\r\n" +
            "        CopyId,\r\n" +
            "        CopyNo,\r\n" +
            "        Barcode,\r\n" +
            "        BranchId\r\n" +
            "    )\r\n" +
            "    VALUES\r\n" +
            "    (\r\n" +
            "        source.CopyId,\r\n" +
            "        source.CopyNo,\r\n" +
            "        source.Barcode,\r\n" +
            "        source.BranchId\r\n" +
            "    );",
            text);
    }

    /// <remarks>
    /// 兩個動作子句都要帶著 <c>AND 1 = 0</c>。展開出來的是一句立刻執行得動的
    /// MERGE，而 MERGE 同時會改與插；沒有這個閘門，一次誤按 F5 就是一次資料事故。
    /// </remarks>
    [Fact]
    public void 兩個動作子句都帶著閘門()
    {
        var text = Build(new[] { "CopyId" }, out _);

        Assert.Contains("WHEN MATCHED AND 1 = 0 THEN", text, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED BY TARGET AND 1 = 0 THEN", text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// 比對鍵猜錯不會報錯，只會把資料寫到別列去，所以沒有主索引鍵時留一個
    /// 資料表裡不會有的名稱——這句 MERGE 因此編譯不過，使用者一定看得到。
    /// </remarks>
    [Fact]
    public void 沒有主索引鍵時留下編譯不過的佔位字()
    {
        var text = Build(Array.Empty<string>(), out _);

        Assert.Contains(
            $"ON target.{SqlMergeStatementText.MissingKeyPlaceholder} = " +
            $"source.{SqlMergeStatementText.MissingKeyPlaceholder}",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 複合主索引鍵每一欄各一列()
    {
        var text = Build(new[] { "CopyId", "BranchId" }, out _);

        Assert.Contains(
            "    ON target.CopyId = source.CopyId\r\n" +
            "    AND target.BranchId = source.BranchId\r\n",
            text,
            StringComparison.Ordinal);

        // 比對鍵不進 UPDATE SET：更新鍵本身沒有意義。
        Assert.DoesNotContain("target.BranchId = source.BranchId,", text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// 整張表都是鍵時 <c>UPDATE SET</c> 一欄都沒有，而空的 <c>SET</c> 是語法錯誤。
    /// MERGE 少一個動作子句仍然合法，所以整個 <c>WHEN MATCHED</c> 就不寫。
    /// </remarks>
    [Fact]
    public void 沒有可更新的欄位時不寫WHEN_MATCHED()
    {
        var text = Build(new[] { "CopyId" }, out _, columns: new[] { "CopyId" });

        Assert.DoesNotContain("WHEN MATCHED", text, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED BY TARGET", text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// 展開之後唯一還沒填的就是來源資料表，游標停在它的起點。
    /// 停在整段結尾等於一展開就被捲到最後一行，使用者得自己捲回去。
    /// </remarks>
    [Fact]
    public void 游標停在來源資料表上()
    {
        var text = Build(new[] { "CopyId" }, out var caretOffset);

        Assert.Equal(
            SqlMergeStatementText.SourcePlaceholder,
            text.Substring(caretOffset, SqlMergeStatementText.SourcePlaceholder.Length));
    }

    [Fact]
    public void 縮排整段重複到每一行()
    {
        var text = Build(new[] { "CopyId" }, out _, indent: "    ");
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);

        // 第一行由呼叫端接在原本的縮排後面，因此自己不帶縮排；
        // 少了這一段，WHEN 那兩行會貼齊左邊而整句看起來像壞掉的。
        Assert.StartsWith("MERGE INTO", lines[0], StringComparison.Ordinal);

        for (var index = 1; index < lines.Length; index++)
        {
            Assert.StartsWith("    ", lines[index], StringComparison.Ordinal);
        }
    }

    /// <remarks>
    /// 展開出來的 MERGE 必須是後續補字接得下去的：<c>target.</c> 與 <c>source.</c>
    /// 要解析得回那兩張表，否則使用者改條件時完全沒有欄位建議。這條鏈以前由
    /// 片段的欄位格守著，改成提交時展開之後守在這裡。
    /// </remarks>
    [Theory]
    [InlineData("target.", "Cat_BookCopy")]
    [InlineData("source.", "SourceTable")]
    public void 展開出來的別名解析得回資料表(string qualifier, string expected)
    {
        var text = Build(new[] { "CopyId" }, out _);
        var caret = text.IndexOf("WHEN NOT MATCHED", StringComparison.Ordinal);
        var sql = text.Substring(0, caret) + qualifier;
        var context = SqlCompletionContextAnalyzer.Analyze(
            sql + text.Substring(caret),
            sql.Length);

        Assert.Equal(CompletionTarget.Column, context.Target);

        var source = Assert.Single(context.ColumnSources!);

        Assert.Equal(SqlColumnSourceKind.Table, source.Kind);
        Assert.Equal(expected, source.Table!.ObjectName);
    }
}
