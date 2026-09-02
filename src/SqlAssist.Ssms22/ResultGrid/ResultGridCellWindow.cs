using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Metadata.ResultGrid;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.ResultGrid;

/// <summary>
/// 一格的完整內容。
/// </summary>
/// <remarks>
/// 結果格線一列只有一行高，而它顯示的字數上限是 65535——一段
/// <c>nvarchar(max)</c> 的 XML 在格線上只看得到開頭那幾十個字，而且沒有任何
/// 「後面還有」的提示。
///
/// 內容用唯讀的多行輸入欄位而不是 <c>TextBlock</c>：使用者要能選、能捲、
/// 能按 Ctrl+C 帶走其中一段。<c>TextBlock</c> 選不了字，而那是這個視窗最常見的
/// 下一步。字型用程式碼字型，因為裡面多半是 XML、JSON 或十六進位——
/// 等寬才對得齊。
/// </remarks>
internal sealed class ResultGridCellWindow : DialogWindow
{
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    private readonly ResultGridCellText _cell;
    private readonly TextBlock _statusText;

    public ResultGridCellWindow(ResultGridCellText cell)
    {
        _cell = cell;

        Title = "SqlAssist — 儲存格內容";
        Width = 760;
        Height = 520;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = VsThemeBrushes.WindowBackground;
        Foreground = VsThemeBrushes.WindowForeground;
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = Metrics.Body;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _statusText = SqlAssistChrome.CreateStatusText(Metrics);
        Content = BuildLayout();
    }

    private Grid BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = SqlAssistChrome.CreateLabel(_cell.Headline, Metrics);
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var content = SqlAssistChrome.CreateTextBox(Metrics);
        content.Text = _cell.Text;
        content.IsReadOnly = true;
        content.AcceptsReturn = true;
        content.TextWrapping = TextWrapping.NoWrap;
        content.FontFamily = SqlAssistChrome.CodeFont;
        content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        content.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        content.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };

        var copy = SqlAssistChrome.CreateButton("複製全部", Metrics);
        copy.MinWidth = 78;
        copy.Margin = new Thickness(0, 0, 6, 0);

        // NULL 沒有東西可以複製，而一顆按下去什麼都不會發生的按鈕比停用的按鈕難懂。
        copy.IsEnabled = !_cell.IsNull;
        copy.Click += OnCopy;

        var close = SqlAssistChrome.CreateButton("關閉", Metrics, primary: true);
        close.MinWidth = 78;
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        DockPanel.SetDock(copy, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(copy);
        footer.Children.Add(close);
        footer.Children.Add(_statusText);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private void OnCopy(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Clipboard.SetText(_cell.Text);
            _statusText.Text = "已複製這一格的完整內容。";
        }
        catch (Exception exception)
        {
            // 剪貼簿被別的程序鎖住時會擲例外，這不值得關掉視窗。
            SqlAssistDiagnostics.WriteAlways($"複製儲存格內容失敗：{exception.Message}");
            _statusText.Text = $"複製失敗：{exception.Message}";
        }
    }
}
