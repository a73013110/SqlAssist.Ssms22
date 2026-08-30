using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

public sealed class SqlGlobalVariableCatalogTests
{
    [Theory]
    [InlineData("@@VERSION")]
    [InlineData("@@ROWCOUNT")]
    [InlineData("@@IDENTITY")]
    [InlineData("@@ERROR")]
    [InlineData("@@TRANCOUNT")]
    [InlineData("@@FETCH_STATUS")]
    [InlineData("@@SPID")]
    public void 收錄常用的全域變數(string name)
    {
        Assert.Contains(SqlGlobalVariableCatalog.All, item => item.DisplayText == name);
    }

    /// <summary>
    /// 顯示文字與插入文字都必須含前面那兩個小老鼠。
    /// </summary>
    /// <remarks>
    /// 適用範圍從第一個小老鼠開始算，插入文字少了它們的話，
    /// <c>@@ROW</c> 提交之後會變成 <c>@@ROWCOUNT</c> 前面還留著原本的 <c>@@</c>。
    /// </remarks>
    [Fact]
    public void 名稱與插入文字都帶著小老鼠()
    {
        Assert.All(SqlGlobalVariableCatalog.All, item =>
        {
            Assert.StartsWith("@@", item.DisplayText);
            Assert.Equal(item.DisplayText, item.InsertionText);
        });
    }

    [Fact]
    public void 全部歸在全域變數類別()
    {
        Assert.All(
            SqlGlobalVariableCatalog.All,
            item => Assert.Equal(SuggestionKind.GlobalVariable, item.Kind));
    }

    /// <summary>
    /// 已淘汰的名稱不收：建議一個被移除的名稱比少一個名稱糟。
    /// </summary>
    [Fact]
    public void 不收已淘汰的名稱()
    {
        Assert.DoesNotContain(
            SqlGlobalVariableCatalog.All,
            item => item.DisplayText == "@@REMSERVER");
    }

    [Fact]
    public void 沒有重複的名稱()
    {
        var names = SqlGlobalVariableCatalog.All.Select(item => item.DisplayText).ToArray();

        Assert.Equal(names.Length, names.Distinct().Count());
    }

    /// <summary>
    /// 每一項都要有說明：那是清單右側唯一告訴使用者這個變數是什麼的東西。
    /// </summary>
    [Fact]
    public void 每一項都有說明()
    {
        Assert.All(
            SqlGlobalVariableCatalog.All,
            item => Assert.False(string.IsNullOrWhiteSpace(item.Description)));
    }
}
