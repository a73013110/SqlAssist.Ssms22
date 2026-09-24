using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using SqlAssist.Core.Notifications;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知島上的一則提醒：語意圖示、標題、訊息、按鈕列、「稍後提醒」的叉號，以及有活動時底部的附條。
/// </summary>
/// <remarks>
/// 按鈕照 UI 準則「一個視窗只有一個主要動作；放在右側並給淡底」：次要動作是幽靈按鈕
/// 排在左邊，主要動作在最右。Tab 順序跟視覺順序：右上角的叉號、按鈕列、最後是底部的附條。
/// 版面只認得 <see cref="NotificationPromptItem"/>；按下去只把 (提醒 Id、按鈕 Id) 交出去，
/// 處理與保存決定是呼叫端的事。圖示、標題與叉號照 <see cref="NotificationLayout"/> 的基準線排，
/// 與活動清單的抬頭同一個位置，兩種形態互換時不跳。
/// </remarks>
internal sealed class NotificationPromptView : Grid
{
    /// <summary>訊息最多幾行；完整內容在 ToolTip。</summary>
    internal const int MessageLines = 3;

    /// <summary>卡片的下距；附條貼到底邊時用它把自己推出去。</summary>
    private const double Bottom = 10;

    private const double LineHeight = 16;

    private readonly SqlIconImage _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly TextBlock _position;
    private readonly StackPanel _buttons;

    public NotificationPromptView()
    {
        Margin = new Thickness(NotificationLayout.Left, NotificationLayout.Top, NotificationLayout.Right, Bottom);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NotificationLayout.IconColumn) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(NotificationLayout.HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _icon = SqlAssistChrome.CreateIcon(SqlIcon.Information);
        _icon.HorizontalAlignment = HorizontalAlignment.Left;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_icon);

        // 與清單抬頭同一種字重：兩者都是這張卡片的標題，形態互換時不該像換了一種字。
        _title = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
        _title.Margin = new Thickness(0); _title.FontSize = 12;
        _title.TextTrimming = TextTrimming.CharacterEllipsis; _title.TextWrapping = TextWrapping.NoWrap;
        _title.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_title, 1);
        Children.Add(_title);

        var corner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _position = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _position.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        _position.FontSize = 11; _position.Margin = new Thickness(SqlAssistChrome.Spacing.Group, 0, SqlAssistChrome.Spacing.Tight, 0);
        _position.VerticalAlignment = VerticalAlignment.Center; _position.Visibility = Visibility.Collapsed;
        corner.Children.Add(_position);
        LaterButton = SqlAssistChrome.CreateNotificationButton(NotificationCatalog.PromptLater, "M1,1 L11,11 M11,1 L1,11");
        LaterButton.Click += (_, _) => Invoke(null);
        corner.Children.Add(LaterButton);
        SetColumn(corner, 2);
        Children.Add(corner);

        _message = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _message.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        // 標題列 24 高、字置中，本身已經在標題下方留出 4 DIP，訊息不再另加上距。
        _message.FontSize = 12; _message.Margin = new Thickness(0);
        _message.TextWrapping = TextWrapping.Wrap; _message.TextTrimming = TextTrimming.CharacterEllipsis;
        _message.LineHeight = LineHeight; _message.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _message.MaxHeight = LineHeight * MessageLines;
        SetRow(_message, 1); SetColumn(_message, 1); SetColumnSpan(_message, 2);
        Children.Add(_message);

        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, SqlAssistChrome.Spacing.Group + SqlAssistChrome.Spacing.Tight, 0, 0)
        };
        SetRow(_buttons, 2); SetColumn(_buttons, 1); SetColumnSpan(_buttons, 2);
        Children.Add(_buttons);

        ActivityStrip = new NotificationActivityStrip { Visibility = Visibility.Collapsed };
        ActivityStrip.Place(gap: Bottom, bottom: Bottom);
        SetRow(ActivityStrip, 3); SetColumnSpan(ActivityStrip, 3);
        Children.Add(ActivityStrip);

        // 提醒要使用者決定，出現時要唸出來、打斷其他播報；活動只是 Polite。
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
    }

    /// <summary>按了某顆按鈕（識別字）或叉號（null）。</summary>
    public event Action<long, string?>? ActionInvoked;

    internal Button LaterButton { get; }

    /// <summary>有活動時貼在卡片底部的那一條；顯示與內容由島嶼決定，這裡只負責位置。</summary>
    internal NotificationActivityStrip ActivityStrip { get; }

    internal IReadOnlyList<Button> ActionButtons { get; private set; } = Array.Empty<Button>();

    public NotificationPromptItem? Item { get; private set; }

    public void Update(NotificationPromptItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        var fresh = Item?.Id != item.Id;
        Item = item;
        _icon.Icon = item.Severity switch
        {
            NotificationPromptSeverity.Error => SqlIcon.Error,
            NotificationPromptSeverity.Warning => SqlIcon.Warning,
            _ => SqlIcon.Information,
        };
        _title.Text = item.Title; _title.ToolTip = item.Title;
        _message.Text = item.Message; _message.ToolTip = item.Message.Length > 0 ? item.Message : null;
        _message.Visibility = item.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _position.Text = NotificationCatalog.PromptPosition(item.Position, item.Count);
        _position.Visibility = item.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_position, _position.Text);
        if (fresh) BuildButtons(item.Actions);
        var name = item.Title + (item.Message.Length > 0 ? "\n" + item.Message : "");
        AutomationProperties.SetName(this, name);
        if (fresh) UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void BuildButtons(IReadOnlyList<NotificationPromptAction> actions)
    {
        _buttons.Children.Clear();
        var buttons = new List<Button>(actions.Count);
        // 次要在左、主要在最右；同一分量內保留目錄給的順序。
        foreach (var primary in new[] { false, true })
            foreach (var action in actions)
            {
                if (action.Primary != primary) continue;
                var button = SqlAssistChrome.CreateButton(action.Label, SqlAssistChrome.DefaultMetrics, primary);
                button.MinWidth = 0; button.FontSize = 12; button.Padding = new Thickness(10, 3, 10, 4);
                button.Margin = new Thickness(buttons.Count == 0 ? 0 : SqlAssistChrome.Spacing.Tight, 0, 0, 0);
                button.ToolTip = action.Label;
                AutomationProperties.SetName(button, action.Label);
                var id = action.Id;
                button.Click += (_, _) => Invoke(id);
                _buttons.Children.Add(button);
                buttons.Add(button);
            }

        ActionButtons = buttons;
    }

    private void Invoke(string? actionId)
    {
        if (Item is { } item) ActionInvoked?.Invoke(item.Id, actionId);
    }
}
