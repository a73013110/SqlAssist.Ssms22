using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Blocks;

[Export(typeof(IWpfTextViewMarginProvider))]
[Name(BlockOverviewMargin.MarginName)]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[MarginContainer(PredefinedMarginNames.VerticalScrollBar)]
[GridCellLength(7)]
[GridUnitType(GridUnitType.Pixel)]
[Order(After = PredefinedMarginNames.OverviewChangeTracking)]
internal sealed class BlockOverviewProvider : IWpfTextViewMarginProvider
{
    [Import]
    internal IScrollMapFactoryService ScrollMaps { get; set; } = null!;

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container) =>
        SqlAssistPlatformGuard.Create("建立區塊捲軸概覽", () =>
            host.TextView.IsClosed ? null : new BlockOverviewMargin(host.TextView, container, ScrollMaps));
}

internal sealed class BlockOverviewMargin : IWpfTextViewMargin
{
    public const string MarginName = "SqlAssist.BlockOverview";
    private readonly IWpfTextView _view;
    private readonly BlockViewState _state;
    private readonly IVerticalScrollBar? _scrollBar;
    private readonly IScrollMap _map;
    private readonly Canvas _canvas = new() { Width = 7, ClipToBounds = true, IsHitTestVisible = false };
    private readonly Border _opening = new() { Width = 7, Height = 3 };
    private readonly Border _closing = new() { Width = 7, Height = 3 };
    private readonly Border _range = new() { Width = 3 };
    private readonly ThemeRefreshQueue _refresh;
    private BlockPair? _shownPair;
    private ITextSnapshot? _shownSnapshot;
    private bool _disposed;

    public BlockOverviewMargin(IWpfTextView view, IWpfTextViewMargin container, IScrollMapFactoryService maps)
    {
        _view = view;
        _state = BlockViewState.Get(view);
        _scrollBar = container as IVerticalScrollBar ?? container.GetTextViewMargin(PredefinedMarginNames.VerticalScrollBar) as IVerticalScrollBar;
        _map = _scrollBar?.Map ?? maps.Create(view, areElisionsExpanded: false);
        _refresh = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新區塊概覽座標", Refresh));
        _canvas.Children.Add(_range);
        Canvas.SetLeft(_range, 2);
        _canvas.Children.Add(_opening);
        _canvas.Children.Add(_closing);
        VsThemeBrushes.Apply(_canvas);
        EditorBlockTheme.Get(view).Apply(_canvas);
        _state.Changed += OnStateChanged;
        _map.MappingChanged += OnChanged;
        if (_scrollBar is not null) _scrollBar.TrackSpanChanged += OnChanged;
        _canvas.SizeChanged += OnSizeChanged;
        view.LayoutChanged += OnLayout;
        view.Closed += OnClosed;
        Refresh();
    }

    public FrameworkElement VisualElement => _canvas;
    public double MarginSize => _canvas.Visibility == Visibility.Visible ? _canvas.Width : 0;
    public bool Enabled => !_disposed && _state.Settings.Enabled && _state.Settings.BlockMatchingEnabled && _state.Settings.BlockOverview;
    public ITextViewMargin? GetTextViewMargin(string marginName) => string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    private void OnStateChanged(object? sender, BlockChangedEventArgs args)
    {
        if (args.Change == BlockChange.Caret && _shownPair == (_state.RangePair ?? _state.ContextPair) &&
            _shownSnapshot == _state.Snapshot) return;
        _refresh.Request();
    }
    private void OnChanged(object? sender, EventArgs args) { if (Enabled) _refresh.Request(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) { if (Enabled) _refresh.Request(); }
    private void OnLayout(object sender, TextViewLayoutChangedEventArgs args) { if (Enabled) _refresh.Request(); }

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        if (_view.InLayout) return; // 下一次 LayoutChanged 再排程，避免版面未完成時忙碌重排。
        _canvas.Visibility = Enabled ? Visibility.Visible : Visibility.Collapsed;
        var pair = _state.RangePair ?? _state.ContextPair;
        var snapshot = _state.Snapshot;
        var visible = Enabled && pair is not null && snapshot is not null && snapshot == _view.TextSnapshot;
        _range.Visibility = _opening.Visibility = _closing.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (!visible || pair is null || snapshot is null) { _shownPair = null; _shownSnapshot = null; return; }
        if (_shownPair != pair || _shownSnapshot != snapshot)
        {
            var role = SqlAssistChrome.BlockBrush(pair.Kind);
            _range.SetResourceReference(Border.BackgroundProperty, role);
            _opening.SetResourceReference(Border.BackgroundProperty, role);
            _closing.SetResourceReference(Border.BackgroundProperty, role);
            AutomationProperties.SetName(_canvas, $"{BlockContextText.KindName(pair.Kind)} 區塊範圍");
            _shownPair = pair;
            _shownSnapshot = snapshot;
        }
        var start = Coordinate(snapshot, pair.Span.Start);
        var end = Coordinate(snapshot, pair.Closing[0].Start);
        Canvas.SetTop(_opening, start);
        Canvas.SetTop(_closing, end);
        Canvas.SetTop(_range, Math.Min(start, end));
        _range.Height = Math.Abs(end - start) + _opening.Height;
    }

    private double Coordinate(ITextSnapshot snapshot, int position)
    {
        var point = new SnapshotPoint(snapshot, position);
        var height = Math.Max(0, _canvas.ActualHeight - _opening.Height);
        var y = _scrollBar?.GetYCoordinateOfBufferPosition(point) ?? _map.GetFractionAtBufferPosition(point) * height;
        if (_scrollBar is IWpfTextViewMargin native && native.VisualElement.IsLoaded && _canvas.IsLoaded)
            y = native.VisualElement.TranslatePoint(new Point(0, y), _canvas).Y;
        // 使用原生 scroll map 處理折疊與換行，不用原始行數硬估可捲動位置。
        return double.IsNaN(y) || double.IsInfinity(y) ? 0 : Math.Max(0, Math.Min(height, y));
    }

    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉區塊概覽", Dispose);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Dispose();
        _state.Changed -= OnStateChanged;
        _map.MappingChanged -= OnChanged;
        if (_scrollBar is not null) _scrollBar.TrackSpanChanged -= OnChanged;
        _canvas.SizeChanged -= OnSizeChanged;
        _view.LayoutChanged -= OnLayout;
        _view.Closed -= OnClosed;
        _canvas.Children.Clear();
        _shownPair = null;
        _shownSnapshot = null;
    }
}
