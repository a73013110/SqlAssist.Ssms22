using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class ThemeVisualTests
{
    [Fact]
    public void SharedControlsRenderAfterRepeatedThemeChangesAtMultipleDpi()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var metrics = SqlAssistChrome.DefaultMetrics;
            var content = new StackPanel { Margin = new Thickness(16) };
            content.Children.Add(SqlAssistChrome.CreateLabel("SqlAssist — 共用控制項主題檢查", metrics));
            content.Children.Add(SqlAssistChrome.CreateHint("同一棵視覺樹反覆切換配色，不重新建立控制項。", metrics));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
            buttons.Children.Add(SqlAssistChrome.CreateButton("複製全部", metrics, primary: true));
            buttons.Children.Add(SqlAssistChrome.CreateButton("關閉", metrics));
            buttons.Children.Add(new CheckBox
            {
                Content = "展開後顯示建議",
                IsChecked = true,
                Margin = new Thickness(16, 4, 0, 0),
                Template = SqlAssistChrome.CreateCheckBoxTemplate()
            }.WithTheme(Control.ForegroundProperty, ThemeBrush.ListForeground));
            content.Children.Add(buttons);

            var text = SqlAssistChrome.CreateTextBox(metrics);
            text.Text = "SELECT LoanId FROM Loan;";
            text.FontFamily = SqlAssistChrome.CodeFont;
            content.Children.Add(text);

            var list = new ListBox
            {
                Margin = new Thickness(0, 12, 0, 12),
                BorderThickness = default,
                ItemContainerStyle = SqlAssistChrome.CreateListItemStyle(metrics),
                SelectedIndex = 1
            }.WithTheme(Control.BackgroundProperty, ThemeBrush.ListBackground);
            list.Items.Add("Loan — 借閱資料");
            list.Items.Add("LoanDetail — 借閱明細（已選取）");
            content.Children.Add(list);

            var grid = SqlAssistChrome.CreateDataGrid(metrics);
            grid.Height = 160;
            grid.IsReadOnly = true;
            grid.Columns.Add(new DataGridTextColumn { Header = "欄位", Binding = new System.Windows.Data.Binding("Name"), Width = 240 });
            grid.Columns.Add(new DataGridTextColumn { Header = "型別", Binding = new System.Windows.Data.Binding("Type"), Width = 140 });
            grid.ItemsSource = new[]
            {
                new { Name = "LoanId", Type = "int" },
                new { Name = "LoanDate", Type = "datetime2" },
                new { Name = "CopyNo", Type = "nvarchar(20)" }
            };
            content.Children.Add(grid);
            content.Children.Add(SqlAssistChrome.CreateHint("樣式、選取文字、交替列及輸入欄位應保持可讀。", metrics));

            var root = new Border { Child = content }
                .WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
            root.Resources.MergedDictionaries.Add(palette.Resources);
            var directory = FindOutputDirectory();
            foreach (var mode in new[] { "light", "dark", "high-contrast", "light-again" })
            {
                var dark = mode is "dark" or "high-contrast";
                var colors = ThemeResourceSetTests.ColorsFor(dark);
                var foreground = dark ? Colors.White : Colors.Black;
                colors[ThemeBrush.DimForeground] = foreground;
                colors[ThemeBrush.Border] = foreground;
                colors[ThemeBrush.AccentBorder] = foreground;
                colors[ThemeBrush.Hairline] = foreground;
                colors[ThemeBrush.RowSelected] = mode == "high-contrast" ? Colors.Yellow : dark ? Colors.DimGray : Colors.LightGray;
                colors[ThemeBrush.RowAlternate] = mode == "high-contrast" ? Colors.Black : dark ? Color.FromRgb(32, 32, 32) : Colors.WhiteSmoke;
                colors[ThemeBrush.SelectedForeground] = mode == "high-contrast" ? Colors.Black : foreground;
                palette.Update(colors);

                foreach (var dpi in new[] { 96, 144, 192 })
                {
                    root.Measure(new Size(720, 460));
                    root.Arrange(new Rect(0, 0, 720, 460));
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(720 * dpi / 96, 460 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    Assert.Equal(720 * dpi / 96, bitmap.PixelWidth);
                    if (directory is not null)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, $"{mode}-{dpi}.png"));
                        encoder.Save(file);
                    }
                }
            }
        });
    }

    private static string? FindOutputDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SqlAssist.Ssms22.sln")))
            {
                var directory = Path.Combine(current.FullName, "artifacts", "theme-qa");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        return null;
    }
}
