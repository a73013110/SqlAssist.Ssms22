using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 載入、空、錯誤與無權限四種狀態的唯一表面；疊在內容上，不另佔一塊版面或狀態列。
/// </summary>
/// <remarks>
/// 四種狀態共用一塊區域，因為它們互斥：任何一種成立時內容都沒有東西可讀。各給一塊
/// 版面的症狀是右側停靠的工具窗只剩兩三行放清單，而其中兩塊永遠是空的。
///
/// 錯誤與無權限只差抬頭那一句，走同一條呈現路徑；動畫沿用
/// <see cref="SqlAssistChrome.PlayAppear"/>，不可見時停轉，不讓背景工具窗持續算繪。
/// </remarks>
internal sealed class SqlStateSurface : Grid
{
    private readonly FrameworkElement _indicator;
    private readonly RotateTransform _rotation = new();
    private readonly StackPanel _message = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 320,
        Margin = new Thickness(24, 0, 24, 0),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private readonly SqlIconImage _icon = new() { Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private SqlSurfaceState _state;

    public SqlStateSurface(UIElement content)
    {
        Children.Add(content);

        _title = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = SqlAssistChrome.DefaultMetrics.Body,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        _detail = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _detail.TextAlignment = TextAlignment.Center;
        _message.Children.Add(_icon);
        _message.Children.Add(_title);
        _message.Children.Add(_detail);
        Children.Add(_message);

        _indicator = SqlAssistChrome.CreateLoadingIndicator(_rotation);
        _indicator.Visibility = Visibility.Collapsed;
        Children.Add(_indicator);

        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        Apply(motion: false);
    }

    /// <summary>
    /// 目前這一塊在說哪一件事。
    /// </summary>
    /// <remarks>
    /// 只在換了一種狀態時淡入一次：同一種狀態改文字（例如刪列之後的筆數）不重播，
    /// 否則每一批結果都會讓這塊字閃一下。
    /// </remarks>
    public SqlSurfaceState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            var revealed = _state.Kind != value.Kind && value.Kind is not (SqlSurfaceKind.None or SqlSurfaceKind.Loading);
            _state = value;
            Apply(revealed);
        }
    }

    /// <summary>只有載入一種狀態的表面用這個捷徑；會用 <see cref="State"/> 的呼叫端不要混用。</summary>
    public bool IsLoading
    {
        get => _state.Kind == SqlSurfaceKind.Loading;
        set => State = value ? SqlSurfaceState.Loading : SqlSurfaceState.None;
    }

    private void Apply(bool motion)
    {
        var loading = _state.Kind == SqlSurfaceKind.Loading;
        _indicator.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

        var hasMessage = _state.Title.Length != 0;
        _message.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = _state.Title;
        _detail.Text = _state.Detail;
        _detail.Visibility = _state.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 讀不到與無權限共用同一顆警示圖示；空狀態不掛圖示，那一句本來就不是警告。
        _icon.Icon = _state.IsUnavailable ? SqlIcon.Warning : null;
        _icon.Visibility = _state.IsUnavailable ? Visibility.Visible : Visibility.Collapsed;

        // 載入中那一句由忙碌圖示自己帶著；這裡再掛一次會讓朗讀器念兩遍。
        AutomationProperties.SetName(this, !hasMessage ? ""
            : _state.Detail.Length == 0 ? _state.Title
            : _state.Title + "。" + _state.Detail);

        if (motion && hasMessage) SqlAssistChrome.PlayAppear(_message);
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        // 動畫關閉時保留靜態忙碌圖示；不可見時停轉，不讓背景工具窗持續算繪。
        _rotation.BeginAnimation(RotateTransform.AngleProperty,
            IsLoading && IsVisible && SqlAssistChrome.MotionEnabled
                ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);
    }
}
