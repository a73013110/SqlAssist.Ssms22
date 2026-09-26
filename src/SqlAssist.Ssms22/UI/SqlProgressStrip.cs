using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 清單上方的一條進度：一行淡色說明、一顆停止，底下一條細的進度條。
/// </summary>
/// <remarks>
/// 放在清單<b>上方</b>而不是頁尾：頁尾在清單的最後，列一多就捲不到；進度與停止要一直看得見。
/// 也不蓋在清單上：已經找到的列照樣可以看、可以點。
///
/// 進度條沿用 <see cref="SqlUsageMeter"/>：確定值時平滑推進，還不知道分母時畫來回的掃描線，
/// 兩者與 SQL Memory 用量同一份外觀與動畫規則（受全域動畫設定控制、看不見就停）。
/// 出現與否由宿主決定（通常晚一小段時間才出現，快的那幾輪不會閃一下）。
/// </remarks>
internal sealed class SqlProgressStrip : StackPanel
{
    private readonly TextBlock _text;
    private readonly SqlUsageMeter _meter = new(2);
    private readonly Button _stop;

    public SqlProgressStrip(string stopLabel)
    {
        Visibility = Visibility.Collapsed;

        _text = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _text.TextWrapping = TextWrapping.NoWrap;
        _text.TextTrimming = TextTrimming.CharacterEllipsis;
        _text.VerticalAlignment = VerticalAlignment.Center;

        _stop = SqlAssistChrome.CreateIconButton(SqlIcon.Stop, stopLabel);
        _stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(_stop, Dock.Right);

        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(_stop);
        row.Children.Add(_text);
        Children.Add(row);

        _meter.Margin = new Thickness(0, SqlAssistChrome.Spacing.Tight, 0, 0);
        Children.Add(_meter);

        // 朗讀器讀得到「正在做什麼」；進度條本身只是它的圖。
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Polite);
    }

    /// <summary>使用者按了停止。</summary>
    public event EventHandler? StopRequested;

    public string Text => _text.Text;

    /// <summary>
    /// 顯示這一刻的進度。
    /// </summary>
    /// <param name="text">說明；已經是組好的一句。</param>
    /// <param name="fraction">0 到 1；還不知道分母時為 null，畫不確定的掃描線。</param>
    public void Show(string text, double? fraction)
    {
        _text.Text = text;
        _text.ToolTip = text;

        if (fraction is { } value) _meter.SetValue(value, SqlMemoryUsageSeverity.Normal);
        else _meter.IsIndeterminate = true;

        if (Visibility == Visibility.Visible) return;

        Visibility = Visibility.Visible;
        SqlAssistChrome.PlayAppear(this);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _meter.SetValue(0, SqlMemoryUsageSeverity.Normal, motion: false);
    }
}
