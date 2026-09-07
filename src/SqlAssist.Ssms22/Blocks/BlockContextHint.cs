using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Blocks;

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class BlockContextProvider : IWpfTextViewCreationListener
{
    [Import(AllowDefault = true)]
    internal IOutliningManagerService? Outlining { get; set; }

    [Export(typeof(AdornmentLayerDefinition))]
    [Name(BlockContextHint.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
    internal AdornmentLayerDefinition Layer = null!;

    public void TextViewCreated(IWpfTextView textView) =>
        SqlAssistPlatformGuard.Run("建立區塊跨頁提示", () =>
            textView.Properties.GetOrCreateSingletonProperty(() => new BlockContextHint(textView, Outlining)));
}

internal sealed class BlockContextHint : IDisposable
{
    public const string LayerName = "SqlAssist.BlockContext";
    private readonly IWpfTextView _view;
    private readonly BlockViewState _state;
    private readonly IOutliningManagerService? _outlining;
    private readonly ThemeRefreshQueue _refresh;
    private IAdornmentLayer? _layer;
    private Button? _surface;
    private TextBlock? _text;
    private bool _attached;
    private bool _disposed;
    private BlockPair? _shownPair;
    private ITextSnapshot? _shownSnapshot;

    public BlockContextHint(IWpfTextView view, IOutliningManagerService? outlining)
    {
        _view = view;
        _outlining = outlining;
        _state = BlockViewState.Get(view);
        _refresh = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新跨頁區塊提示", Refresh));
        _state.Changed += OnStateChanged;
        view.LayoutChanged += OnLayout;
        view.ViewportWidthChanged += OnChanged;
        view.Closed += OnClosed;
        VsThemeBrushes.Changed += OnChanged;
        _refresh.Request();
    }

    private void OnStateChanged(object? sender, BlockChangedEventArgs args) => SqlAssistPlatformGuard.Probe("更新跨頁提示", Refresh);
    private void OnLayout(object sender, TextViewLayoutChangedEventArgs args) { if (_state.Settings.BlockContextHint) _refresh.Request(); }
    private void OnChanged(object? sender, EventArgs args) { if (_state.Settings.BlockContextHint) _refresh.Request(); }

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var pair = _state.ContextPair;
        var snapshot = _state.Snapshot;
        if (!_state.Settings.BlockContextHint || pair is null || snapshot is null || snapshot != _view.TextSnapshot ||
            _view.InLayout || _view.TextViewLines is null || _view.TextViewLines.Count == 0)
        { Hide(); return; }
        var firstVisible = _view.TextViewLines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
        var opening = snapshot.GetLineFromPosition(pair.Span.Start);
        if (opening.LineNumber >= firstVisible) { Hide(); return; }
        _layer ??= _view.GetAdornmentLayer(LayerName);
        if (_surface is null)
        {
            _surface = SqlAssistChrome.CreateBlockContext(out _text);
            _surface.Click += OnNavigate;
            VsThemeBrushes.Apply(_surface);
            EditorBlockTheme.Get(_view).Apply(_surface);
        }
        if (_shownPair != pair || _shownSnapshot != snapshot)
        {
            _text!.Text = BlockContextText.Format(pair.Kind, opening.LineNumber + 1, ReadLine(snapshot, opening));
            AutomationProperties.SetName(_surface, "返回區塊起始行：" + _text.Text);
            _surface.ToolTip = _text.Text + "\n點擊返回起始行";
            _shownPair = pair;
            _shownSnapshot = snapshot;
        }
        // 提示必須是不透明表面，不能讓底下 SQL 穿透而降低文字對比。
        _surface.Opacity = 1;
        _surface.MaxWidth = Math.Max(0, _view.ViewportWidth - 16);
        // 只有提示按鈕區域接收輸入，其他編輯器區域與捲動操作保持原生行為。
        // AdornmentLayer 的初始座標仍是文件座標；ViewportRelative 只負責後續捲動位移。
        Canvas.SetLeft(_surface, _view.ViewportLeft + 8);
        Canvas.SetTop(_surface, _view.ViewportTop);
        if (!_attached)
            _attached = _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, this, _surface,
                (_, _) => _attached = false);
    }

    private static string ReadLine(ITextSnapshot snapshot, ITextSnapshotLine line) =>
        snapshot.GetText(line.Start.Position, Math.Min(line.Length, BlockContextText.MaximumSummaryLength + 1));

    private void OnNavigate(object sender, RoutedEventArgs args)
    {
        // 主動點擊的失敗必須可見，不能以平台 Guard 靜默吞掉導覽錯誤。
        try
        {
            // 文字若已改動，舊提示不得把游標送到另一段 SQL；等最新分析再允許跳轉。
            if (_disposed || _view.IsClosed) return;
            if (_shownSnapshot != _view.TextSnapshot || _shownPair is not { } pair)
            {
                SqlAssistStatusBar.Show(ServiceProvider.GlobalProvider, "區塊已變更，請等待最新提示後再返回起始行。");
                return;
            }
            var point = new SnapshotPoint(_view.TextSnapshot, pair.Span.Start);
            // 按需展開包含目的地的原生摺疊，不掃描或展開整份查詢。
            _outlining?.GetOutliningManager(_view)?.ExpandAll(new SnapshotSpan(point, 0), _ => true);
            _view.Caret.MoveTo(point);
            _view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0));
            _view.DisplayTextLineContainingBufferPosition(point, 0, ViewRelativePosition.Top);
            _view.VisualElement.Focus();
            args.Handled = true;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"返回區塊起始行失敗：{exception}");
            SqlAssistStatusBar.Show(ServiceProvider.GlobalProvider, "無法返回區塊起始行，請重新定位游標後再試。");
        }
    }

    private void Hide()
    {
        if (_attached && _surface is not null) _layer?.RemoveAdornment(_surface);
        _attached = false;
        _shownPair = null;
        _shownSnapshot = null;
    }

    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉跨頁區塊提示", Dispose);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Dispose();
        _state.Changed -= OnStateChanged;
        _view.LayoutChanged -= OnLayout;
        _view.ViewportWidthChanged -= OnChanged;
        _view.Closed -= OnClosed;
        VsThemeBrushes.Changed -= OnChanged;
        Hide();
        if (_surface is not null) _surface.Click -= OnNavigate;
        _surface = null;
        _text = null;
        _layer = null;
    }
}
