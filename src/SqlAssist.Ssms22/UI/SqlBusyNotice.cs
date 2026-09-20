using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 面板裡的一行狀態：正在讀取，或讀不到。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlStateSurface"/> 分工清楚：那一塊蓋住整份內容，說的是「現在沒有東西可看」；
/// 這一行掛在清單旁邊，說的是「清單還在路上，或這一份就是全部」。彈出面板不能用前者——
/// 蓋住的是一份使用者還在看的選項清單，而他正要從裡面挑一個。
///
/// 轉圈只在忙碌、可見且動畫開著時跑：停靠面板裡的下拉關掉之後仍留在視覺樹上，
/// 少了這一道就是一個看不見的圈永遠佔著算繪。
/// </remarks>
internal sealed class SqlBusyNotice : Border
{
    /// <summary>轉圈的直徑；與一行說明文字同高，不把那一列撐大。</summary>
    private const double SpinnerSize = 12;

    private readonly RotateTransform _rotation = new();
    private readonly FrameworkElement _spinner;
    private readonly TextBlock _text = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private bool _busy;

    public SqlBusyNotice()
    {
        var arc = new Path
        {
            Data = Geometry.Parse("M 11,6 A 5,5 0 1 1 6,1"),
            Width = SpinnerSize,
            Height = SpinnerSize,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotation,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        }.WithTheme(Shape.StrokeProperty, ThemeBrush.DimForeground);
        _spinner = arc;

        _text.TextTrimming = TextTrimming.None;
        _text.TextWrapping = TextWrapping.Wrap;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_spinner);
        row.Children.Add(_text);

        Child = row;
        Padding = new Thickness(2, 4, 2, 4);
        Visibility = Visibility.Collapsed;

        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>目前這一行在說什麼；空字串表示整列收起。</summary>
    public string Message => _text.Text;

    /// <summary>
    /// 換一行話。
    /// </summary>
    /// <param name="message">空字串收起整列；收起用 <see cref="Visibility.Collapsed"/>，不留一條空白。</param>
    /// <param name="busy">還在等結果；只有這時候才轉。</param>
    public void Show(string message, bool busy = false)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        _busy = busy && message.Length != 0;
        _text.Text = message;
        _spinner.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, message);
        UpdateAnimation();
    }

    /// <summary>收起整列。</summary>
    public void Clear() => Show("");

    private void UpdateAnimation()
    {
        // 動畫關閉時留靜態圖示；那一行的字本身已經說了在等什麼。
        _rotation.BeginAnimation(RotateTransform.AngleProperty,
            _busy && IsVisible && SqlAssistChrome.MotionEnabled
                ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);
    }
}
