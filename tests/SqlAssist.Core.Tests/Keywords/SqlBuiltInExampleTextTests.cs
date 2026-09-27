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

    /// <summary>單一段也要有標頭：單一規則比「只有一段就不寫」的補丁好守。</summary>
    [Fact]
    public void 單一段仍加上標頭()
    {
        var text = SqlBuiltInExampleText.Combine(new[] { Example("basic", "基本用法", "SELECT 1") });

        Assert.Equal("-- ▸ 基本用法\nSELECT 1", text);
    }

    /// <summary>每段前面加標頭，段落之間空一行。</summary>
    [Fact]
    public void 多段之間空一行並各自加上標頭()
    {
        var examples = new List<SqlBuiltInExample>
        {
            Example("basic", "基本用法", "SELECT 1"),
            Example("trap", "漏寫OUTPUT的陷阱", "SELECT 2"),
        };

        var text = SqlBuiltInExampleText.Combine(examples);

        Assert.Equal(
            "-- ▸ 基本用法\nSELECT 1\n\n-- ▸ 漏寫OUTPUT的陷阱\nSELECT 2",
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
            "-- ▸ 第一段\nSELECT 1\n\n-- ▸ 第二段\nSELECT 2\n\n-- ▸ 第三段\nSELECT 3",
            text);
    }
}
