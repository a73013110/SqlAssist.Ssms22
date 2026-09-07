using System;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Blocks;

internal enum BlockChange { Caret, Analysis, Settings }

internal sealed class BlockChangedEventArgs : EventArgs
{
    public BlockChangedEventArgs(BlockChange change) => Change = change;
    public BlockChange Change { get; }
}

/// <summary>同一檢視只有一次游標查詢與設定訂閱；所有呈現層讀這份狀態，不各自解析。</summary>
internal sealed class BlockViewState : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly BlockAnalysis _analysis;
    private bool _disposed;
    public SqlAssistSettings Settings { get; private set; } = SqlAssistSettingsStore.Current;
    public ITextSnapshot? Snapshot { get; private set; }
    public BlockMatcher? Matcher { get; private set; }
    public BlockPair? SelectedPair { get; private set; }
    public BlockPair? ContextPair { get; private set; }
    public event EventHandler<BlockChangedEventArgs>? Changed;

    public static BlockViewState Get(IWpfTextView view) =>
        view.Properties.GetOrCreateSingletonProperty(() => new BlockViewState(view));

    private BlockViewState(IWpfTextView view)
    {
        _view = view;
        _analysis = BlockAnalysis.Acquire(view.TextBuffer, view.VisualElement.Dispatcher);
        _analysis.Updated += OnAnalysis;
        view.Caret.PositionChanged += OnCaret;
        view.LayoutChanged += OnLayout;
        view.Closed += OnClosed;
        SqlAssistSettingsStore.Changed += OnSettings;
        Refresh(BlockChange.Analysis);
    }

    private void OnCaret(object sender, CaretPositionChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("更新區塊游標狀態", () => Refresh(BlockChange.Caret));
    private void OnAnalysis(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Run("發布區塊快照", () => Refresh(BlockChange.Analysis));
    private void OnLayout(object sender, TextViewLayoutChangedEventArgs args)
    {
        // buffer.Changed 可能比檢視切換 snapshot 早，版面完成後再同步一次。
        if (args.NewSnapshot != args.OldSnapshot) OnAnalysis(sender, EventArgs.Empty);
    }
    private void OnSettings(object? sender, EventArgs args) =>
        TextViewDispatch.AfterCurrentCommand(_view, "套用區塊呈現設定", _ => Refresh(BlockChange.Settings));
    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉區塊檢視狀態", Dispose);

    private void Refresh(BlockChange change)
    {
        if (_disposed || _view.IsClosed) return;
        var oldSelected = SelectedPair;
        var oldContext = ContextPair;
        var oldSnapshot = Snapshot;
        var oldSettings = Settings;
        Settings = SqlAssistSettingsStore.Current;
        Snapshot = _analysis.Snapshot?.Version == _view.TextSnapshot.Version ? _analysis.Snapshot : null;
        Matcher = Snapshot is null ? null : _analysis.Matcher;
        var position = _view.Caret.Position.BufferPosition;
        var valid = Snapshot == position.Snapshot && Settings.Enabled && Settings.BlockMatchingEnabled;
        SelectedPair = valid ? Matcher?.FindPairAt(position.Position) : null;
        if (SelectedPair is { } selected && !BlockDisplayRules.IsKindEnabled(selected.Kind, Settings)) SelectedPair = null;
        ContextPair = valid && Settings.BlockContextHint ? SelectedPair ?? Matcher?.GetEnclosingBlock(position.Position) : null;
        if (ContextPair is { } context && !BlockDisplayRules.IsKindEnabled(context.Kind, Settings))
            ContextPair = Matcher?.GetAncestors(position.Position).FirstOrDefault(p => BlockDisplayRules.IsKindEnabled(p.Kind, Settings));

        // 同一端點內移動或同區塊內打量文字，不讓六個呈現層重建相同內容。
        if (change == BlockChange.Caret && oldSelected == SelectedPair && oldContext == ContextPair &&
            oldSnapshot == Snapshot && ReferenceEquals(oldSettings, Settings)) return;

        if (Changed is not { } handlers) return;
        var args = new BlockChangedEventArgs(change);
        foreach (EventHandler<BlockChangedEventArgs> handler in handlers.GetInvocationList())
            SqlAssistPlatformGuard.Run("更新區塊呈現層", () => handler(this, args));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _analysis.Updated -= OnAnalysis;
        _view.Caret.PositionChanged -= OnCaret;
        _view.LayoutChanged -= OnLayout;
        _view.Closed -= OnClosed;
        SqlAssistSettingsStore.Changed -= OnSettings;
        _analysis.Release();
        Changed = null;
        Matcher = null;
        Snapshot = null;
        SelectedPair = null;
        ContextPair = null;
    }
}
