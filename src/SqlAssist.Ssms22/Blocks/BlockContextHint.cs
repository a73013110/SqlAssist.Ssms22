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

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class BlockContextProvider : IWpfTextViewCreationListener
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(BlockContextHint.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
    internal AdornmentLayerDefinition Layer = null!;

    public void TextViewCreated(IWpfTextView textView) =>
        SqlAssistPlatformGuard.Run("建立區塊跨頁提示", () =>
            textView.Properties.GetOrCreateSingletonProperty(() => new BlockContextHint(textView)));
}

internal sealed class BlockContextHint : IDisposable
{
    public const string LayerName = "SqlAssist.BlockContext";
    private readonly IWpfTextView _view;
    private readonly BlockViewState _state;
    private readonly ThemeRefreshQueue _refresh;
    private IAdornmentLayer? _layer;
    private Border? _surface;
    private TextBlock? _text;
    private bool _attached;
    private bool _disposed;
    private BlockPair? _shownPair;
    private ITextSnapshot? _shownSnapshot;

    public BlockContextHint(IWpfTextView view)
    {
        _view = view;
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
        _surface ??= SqlAssistChrome.CreateBlockContext(out _text);
        if (_shownPair != pair || _shownSnapshot != snapshot)
        {
            var preceding = opening.LineNumber > 0 ? ReadLine(snapshot, snapshot.GetLineFromLineNumber(opening.LineNumber - 1)) : string.Empty;
            _text!.Text = BlockContextText.Format(pair.Kind, opening.LineNumber + 1, ReadLine(snapshot, opening), preceding);
            AutomationProperties.SetName(_surface, _text.Text);
            _shownPair = pair;
            _shownSnapshot = snapshot;
        }
        _surface.Opacity = SystemParameters.HighContrast ? 1 : 0.94;
        _surface.MaxWidth = Math.Max(0, _view.ViewportWidth - 16);
        // ViewportRelative 固定於可視區域，不跟 SQL 內容一起捲走，也不攔截滑鼠與鍵盤。
        // AdornmentLayer 的初始座標仍是文件座標；ViewportRelative 只負責後續捲動位移。
        Canvas.SetLeft(_surface, _view.ViewportLeft + 8);
        Canvas.SetTop(_surface, _view.ViewportTop);
        if (!_attached)
            _attached = _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, this, _surface,
                (_, _) => _attached = false);
    }

    private static string ReadLine(ITextSnapshot snapshot, ITextSnapshotLine line) =>
        snapshot.GetText(line.Start.Position, Math.Min(line.Length, BlockContextText.MaximumSummaryLength + 1));

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
        _surface = null;
        _text = null;
        _layer = null;
    }
}
