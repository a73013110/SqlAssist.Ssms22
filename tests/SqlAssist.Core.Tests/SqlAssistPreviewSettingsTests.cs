using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SqlAssistPreviewSettingsTests
{
    [Fact]
    public void 預設是按向右鍵才展開()
    {
        // 自動展開會讓方向鍵掃過清單時連續重畫，預設要保守。
        Assert.Equal(SqlPreviewMode.RightArrow, new SqlAssistPreviewSettings().Mode);
    }

    [Theory]
    [InlineData(SqlPreviewMode.Off)]
    [InlineData(SqlPreviewMode.Delay)]
    [InlineData(SqlPreviewMode.RightArrow)]
    public void 模式可以來回轉換(SqlPreviewMode mode)
    {
        var settings = new SqlAssistPreviewSettings { Mode = mode };

        Assert.Equal(mode, settings.Mode);
        Assert.Equal(mode, settings.Clone().Mode);
    }

    [Fact]
    public void 預設貼在建議清單旁()
    {
        Assert.Equal(SqlPreviewPlacement.Beside, new SqlAssistPreviewSettings().Placement);
    }

    [Theory]
    [InlineData(SqlPreviewPlacement.Beside)]
    [InlineData(SqlPreviewPlacement.Stacked)]
    public void 擺放位置可以來回轉換(SqlPreviewPlacement placement)
    {
        var settings = new SqlAssistPreviewSettings { Placement = placement };

        Assert.Equal(placement, settings.Placement);
        Assert.Equal(placement, settings.Clone().Placement);
    }

    [Fact]
    public void 尺寸為零或負數時退回預設值()
    {
        // 設定檔被手動改壞時，不能畫出一個看不見的視窗。
        var settings = new SqlAssistPreviewSettings { Width = 0, Height = -100 };

        Assert.Equal(620, settings.ClampWidth());
        Assert.Equal(420, settings.ClampHeight());
    }

    [Fact]
    public void 尺寸小於下限時拉回下限()
    {
        var settings = new SqlAssistPreviewSettings { Width = 10, Height = 10 };

        Assert.Equal(SqlAssistPreviewSettings.MinimumWidth, settings.ClampWidth());
        Assert.Equal(SqlAssistPreviewSettings.MinimumHeight, settings.ClampHeight());
    }

    [Fact]
    public void 尺寸大於上限時壓回上限()
    {
        var settings = new SqlAssistPreviewSettings { Width = 99999, Height = 99999 };

        Assert.Equal(SqlAssistPreviewSettings.MaximumWidth, settings.ClampWidth());
        Assert.Equal(SqlAssistPreviewSettings.MaximumHeight, settings.ClampHeight());
    }

    [Fact]
    public void 尺寸落在範圍內時原樣保留()
    {
        var settings = new SqlAssistPreviewSettings { Width = 800, Height = 500 };

        Assert.Equal(800, settings.ClampWidth());
        Assert.Equal(500, settings.ClampHeight());
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(220, 220)]
    [InlineData(9999, 2000)]
    public void 延遲毫秒數收斂到合理範圍(int configured, int expected)
    {
        var settings = new SqlAssistPreviewSettings { DelayMilliseconds = configured };

        Assert.Equal(expected, settings.ClampDelay());
    }

    [Fact]
    public void 複製會帶走每一個欄位()
    {
        var settings = new SqlAssistPreviewSettings
        {
            Mode = SqlPreviewMode.Delay,
            Placement = SqlPreviewPlacement.Stacked,
            DelayMilliseconds = 350,
            Width = 700,
            Height = 480
        };

        var clone = settings.Clone();

        Assert.Equal(SqlPreviewMode.Delay, clone.Mode);
        Assert.Equal(SqlPreviewPlacement.Stacked, clone.Placement);
        Assert.Equal(350, clone.DelayMilliseconds);
        Assert.Equal(700, clone.Width);
        Assert.Equal(480, clone.Height);
    }
}
