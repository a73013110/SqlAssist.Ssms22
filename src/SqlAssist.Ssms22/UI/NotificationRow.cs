using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SqlAssist.Core.Notifications;

namespace SqlAssist.Ssms22.UI;

/// <summary>保留列的身分，只在狀態改變時播放圖示，不因其他任務更新重播整份清單。</summary>
internal sealed class NotificationRow : Border
{
    private const string NewLine = "\n";
    private const string Separator = " · ";

    private readonly Path _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _sourceLine;
    private readonly TextBlock _message;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _shake = new();
    private string _headline = "";
    private string _sourceText;
    private int _repeat = 1;
    private NotificationVisualStatus? _state;
    private bool _motion;
    internal NotificationVisualStatus Status => _state ?? NotificationVisualStatus.Pending;
    internal string StatusText { get; private set; } = "";

    public NotificationRow(NotificationCardItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        // 標題獨占文字欄；來源與訊息按需出現，不再平均分配寬度而提早省略。
        MinHeight = 28; Padding = new Thickness(0, 5, 8, 5); ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.Arrow;
        _sourceText = Provenance(item, showDocument: true);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var transform = new TransformGroup();
        transform.Children.Add(_scale); transform.Children.Add(_shake);
        _icon = new Path
        {
            Width = 12, Height = 12, Stretch = Stretch.None, StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = transform
        };
        grid.Children.Add(_icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top }; Grid.SetColumn(text, 1);
        _title = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
        _title.Margin = new Thickness(0); _title.FontSize = 12;
        _title.TextWrapping = TextWrapping.Wrap; _title.MaxHeight = 32; _title.LineHeight = 16;
        _title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(_title);
        _sourceLine = SqlAssistChrome.CreateHint(_sourceText, SqlAssistChrome.DefaultMetrics);
        _sourceLine.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        _sourceLine.FontSize = 11; _sourceLine.Margin = new Thickness(0, 2, 0, 0);
        _sourceLine.TextWrapping = TextWrapping.NoWrap; _sourceLine.TextTrimming = TextTrimming.CharacterEllipsis;
        _sourceLine.ToolTip = _sourceText;
        _sourceLine.Visibility = Visible(_sourceText.Length > 0);
        text.Children.Add(_sourceLine);
        _message = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _message.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        _message.FontSize = 11; _message.Margin = new Thickness(0, 2, 0, 0);
        _message.TextWrapping = TextWrapping.Wrap; _message.LineHeight = 15; _message.MaxHeight = 30;
        _message.TextTrimming = TextTrimming.CharacterEllipsis; _message.Visibility = Visibility.Collapsed;
        text.Children.Add(_message);
        grid.Children.Add(text);
        // 中性膠囊而不是狀態色：×N 回答的是次數，狀態仍然只由圖示與狀態文字表達。
        _badgeText = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _badgeText.FontSize = 10; _badgeText.Margin = new Thickness(0);
        _badgeText.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        _badge = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Child = _badgeText
        }.WithTheme(BackgroundProperty, ThemeBrush.BadgeBackground);
        Grid.SetColumn(_badge, 2);
        grid.Children.Add(_badge);
        Child = grid;
    }

    /// <summary>換上這一輪的內容。</summary>
    /// <remarks>
    /// 措辭已經由呈現端依結果決定好，這裡不判斷結果也不自己拼字。完成之後主要那一行會從
    /// 現在進行式換成過去式並補上耗時，所以每一輪都要重新讀，不能只在建構時寫一次。
    /// </remarks>
    /// <param name="showDocument">文件不是全部相同時才在列上重複顯示；資料庫一律留在列上。</param>
    internal void Update(NotificationCardItem item, bool showDocument, bool motion)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        var headline = item.Subject.Length > 0 ? item.Title + Separator + item.Subject : item.Title;
        var provenance = Provenance(item, showDocument);
        var source = Visible(provenance.Length > 0);
        var changed = _state != item.Status;
        if (!changed && _headline == headline && _repeat == item.Repeat && _motion == motion &&
            StatusText == item.StatusText && _message.Text == item.Message && _sourceLine.Visibility == source &&
            _sourceText == provenance)
            return;
        if (_sourceText != provenance)
        {
            _sourceText = provenance;
            _sourceLine.Text = provenance; _sourceLine.ToolTip = provenance;
        }

        _headline = headline; _repeat = item.Repeat; _motion = motion; StatusText = item.StatusText;
        _title.Text = headline; _title.ToolTip = headline;
        _sourceLine.Visibility = source;
        _message.Text = item.Message; _message.ToolTip = item.Message;
        _message.Visibility = Visible(item.Message.Length > 0);
        _badgeText.Text = "×" + item.Repeat.ToString(CultureInfo.CurrentCulture);
        _badge.ToolTip = "這一列代表 " + item.Repeat.ToString(CultureInfo.CurrentCulture) + " 次相同的工作";
        _badge.Visibility = Visible(item.Repeat > 1);
        AutomationProperties.SetName(_badge, (string)_badge.ToolTip);
        // 輔助技術唸的那一句不受抬頭去重影響：讀出來的人看不到抬頭那一行。
        var full = NotificationCatalog.Provenance(item.Document, item.Source);
        var description = headline + (full.Length > 0 ? NewLine + full : "");
        ToolTip = description + NewLine + item.StatusText +
            (item.Message.Length > 0 ? NewLine + item.Message : "") +
            (item.Repeat > 1 ? NewLine + (string)_badge.ToolTip : "");
        AutomationProperties.SetName(this, (string)ToolTip);
        if (!changed)
        {
            if (!motion) StopMotion();
            return;
        }

        _state = item.Status;
        StopMotion();
        ApplyIcon(item.Status);
        // 列上的執行中是靜態光環：每一列各掛一個 Forever 旋轉等於整份清單長期占著算繪，
        // 而「有事情在跑」由抬頭那一個轉就說得完。
        if (motion) SqlAssistChrome.AnimateNotificationResult(_scale, _shake, item.Status);
    }

    private void ApplyIcon(NotificationVisualStatus state)
    {
        var role = ThemeBrush.DimForeground;
        string geometry;
        switch (state)
        {
            case NotificationVisualStatus.Running:
                role = ThemeBrush.AccentBorder;
                geometry = "M8,1 A7,7 0 1 1 1,8"; break;
            case NotificationVisualStatus.Completed:
                role = ThemeBrush.NotificationSuccess;
                geometry = "M3,8 L6.5,11.5 L13,4.5"; break;
            case NotificationVisualStatus.Failed:
                role = ThemeBrush.NotificationFailure;
                geometry = "M8,1 L15,14 L1,14 Z M8,5 L8,9 M8,11 L8,12"; break;
            case NotificationVisualStatus.Canceled:
                geometry = "M3,3 L13,13 M13,3 L3,13"; break;
            default:
                geometry = "M8,1 A7,7 0 1 1 7.99,1 M8,4 L8,8 L11,10"; break;
        }

        _icon.Data = SqlAssistChrome.NotificationGeometry(geometry);
        _icon.WithTheme(Shape.StrokeProperty, role);
        if (state == NotificationVisualStatus.Running)
            _icon.SetResourceReference(Shape.StrokeProperty, ThemeResourceSet.NotificationSpinnerKey);
    }

    internal void Reveal(bool motion, double availableWidth)
    {
        if (!motion) return;
        BeginAnimation(OpacityProperty, SqlAssistChrome.NotificationAnimation(0, 1, 240));
        // UI 合併更新時，新列可能已完成；高度歸零會遮掉大部分狀態彈跳。
        if (Status != NotificationVisualStatus.Running) return;
        // 高度展開不縮放文字，結束後回到自然高度以容納換行與 DPI 改變。
        Measure(new Size(availableWidth, double.PositiveInfinity));
        BeginAnimation(MaxHeightProperty, new DoubleAnimation(0, DesiredSize.Height, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>停掉動畫並記住不要再播；卡片離開畫面時整份清單一起靜音。</summary>
    internal void SuspendMotion()
    {
        _motion = false;
        StopMotion();
    }

    internal void StopMotion()
    {
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _shake.BeginAnimation(TranslateTransform.XProperty, null);
        BeginAnimation(OpacityProperty, null); BeginAnimation(MaxHeightProperty, null);
    }

    /// <summary>這一列要顯示的出處；抬頭已經寫了文件時只留資料庫。</summary>
    private static string Provenance(NotificationCardItem item, bool showDocument) =>
        NotificationCatalog.Provenance(showDocument ? item.Document : "", item.Source);

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
