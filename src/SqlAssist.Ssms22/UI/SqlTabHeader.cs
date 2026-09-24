using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 分頁標籤：名稱後面接一個數字，平常是總數，搜尋時換成命中數。
/// </summary>
/// <remarks>
/// 數字放在標籤上而不是另起一行摘要：「這張表有幾個索引」與「索引在哪一頁」是同一件事，
/// 摘要寫一次、分頁再寫一次的那一版，抬頭多佔一行而使用者仍然要點過去才看得到內容。
///
/// 命中數借命中那一組色票（<see cref="ThemeBrush.MatchBackground"/>），不借強調底：
/// 分頁上那一格回答的是「這一頁有幾個符合」，與內容裡標出來的那幾段是同一件事。
/// 零也照寫，淡色——分頁收起來的話，使用者會以為那一頁不見了。
/// </remarks>
internal sealed class SqlTabHeader : StackPanel
{
    private readonly TextBlock _label;
    private readonly Border _chip;
    private readonly TextBlock _count;

    public SqlTabHeader(string label)
    {
        Orientation = Orientation.Horizontal;
        _label = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center }
            .WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        _chip = new Border
        {
            CornerRadius = new CornerRadius(SqlAssistChrome.PillRadius),
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _count
        };
        Children.Add(_label);
        Children.Add(_chip);
        AutomationProperties.SetName(this, label);
    }

    public string Label
    {
        get => _label.Text;
        set
        {
            _label.Text = value;
            UpdateName();
        }
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
        _chip.Visibility = count is null ? Visibility.Collapsed : Visibility.Visible;
        _count.Text = count?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

        if (hits && count > 0)
        {
            _chip.Padding = new Thickness(5, 0, 5, 0);
            _chip.WithTheme(Border.BackgroundProperty, ThemeBrush.MatchBackground);
            _count.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.MatchForeground);
        }
        else
        {
            // 不留底色與內距：總數只是名稱後面淡一階的數字，不是一顆膠囊。
            _chip.Padding = new Thickness(0);
            _chip.ClearValue(Border.BackgroundProperty);
            _count.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        }

        UpdateName();
    }

    private void UpdateName() =>
        AutomationProperties.SetName(this, Count is { } count
            ? string.Format(CultureInfo.CurrentCulture, IsHitCount ? "{0}，{1} 個符合" : "{0}，{1} 項", _label.Text, count)
            : _label.Text);
}
