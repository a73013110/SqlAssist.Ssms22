using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Core.Localization;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 分頁標籤：語意圖示（可省）、名稱，名稱後面接一個數字，平常是總數，搜尋時換成命中數。
/// </summary>
/// <remarks>
/// 每一個分頁都由 <see cref="SqlAssistChrome.CreateTab(SqlTabHeader, object?)"/> 掛上這一份，
/// SQL Memory、結構預覽與關於視窗是同一種標籤。名稱的前景繫結擁有它的分頁（與工具列按鈕同一條路），
/// 不靠樣板往下繼承：宿主的隱含 TextBlock 樣式會蓋掉繼承值，深色主題裡就是一排黑字。
///
/// 數字放在標籤上而不是另起一行摘要：「這張表有幾個索引」與「索引在哪一頁」是同一件事，
/// 摘要寫一次、分頁再寫一次的那一版，抬頭多佔一行而使用者仍然要點過去才看得到內容。
///
/// 命中數借命中那一組色票（<see cref="ThemeBrush.MatchBackground"/>），不借強調底：
/// 分頁上那一格回答的是「這一頁有幾個符合」，與內容裡標出來的那幾段是同一件事。
/// 零也照寫，淡色——分頁收起來的話，使用者會以為那一頁不見了。數字那一格第一次有數字時才建，
/// 不帶數字的分頁（SQL Memory）不多掛一組看不見的元素。
/// </remarks>
internal sealed class SqlTabHeader : StackPanel
{
    private readonly TextBlock _label;
    private Border? _chip;
    private TextBlock? _count;

    public SqlTabHeader(string label, SqlIcon? icon = null)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        if (icon is { } glyph)
        {
            Glyph = new Grid { Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            Glyph.Children.Add(SqlAssistChrome.CreateIcon(glyph));
            Children.Add(Glyph);
        }

        _label = SqlAssistChrome.CreateButtonText(label);
        Children.Add(_label);
        UpdateName();
    }

    /// <summary>圖示的承載格；要在圖示上疊狀態點（例如用量分頁的容量分級）時加在這裡。沒有圖示時 null。</summary>
    public Grid? Glyph { get; }

    public string Label
    {
        get => _label.Text;
        set
        {
            _label.Text = value;
            UpdateName();
        }
    }

    /// <summary>窄窗收起名稱只留圖示；名稱仍在自動化名稱裡，Tooltip 由收起它的那一方負責。</summary>
    public bool ShowsLabel
    {
        get => _label.Visibility == Visibility.Visible;
        set => _label.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>目前那一格數字；沒有時 null。</summary>
    public int? Count { get; private set; }

    /// <summary>數字是不是搜尋的命中數。</summary>
    public bool IsHitCount { get; private set; }

    /// <summary>標上總數；null 把那一格收掉。</summary>
    public void ShowTotal(int? count) => Show(count, hits: false);

    /// <summary>標上命中數；零也照寫。</summary>
    public void ShowHits(int count) => Show(count, hits: true);

    private void Show(int? count, bool hits)
    {
        Count = count;
        IsHitCount = hits;

        if (count is null && _chip is null)
        {
            UpdateName();
            return;
        }

        var (chip, text) = EnsureChip();
        chip.Visibility = count is null ? Visibility.Collapsed : Visibility.Visible;
        text.Text = count is { } value ? SqlText.Number(value) : string.Empty;

        if (hits && count > 0)
        {
            chip.Padding = new Thickness(5, 0, 5, 0);
            chip.WithTheme(Border.BackgroundProperty, ThemeBrush.MatchBackground);
            text.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.MatchForeground);
        }
        else
        {
            // 不留底色與內距：總數只是名稱後面淡一階的數字，不是一顆膠囊。
            chip.Padding = new Thickness(0);
            chip.ClearValue(Border.BackgroundProperty);
            text.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        }

        UpdateName();
    }

    private (Border Chip, TextBlock Text) EnsureChip()
    {
        if (_chip is null || _count is null)
        {
            _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _chip = new Border
            {
                CornerRadius = new CornerRadius(SqlAssistChrome.PillRadius),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _count
            };
            Children.Add(_chip);
        }

        return (_chip, _count);
    }

    private void UpdateName() =>
        AutomationProperties.SetName(this, Count is { } count
            ? IsHitCount ? ChromeText.TabHitCount(_label.Text, count) : ChromeText.TabItemCount(_label.Text, count)
            : _label.Text);
}
