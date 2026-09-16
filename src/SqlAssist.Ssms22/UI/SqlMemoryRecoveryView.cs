using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// SQL Memory 資料庫開不起來時的引導卡片：說明狀況，並提供備份重建與開啟資料夾。
/// </summary>
/// <remarks>
/// 這是清單的錯誤狀態，不是對話框：留在工具窗中央、沿用共用外觀，
/// 不另外彈視窗打斷正在寫的 SQL。破壞性的那一步由呼叫端再開確認框。
/// </remarks>
internal sealed class SqlMemoryRecoveryView : Border
{
    private const string RebuildLabel = "備份並重建資料庫…";

    private readonly TextBlock _title;
    private readonly TextBlock _description;
    private readonly Button _rebuild;
    private readonly Button _openFolder;

    public event EventHandler? RebuildRequested;
    public event EventHandler? OpenFolderRequested;

    public SqlMemoryRecoveryView()
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(24);
        Margin = new Thickness(16);
        MaxWidth = 420;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        SetResourceReference(BackgroundProperty, ThemeBrush.BadgeBackground);
        SetResourceReference(BorderBrushProperty, ThemeBrush.Hairline);
        AutomationProperties.SetName(this, "SQL Memory 復原引導");

        var content = new StackPanel();

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
            Child = new SqlIconImage
            {
                Icon = SqlIcon.Database,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        badge.SetResourceReference(BackgroundProperty, ThemeBrush.AccentBackground);
        badge.SetResourceReference(BorderBrushProperty, ThemeBrush.AccentBorder);
        content.Children.Add(badge);

        _title = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = SqlAssistChrome.DefaultMetrics.Title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        content.Children.Add(_title);

        _description = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = SqlAssistChrome.DefaultMetrics.Caption,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        _description.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        content.Children.Add(_description);

        // 次要在左、主要在右；卡片本身置中，動作列跟著內容置中而不是靠齊視窗邊。
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _openFolder = SqlAssistChrome.CreateButton("開啟資料夾", SqlAssistChrome.DefaultMetrics);
        _openFolder.MinWidth = 80;
        _openFolder.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(_openFolder, "開啟 SQL Memory 資料庫資料夾");

        _rebuild = SqlAssistChrome.CreateButton(RebuildLabel, SqlAssistChrome.DefaultMetrics, primary: true);
        _rebuild.MinWidth = 130;
        _rebuild.Margin = new Thickness(8, 0, 0, 0);
        _rebuild.Click += (_, _) => RebuildRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(_rebuild, "備份並重建 SQL Memory 資料庫");

        actions.Children.Add(_openFolder);
        actions.Children.Add(_rebuild);
        content.Children.Add(actions);

        Child = content;
    }

    public void SetStatus(string title, string description)
    {
        _title.Text = title;
        _description.Text = description;
    }

    /// <summary>重建期間兩顆都停用：檔案正在搬，開資料夾看到的是搬到一半的樣子。</summary>
    public void SetRebuilding(bool rebuilding)
    {
        _rebuild.IsEnabled = !rebuilding;
        _openFolder.IsEnabled = !rebuilding;
        _rebuild.Content = rebuilding ? "正在重建…" : RebuildLabel;
    }

    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public void Reveal(bool? motion = null) => SqlAssistChrome.PlayAppear(this, motion);
}
