using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Settings;

/// <summary>
/// Unified Settings 的 minimum／maximum 只約束設定 UI 的輸入，
/// 手改設定檔或讀不到註冊資訊時仍可能拿到界外值，所以讀取端要再收斂一次。
/// </summary>
public sealed class SqlAssistLimitsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(999, 10)]
    [InlineData(-3, 1)]
    public void 觸發字元數收斂到一到十(int value, int expected)
    {
        Assert.Equal(expected, SqlAssistLimits.ClampTriggerCharacters(value));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(220, 220)]
    [InlineData(9999, 2000)]
    public void 預覽延遲收斂到零到兩千(int value, int expected)
    {
        Assert.Equal(expected, SqlAssistLimits.ClampPreviewDelay(value));
    }

    [Theory]
    [InlineData(14, 14)]
    [InlineData(3, SqlAssistLimits.MinimumPreviewFontSize)]
    [InlineData(99, SqlAssistLimits.MaximumPreviewFontSize)]
    public void 字級收斂到可讀範圍(double value, double expected)
    {
        Assert.Equal(expected, SqlAssistLimits.ClampPreviewFontSize(value));
    }

    /// <remarks>
    /// 0、NaN 與無限大不是使用者調出來的，是資料損壞——回退到預設值而不是下限，
    /// 否則畫出來的是一個技術上合法但明顯不對的視窗。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 損壞的尺寸回退到預設值(double value)
    {
        Assert.Equal(SqlAssistLimits.DefaultPreviewWidth, SqlAssistLimits.ClampPreviewWidth(value));
        Assert.Equal(SqlAssistLimits.DefaultPreviewHeight, SqlAssistLimits.ClampPreviewHeight(value));
        Assert.Equal(SqlAssistLimits.DefaultPreviewFontSize, SqlAssistLimits.ClampPreviewFontSize(value));
    }

    [Fact]
    public void 尺寸收斂到允許範圍()
    {
        Assert.Equal(SqlAssistLimits.MinimumPreviewWidth, SqlAssistLimits.ClampPreviewWidth(10));
        Assert.Equal(SqlAssistLimits.MaximumPreviewWidth, SqlAssistLimits.ClampPreviewWidth(99999));
        Assert.Equal(SqlAssistLimits.MinimumPreviewHeight, SqlAssistLimits.ClampPreviewHeight(10));
        Assert.Equal(SqlAssistLimits.MaximumPreviewHeight, SqlAssistLimits.ClampPreviewHeight(99999));
        Assert.Equal(800, SqlAssistLimits.ClampPreviewWidth(800));
    }
}
