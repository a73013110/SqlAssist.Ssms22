using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>膠囊的色調；強調只給真正的重點，一個表面最多一顆。</summary>
internal enum SqlPillTone
{
    Neutral,
    Accent
}

/// <summary>
/// 一顆帶圖示插槽的膠囊，內容由宿主隨顯示對象換。
/// </summary>
/// <remarks>
/// 清單列上的膠囊是資料樣板（<c>SqlAssistChrome.CreateBadge</c>），這一份給沒有資料列的表面，
/// 例如浮動預覽的抬頭。形狀取同一組常數（<see cref="SqlAssistChrome.PillRadius"/>）：
/// 同一種事實在清單上與抬頭上長得不一樣，使用者會以為是兩種東西。
///
/// 色調只換底與框，字色一律是一般前景——與旗標欄的主索引鍵徽章同一個做法，
/// 強調不靠字色，高對比主題上才不會變成一顆讀不出字的色塊。
/// </remarks>
internal sealed class SqlPill : Border
{
    private readonly TextBlock _text;
    private SqlPillTone _tone;

    public SqlPill()
        : this(null)
    {
    }

    public SqlPill(SqlIcon icon)
        : this(new SqlIconImage { Icon = icon })
    {
    }

    /// <param name="glyph">圖示插槽；null 就只有字。插槽不共用，每一顆持有自己的影像。</param>
    public SqlPill(FrameworkElement? glyph)
    {
        Glyph = glyph;
        CornerRadius = new CornerRadius(SqlAssistChrome.PillRadius);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(6, 1, 7, 1);
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;

        _text = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);

        var content = new DockPanel();
        if (glyph is not null)
        {
            glyph.Margin = new Thickness(0, 0, 4, 0);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(glyph, Dock.Left);
            content.Children.Add(glyph);
        }

        content.Children.Add(_text);
        Child = content;
        ApplyTone();
    }

    public FrameworkElement? Glyph { get; }

    /// <summary>膠囊上的字；也是自動化名稱，窄到被省略時仍唸得出來。</summary>
    public string Text
    {
        get => _text.Text;
        set
        {
            _text.Text = value;
            AutomationProperties.SetName(this, value);
        }
    }

    public double TextSize
    {
        get => _text.FontSize;
        set => _text.FontSize = value;
    }

    public SqlPillTone Tone
    {
        get => _tone;
        set
        {
            if (_tone == value)
            {
                return;
            }

            _tone = value;
            ApplyTone();
        }
    }

    private void ApplyTone()
    {
        var accent = _tone == SqlPillTone.Accent;
        this.WithTheme(BackgroundProperty, accent ? ThemeBrush.AccentBackground : ThemeBrush.BadgeBackground)
            .WithTheme(BorderBrushProperty, accent ? ThemeBrush.AccentBorder : ThemeBrush.Hairline);
    }
}
