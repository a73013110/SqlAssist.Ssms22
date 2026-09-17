using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlMemoryCleanupViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    static SqlMemoryCleanupViewTests() => SqlIconImage.Factory = icon => new Border { Width = 16, Height = 16, Tag = icon };

    private static (Border Host, SqlMemoryCleanupView View, TextBox Server, ThemeResourceSet Palette) Host()
    {
        var palette = new ThemeResourceSet();
        var server = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
        var database = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
        var view = new SqlMemoryCleanupView(
            SqlAssistChrome.CreateInputBar(SqlIcon.Server, server, SqlAssistChrome.CreateDropDownButton("選擇伺服器")), server,
            SqlAssistChrome.CreateInputBar(SqlIcon.Database, database, SqlAssistChrome.CreateDropDownButton("選擇資料庫")), database)
        { Margin = new Thickness(16) };
        var host = new Border { Child = view };
        host.Resources.MergedDictionaries.Add(palette.Resources);
        host.SetResourceReference(Border.BackgroundProperty, ThemeBrush.WindowBackground);
        return (host, view, server, palette);
    }

    private static CheckBox Target(SqlMemoryCleanupView view, string title) =>
        Descendants<CheckBox>(view).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == title);

    [Fact]
    public void ComposeFollowsTheControlsAndScopeOnlyAppliesToHistoryTargets()
    {
        WpfTest.Run(() =>
        {
            var (host, view, server, _) = Host();
            host.Measure(new Size(560, 700)); host.Arrange(new Rect(0, 0, 560, 700));
            var changes = 0;
            view.Changed += (_, _) => changes++;

            var request = view.Compose(Now)!;
            Assert.Equal(SqlMemoryCleanupTargets.Executions, request.Targets);
            Assert.Equal(Now.AddDays(-30), request.Before);
            Assert.Equal(10, request.KeepFavoriteRevisions);

            server.Text = " LibraryServer ";
            Target(view, "執行紀錄").IsChecked = false;
            Assert.Null(view.Compose(Now));
            Target(view, "收藏的舊版本").IsChecked = true;
            Assert.Equal(3, changes);
            request = view.Compose(Now)!;
            Assert.Equal(SqlMemoryCleanupTargets.FavoriteRevisions, request.Targets);
            Assert.Equal("LibraryServer", request.Server);
            // 只清收藏版本時，期間與連線不適用：整段停用，免得誤以為也會套用。
            var scope = Descendants<StackPanel>(view).Single(panel => System.Windows.Automation.AutomationProperties.GetName(panel) == "History 範圍");
            Assert.False(scope.IsEnabled);

            Target(view, "草稿").IsChecked = true;
            Assert.True(scope.IsEnabled);
            Assert.Equal(SqlMemoryCleanupTargets.Drafts | SqlMemoryCleanupTargets.FavoriteRevisions, view.Compose(Now)!.Targets);
        });
    }

    [Fact]
    public void SubmitIsNeverDefaultAndOnlyEnabledWithAPositiveEstimate()
    {
        WpfTest.Run(() =>
        {
            var (_, view, _, _) = Host();
            Assert.True(view.Cancel.IsDefault);
            Assert.True(view.Cancel.IsCancel);
            Assert.False(view.Submit.IsDefault);

            view.ShowEstimating(hasTargets: true);
            Assert.False(view.Submit.IsEnabled);
            Assert.Equal("清除", view.Submit.Content);

            view.ShowEstimate(new SqlMemoryCleanupEstimate(1200, 34, 0, 0));
            Assert.True(view.Submit.IsEnabled);
            Assert.Equal("清除 1,234 筆", view.Submit.Content);
            Assert.Contains(Descendants<TextBlock>(view), text => text.Text == "執行紀錄 1,200 · 草稿 34");

            // 條件一改就回到試算中：按鈕不留舊筆數，不能拿過期的試算送出。
            view.ShowEstimating(hasTargets: true);
            Assert.False(view.Submit.IsEnabled);
            view.ShowEstimate(new SqlMemoryCleanupEstimate(0, 0, 0, 0));
            Assert.False(view.Submit.IsEnabled);
            view.ShowEstimateFailure("試算失敗");
            Assert.False(view.Submit.IsEnabled);
        });
    }

    [Fact]
    public void ClickingTheRowPaddingTogglesButTheAccessoryDoesNot()
    {
        WpfTest.Run(() =>
        {
            var (host, view, _, _) = Host();
            host.Measure(new Size(560, 700)); host.Arrange(new Rect(0, 0, 560, 700));
            var drafts = Target(view, "草稿");
            var row = (Border)((FrameworkElement)drafts.Parent).Parent;
            row.RaiseEvent(Click(row));
            Assert.True(drafts.IsChecked);

            var favorites = Target(view, "收藏的舊版本");
            var keep = Descendants<ComboBox>(view).Single();
            keep.RaiseEvent(Click(keep));
            Assert.False(favorites.IsChecked);
        });
    }

    [Fact]
    public void CleanupDialogRendersAcrossThemesWidthsAndDpiWithoutHorizontalOverflow()
    {
        WpfTest.Run(() =>
        {
            var (host, view, _, palette) = Host();
            var directory = ThemeVisualTests.FindOutputDirectory();
            host.Measure(new Size(560, 700)); host.Arrange(new Rect(0, 0, 560, 700));
            Target(view, "收藏的舊版本").IsChecked = true;
            foreach (var mode in new[] { "light", "dark", "high-contrast" })
            foreach (var width in new[] { 480, 560 })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                view.ShowEstimate(new SqlMemoryCleanupEstimate(12345, 67, 3, 120));
                host.Measure(new Size(width, double.PositiveInfinity));
                var height = Math.Ceiling(host.DesiredSize.Height);
                host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                foreach (var element in Descendants<FrameworkElement>(view).Where(element => element is Button or ComboBox or TextBox))
                    Assert.InRange(element.TranslatePoint(new Point(element.ActualWidth, 0), host).X, 0, width - 16 + 0.5);
                // 危險動作的靜止底色就是語意色，文字用配對前景；取消維持中性幽靈按鈕。
                var submitBackground = (Border)view.Submit.Template.FindName("bg", view.Submit);
                Assert.Same(palette.Resources[ThemeBrush.DangerBackground], submitBackground.Background);
                Assert.Same(palette.Resources[ThemeBrush.DangerForeground], view.Submit.Foreground);
                foreach (var dpi in new[] { 96, 144 })
                {
                    var bitmap = new RenderTargetBitmap(width * dpi / 96, (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    if (directory is null) continue;
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(directory, $"sql-memory-cleanup-{mode}-{width}-{dpi}.png"));
                    encoder.Save(file);
                }
            }
        });
    }

    private static MouseButtonEventArgs Click(UIElement source) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = source };

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
