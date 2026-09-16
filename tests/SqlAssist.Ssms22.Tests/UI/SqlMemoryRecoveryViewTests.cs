using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlMemoryRecoveryViewTests
{
    static SqlMemoryRecoveryViewTests() => SqlIconImage.Factory = HostImage;

    private static FrameworkElement HostImage(SqlIcon icon) => new Border { Width = 16, Height = 16, Tag = icon };

    private static SqlMemoryRecoveryView CreateView()
    {
        var view = new SqlMemoryRecoveryView();
        view.Resources.MergedDictionaries.Add(new ThemeResourceSet().Resources);
        return view;
    }

    private static (Button OpenFolder, Button Rebuild) Actions(SqlMemoryRecoveryView view)
    {
        var content = Assert.IsType<StackPanel>(view.Child);
        var actions = Assert.IsType<StackPanel>(content.Children[3]);
        return (Assert.IsType<Button>(actions.Children[0]), Assert.IsType<Button>(actions.Children[1]));
    }

    [Fact]
    public void RecoveryViewCentersTheCardAndNamesItForAutomation()
    {
        WpfTest.Run(() =>
        {
            var view = CreateView();

            Assert.Equal("SQL Memory 復原引導", AutomationProperties.GetName(view));
            Assert.Equal(HorizontalAlignment.Center, view.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, view.VerticalAlignment);
            Assert.Equal(8d, view.CornerRadius.TopLeft);
            Assert.Equal(420d, view.MaxWidth);
        });
    }

    [Fact]
    public void SecondaryActionSitsLeftOfThePrimaryOne()
    {
        WpfTest.Run(() =>
        {
            var view = CreateView();
            var (openFolder, rebuild) = Actions(view);

            // 視覺順序就是 Tab 順序：幽靈按鈕在左，主要動作在右。
            Assert.Equal("開啟資料夾", openFolder.Content);
            Assert.Equal("備份並重建資料庫…", rebuild.Content);
            Assert.Equal(new Thickness(0), openFolder.Margin);
            Assert.Equal(new Thickness(8, 0, 0, 0), rebuild.Margin);
        });
    }

    [Fact]
    public void StatusTextAndRebuildingStateReachTheControls()
    {
        WpfTest.Run(() =>
        {
            var view = CreateView();
            view.SetStatus("資料庫版本不相容", "備份並重建之後從空白開始記錄。");

            var content = Assert.IsType<StackPanel>(view.Child);
            Assert.Equal("資料庫版本不相容", Assert.IsType<TextBlock>(content.Children[1]).Text);
            Assert.Equal("備份並重建之後從空白開始記錄。", Assert.IsType<TextBlock>(content.Children[2]).Text);

            var (openFolder, rebuild) = Actions(view);
            Assert.True(rebuild.IsEnabled);
            Assert.True(openFolder.IsEnabled);

            view.SetRebuilding(true);
            Assert.False(rebuild.IsEnabled);
            Assert.False(openFolder.IsEnabled);
            Assert.Equal("正在重建…", rebuild.Content);

            view.SetRebuilding(false);
            Assert.True(rebuild.IsEnabled);
            Assert.True(openFolder.IsEnabled);
            Assert.Equal("備份並重建資料庫…", rebuild.Content);
        });
    }

    [Fact]
    public void RevealWithoutMotionLandsOnTheFinalStateAndReleasesTheProperty()
    {
        WpfTest.Run(() =>
        {
            var view = CreateView();

            view.Reveal(motion: true);
            view.Reveal(motion: false);

            Assert.Equal(1d, view.Opacity);
            // 上一次的動畫若保留結束值，之後的直接指定會被壓住，「關掉動畫」就會變成關不掉。
            view.Opacity = 0.5;
            Assert.Equal(0.5, view.Opacity);
        });
    }
}
