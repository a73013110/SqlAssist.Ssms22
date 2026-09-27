using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 多段範例接成一份文字給浮動預覽的範例分頁；只看資料就決定得了，因此獨立於
/// <see cref="SqlAssist.Ssms22.Preview"/> 之外測試。
/// </summary>
public sealed class SqlBuiltInExampleTextTests
{
    private static SqlBuiltInExample Example(string id, string title, string sql) => new(id, title, sql);

    [Fact]
    public void 沒有範例回傳空字串()
    {
        Assert.Equal(string.Empty, SqlBuiltInExampleText.Combine(System.Array.Empty<SqlBuiltInExample>()));
    }

    /// <summary>單一段沒有下一段可分隔，不加 GO；標頭仍要有：單一規則比「只有一段就不寫」的補丁好守。</summary>
    [Fact]
    public void 單一段仍加上標頭且不加GO()
    {
        var text = SqlBuiltInExampleText.Combine(new[] { Example("basic", "基本用法", "SELECT 1") });

        Assert.Equal("-- ▸ 基本用法\nSELECT 1", text);
    }

    /// <summary>每段前面加標頭，段落之間插一行 GO 再空一行，讓整頁複製後可以分批執行。</summary>
    [Fact]
    public void 多段之間插入GO並空一行()
    {
        var examples = new List<SqlBuiltInExample>
        {
            Example("basic", "基本用法", "SELECT 1"),
            Example("trap", "漏寫OUTPUT的陷阱", "SELECT 2"),
        };

        var text = SqlBuiltInExampleText.Combine(examples);

        Assert.Equal(
            "-- ▸ 基本用法\nSELECT 1\nGO\n\n-- ▸ 漏寫OUTPUT的陷阱\nSELECT 2",
            text);
    }

    /// <summary>三段以上一樣接得起來，不是只有兩段的特例。</summary>
    [Fact]
    public void 三段以上照樣接成一份()
    {
        var examples = new List<SqlBuiltInExample>
        {
            Example("a", "第一段", "SELECT 1"),
            Example("b", "第二段", "SELECT 2"),
            Example("c", "第三段", "SELECT 3"),
        };

        var text = SqlBuiltInExampleText.Combine(examples);

        Assert.Equal(
            "-- ▸ 第一段\nSELECT 1\nGO\n\n-- ▸ 第二段\nSELECT 2\nGO\n\n-- ▸ 第三段\nSELECT 3",
            text);
    }

    /// <summary>某段本身已經以 GO 結尾時不重複加，避免連續兩行 GO。</summary>
    [Fact]
    public void 段落已經以GO結尾時不重複加GO()
    {
        var examples = new List<SqlBuiltInExample>
        {
            Example("create", "建立程序", "CREATE PROCEDURE #Lib_X AS SELECT 1;\nGO"),
            Example("call", "呼叫程序", "EXEC #Lib_X"),
        };

        var text = SqlBuiltInExampleText.Combine(examples);

        Assert.Equal(
            "-- ▸ 建立程序\nCREATE PROCEDURE #Lib_X AS SELECT 1;\nGO\n\n-- ▸ 呼叫程序\nEXEC #Lib_X",
            text);
    }
}
