using System;
using System.Windows;
using System.Windows.Automation;
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
        SqlAssistDialogs.Configure(this, "SqlAssist — 儲存格內容", 760, 520, minWidth: 420, minHeight: 260);

        _statusText = SqlAssistChrome.CreateStatusText(Metrics);
        Content = BuildLayout();
    }

    private Grid BuildLayout()
    {
        var root = new Grid { Margin = SqlAssistChrome.DialogPadding };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var wrap = new CheckBox
        {
            Content = "自動換行",
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Template = SqlAssistChrome.CreateCheckBoxTemplate(),
            ToolTip = "只改變顯示方式；複製時仍保留原始換行與空白。"
        }.WithTheme(CheckBox.ForegroundProperty, ThemeBrush.ListForeground);
        DockPanel.SetDock(wrap, Dock.Right);
        toolbar.Children.Add(wrap);
        toolbar.Children.Add(SqlAssistChrome.CreateMetadataText(_cell.Headline, Metrics));
        root.Children.Add(toolbar);

        var content = SqlAssistChrome.CreateTextBox(Metrics);
        content.Text = _cell.Text;
        content.IsReadOnly = true;
        content.IsReadOnlyCaretVisible = true;
        content.AcceptsReturn = true;
        content.TextWrapping = TextWrapping.NoWrap;
        content.FontFamily = SqlAssistChrome.CodeFont;
        content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        content.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        content.Padding = new Thickness(12);
        AutomationProperties.SetName(content, "儲存格完整內容（唯讀）");
        // 不換行時一行 XML 可以長到幾千字元；WPF 原生不認 Shift＋滾輪，少了這一道只剩拖捲軸。
        // 切成自動換行之後沒有水平捲軸，那一刻它不攔滾輪，垂直捲動照舊。
        SqlAssistChrome.ApplyShiftWheelPan(content);

        // 只切換排版，不重設 Text，保留原文與既有選取。
        wrap.Checked += (_, _) => content.TextWrapping = TextWrapping.Wrap;
        wrap.Unchecked += (_, _) => content.TextWrapping = TextWrapping.NoWrap;

        var body = new Grid();
        body.Children.Add(content);
        if (_cell.Text.Length == 0)
        {
            // 說明是覆蓋層，不混進 Text，避免把 NULL 或空字串複製成提示文字。
            var empty = SqlAssistChrome.CreateHint(
                _cell.IsNull ? "NULL — 這一格沒有值" : "空內容 — 長度為 0", Metrics);
            empty.Margin = new Thickness(16);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            empty.IsHitTestVisible = false;
            body.Children.Add(empty);
        }
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var copy = SqlAssistChrome.CreateButton("複製全部", Metrics);
        copy.ToolTip = "複製完整原文；也可在內容中選取後按 Ctrl+C。";

        // NULL 沒有東西可以複製，而一顆按下去什麼都不會發生的按鈕比停用的按鈕難懂。
        copy.IsEnabled = !_cell.IsNull;
        copy.Click += OnCopy;

        var close = SqlAssistChrome.CreateButton("關閉", Metrics, primary: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        var footer = SqlAssistChrome.CreateDialogFooter(new[] { copy }, _statusText, close);
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
