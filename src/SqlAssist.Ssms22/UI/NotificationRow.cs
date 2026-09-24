using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SqlAssist.Core.Notifications;

namespace SqlAssist.Ssms22.UI;

/// <summary>保留列的身分，只在狀態改變時播放圖示，不因其他任務更新重播整份清單。</summary>
/// <remarks>
/// 列比文字欄寬 <see cref="NotificationLayout.Bleed"/>：停駐與失敗的底色往左右延伸，文字與圖示仍落在
/// 島嶼共用的基準線上（圖示中線、文字起點與右側收邊），跟抬頭、文件列對齊。
/// </remarks>
internal sealed class NotificationRow : Grid
{
    private const string NewLine = "\n";
    private const string Separator = " · ";

    /// <summary>列的上下內距；兩行以內的列至少 28 DIP 高。</summary>
    private const double VerticalPadding = 6;

    private readonly Border _hover;
    private readonly Border _failure;
    private readonly Run _headline = new();
    private readonly Run _subject = new();
    private readonly TextBlock _sourceLine;
    private readonly TextBlock _message;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly TranslateTransform _offset = new();
    private string _headlineText = "";
    private string _sourceText;
    private int _repeat = 1;
    private NotificationVisualStatus? _state;
    private bool _motion;
    private Action? _exited;

    internal NotificationVisualStatus Status => _state ?? NotificationVisualStatus.Pending;
    internal string StatusText { get; private set; } = "";

    public NotificationRow(NotificationActivityItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        MinHeight = 28; ClipToBounds = true;
        // 透明底才收得到整列的停駐；列本身不能按，游標維持箭頭。
        Background = Brushes.Transparent;
        Cursor = System.Windows.Input.Cursors.Arrow;
        RenderTransform = _offset;
        _sourceText = Provenance(item, showDocument: true);

        _failure = Wash().WithTheme(Border.BackgroundProperty, ThemeBrush.NotificationFailure);
        _hover = Wash().WithTheme(Border.BackgroundProperty, ThemeBrush.RowHover);
        Children.Add(_failure);
        Children.Add(_hover);

        var content = new Grid
        {
            Margin = new Thickness(NotificationLayout.Bleed + NotificationLayout.StatusIconInset.Left, VerticalPadding,
                NotificationLayout.Bleed + (NotificationLayout.TextEnd - NotificationLayout.Right), VerticalPadding)
        };
        // 狀態圖示已經往內縮了 2 DIP 對齊中線；文字欄從 TextStart 起算，所以這一欄扣掉那 2 DIP。
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NotificationLayout.IconColumn - NotificationLayout.StatusIconInset.Left) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Icon = new NotificationStatusIcon { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        content.Children.Add(Icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top }; SetColumn(text, 1);
        TitleText = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
        TitleText.Margin = new Thickness(0); TitleText.FontSize = 12; TitleText.FontWeight = FontWeights.Normal;
        TitleText.TextWrapping = TextWrapping.Wrap; TitleText.MaxHeight = 32; TitleText.LineHeight = 16;
        TitleText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        TitleText.TextTrimming = TextTrimming.CharacterEllipsis;
        // 標題是這一列在說的事，主旨是它作用在誰身上：前者正常前景、後者淡色，掃過清單時先讀到動作。
        _subject.SetResourceReference(TextElement.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        TitleText.Inlines.Add(_headline);
        TitleText.Inlines.Add(_subject);
        text.Children.Add(TitleText);
        _sourceLine = Dim(SqlAssistChrome.CreateHint(_sourceText, SqlAssistChrome.DefaultMetrics));
        _sourceLine.TextWrapping = TextWrapping.NoWrap; _sourceLine.TextTrimming = TextTrimming.CharacterEllipsis;
        _sourceLine.ToolTip = _sourceText;
        _sourceLine.Visibility = Visible(_sourceText.Length > 0);
        text.Children.Add(_sourceLine);
        _message = Dim(SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics));
        _message.TextWrapping = TextWrapping.Wrap; _message.LineHeight = 15; _message.MaxHeight = 30;
        _message.TextTrimming = TextTrimming.CharacterEllipsis; _message.Visibility = Visibility.Collapsed;
        text.Children.Add(_message);
        content.Children.Add(text);

        // 中性膠囊而不是狀態色：×N 回答的是次數，狀態仍然只由圖示與狀態文字表達。
        // 高 16 與標題第一行同高，數字等寬，一整欄的徽章右緣與數字都對得齊。
        _badgeText = Dim(SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics));
        _badgeText.FontSize = 10; _badgeText.Margin = new Thickness(0); _badgeText.VerticalAlignment = VerticalAlignment.Center;
        Typography.SetNumeralAlignment(_badgeText, FontNumeralAlignment.Tabular);
        _badge = new Border
        {
            CornerRadius = new CornerRadius(8), Height = 16, Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(SqlAssistChrome.Spacing.Group, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Child = _badgeText
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.BadgeBackground);
        SetColumn(_badge, 2);
        content.Children.Add(_badge);
        Children.Add(content);

        MouseEnter += (_, _) => NotificationMotion.Fade(_hover, 1, NotificationMotion.Wash, _motion);
        MouseLeave += (_, _) => NotificationMotion.Fade(_hover, 0, NotificationMotion.WashOut, _motion);
    }

    internal NotificationStatusIcon Icon { get; }

    internal TextBlock TitleText { get; }

    /// <summary>標題那一行的完整文字；畫面上拆成標題與淡色主旨兩段。</summary>
    internal string Headline => _headlineText;

    internal TextBlock SourceLine => _sourceLine;

    internal TextBlock MessageText => _message;

    internal Border Badge => _badge;

    internal UIElement FailureWash => _failure;

    /// <summary>正在離場；畫面上還在，但已經不是清單的一員。</summary>
    internal bool IsExiting => _exited is not null;

    /// <summary>換上這一輪的內容。</summary>
    /// <remarks>
    /// 措辭已經由呈現端依結果決定好，這裡不判斷結果也不自己拼字。完成之後主要那一行會從
    /// 現在進行式換成過去式並補上耗時，所以每一輪都要重新讀，不能只在建構時寫一次。
    /// </remarks>
    /// <param name="showDocument">文件不是全部相同時才在列上重複顯示；資料庫一律留在列上。</param>
    internal void Update(NotificationActivityItem item, bool showDocument, bool motion)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        var headline = item.Subject.Length > 0 ? item.Title + Separator + item.Subject : item.Title;
        var provenance = Provenance(item, showDocument);
        var source = Visible(provenance.Length > 0);
        var changed = _state != item.Status;
        if (!changed && _headlineText == headline && _repeat == item.Repeat && _motion == motion &&
            StatusText == item.StatusText && _message.Text == item.Message && _sourceLine.Visibility == source &&
            _sourceText == provenance)
            return;
        if (_sourceText != provenance)
        {
            _sourceText = provenance;
            _sourceLine.Text = provenance; _sourceLine.ToolTip = provenance;
        }

        _headlineText = headline; _repeat = item.Repeat; _motion = motion; StatusText = item.StatusText;
        _headline.Text = item.Title;
        _subject.Text = item.Subject.Length > 0 ? Separator + item.Subject : "";
        TitleText.ToolTip = headline;
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

        var first = _state is null;
        _state = item.Status;
        // 列上的執行中是靜態光環：每一列各掛一個 Forever 旋轉等於整份清單長期占著算繪，
        // 而「有事情在跑」由抬頭那一個轉就說得完。第一次出現不是狀態轉換，不播回饋。
        Icon.SetStatus(item.Status, feedback: motion && !first);
        NotificationMotion.Fade(_failure, item.Status == NotificationVisualStatus.Failed ? FailureOpacity : 0,
            NotificationMotion.Wash, motion && !first);
    }

    /// <summary>新列長出高度、淡入，並從上方 4 DIP 落定。</summary>
    /// <param name="width">列會拿到的寬度；量出自然高度後從 0 長到那裡，結束後交還自然高度。</param>
    internal void Enter(bool motion, double width)
    {
        if (!motion) return;
        Measure(new Size(width, double.PositiveInfinity));
        var grow = NotificationMotion.Ease(0, DesiredSize.Height, NotificationMotion.RowEnter);
        grow.FillBehavior = FillBehavior.Stop;
        BeginAnimation(MaxHeightProperty, grow);
        var fade = NotificationMotion.Ease(0, 1, NotificationMotion.RowEnter);
        fade.FillBehavior = FillBehavior.Stop;
        BeginAnimation(OpacityProperty, fade);
        var settle = NotificationMotion.Ease(-NotificationMotion.RowEnterShift, 0, NotificationMotion.RowEnter);
        settle.FillBehavior = FillBehavior.Stop;
        _offset.BeginAnimation(TranslateTransform.YProperty, settle);
    }

    /// <summary>展開時依序進場的第 <paramref name="index"/> 列；延遲期間就停在起點，不先整份閃出來。</summary>
    internal void Stagger(int index, bool motion)
    {
        if (!motion) return;
        var delay = NotificationMotion.ContentDelay + Math.Min(index, NotificationMotion.StaggerLimit - 1) * NotificationMotion.RowStagger;
        BeginAnimation(OpacityProperty, NotificationMotion.Delayed(0, 1, delay, NotificationMotion.RowEnter));
        _offset.BeginAnimation(TranslateTransform.YProperty,
            NotificationMotion.Delayed(NotificationMotion.StaggerShift, 0, delay, NotificationMotion.RowEnter));
    }

    /// <summary>
    /// 離場：先淡出、再收起高度，播完呼叫 <paramref name="exited"/>；動畫關著時立刻呼叫。
    /// </summary>
    /// <remarks>分兩段是為了讓字先消失：同時收高度的話，還看得到的字會被從底下擠扁。</remarks>
    internal void Exit(bool motion, Action exited)
    {
        if (exited is null) throw new ArgumentNullException(nameof(exited));
        _exited = exited;
        IsHitTestVisible = false;
        if (!motion) { Finish(); return; }
        var height = ActualHeight;
        var fade = NotificationMotion.Ease(Opacity, 0, NotificationMotion.RowFadeOut);
        fade.Completed += (_, _) =>
        {
            if (_exited != exited) return;
            var collapse = NotificationMotion.Ease(height, 0, NotificationMotion.RowCollapse);
            collapse.Completed += (_, _) => { if (_exited == exited) Finish(); };
            BeginAnimation(MaxHeightProperty, collapse);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>離場途中同一個身分又回來了：停在原地、恢復成一般的列。</summary>
    internal void CancelExit()
    {
        if (_exited is null) return;
        _exited = null;
        IsHitTestVisible = true;
        BeginAnimation(OpacityProperty, null); BeginAnimation(MaxHeightProperty, null);
    }

    /// <summary>停掉動畫並記住不要再播；島嶼離開畫面時整份清單一起靜音。</summary>
    internal void SuspendMotion()
    {
        _motion = false;
        StopMotion();
    }

    internal void StopMotion()
    {
        Icon.StopMotion();
        foreach (var wash in new UIElement[] { _hover, _failure }) wash.BeginAnimation(OpacityProperty, null);
        _offset.BeginAnimation(TranslateTransform.YProperty, null);
        BeginAnimation(OpacityProperty, null); BeginAnimation(MaxHeightProperty, null);
        if (_exited is not null) Finish();
    }

    /// <summary>
    /// 這一批共同的文件；指向兩份以上文件時回空字串。
    /// </summary>
    /// <remarks>
    /// 沒有文件的列（套件初始化、重建主題筆刷、中繼資料查詢）不參與比較。它們算進來的話，
    /// 一列不屬於任何文件的背景工作就會把抬頭那一行整個收掉，而畫面上的其他列明明都來自
    /// 同一份查詢——那正是檔名時有時無的成因。
    /// </remarks>
    internal static string CommonDocument(IReadOnlyList<NotificationActivityItem> items)
    {
        var common = "";
        for (var index = 0; index < items.Count; index++)
        {
            var document = items[index].Document;
            if (document.Length == 0) continue;
            if (common.Length == 0) common = document;
            else if (!string.Equals(common, document, StringComparison.Ordinal)) return "";
        }

        return common;
    }

    /// <summary>失敗列的底色濃度；失敗色本身已經是圖形對比，鋪滿整列只能是一層淡淡的提示。</summary>
    private const double FailureOpacity = 0.1;

    private void Finish()
    {
        var exited = _exited;
        _exited = null;
        exited?.Invoke();
    }

    private static Border Wash() => new()
    {
        CornerRadius = new CornerRadius(6), Opacity = 0, IsHitTestVisible = false,
    };

    private static TextBlock Dim(TextBlock text)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        text.FontSize = 11; text.Margin = new Thickness(0, 2, 0, 0);
        return text;
    }

    /// <summary>這一列要顯示的出處；抬頭已經寫了文件時只留資料庫。</summary>
    private static string Provenance(NotificationActivityItem item, bool showDocument) =>
        NotificationCatalog.Provenance(showDocument ? item.Document : "", item.Source);

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
