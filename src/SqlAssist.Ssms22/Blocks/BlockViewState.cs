using System;
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

    /// <summary>配對來自比目前文字舊的 snapshot；呈現層要平移座標而不是當成沒有配對。</summary>
    public bool IsStale { get; private set; }
    public BlockPair? SelectedPair { get; private set; }
    public BlockPair? ContextPair { get; private set; }
    public BlockPair? RangePair { get; private set; }
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
        var oldRange = RangePair;
        var oldSnapshot = Snapshot;
        var oldStale = IsStale;
        var oldSettings = Settings;
        Settings = SqlAssistSettingsStore.Current;
        // 版本不符不再判成無效：保留上一份索引並標記 stale，游標位置換算回它的座標系。
        Snapshot = _analysis.Snapshot;
        Matcher = Snapshot is null ? null : _analysis.Matcher;
        IsStale = Snapshot is not null && Snapshot.Version != _view.TextSnapshot.Version;
        var position = _view.Caret.Position.BufferPosition;
        var valid = Snapshot is not null && Settings.Enabled && Settings.BlockMatchingEnabled;
        var lookup = valid ? BlockProjection.ToSource(position, Snapshot!) : 0;
        SelectedPair = valid ? Matcher?.FindPairAt(lookup) : null;
        if (SelectedPair is { } selected && !BlockDisplayRules.IsKindEnabled(selected.Kind, Settings)) SelectedPair = null;
        ContextPair = valid && Matcher is { } matcher &&
            (Settings.BlockContextHint || Settings.BlockGlyphs || Settings.BlockOverview || Settings.BlockRangeBackground)
            ? SelectedPair ?? BlockDisplayRules.FindContext(matcher, lookup, Settings) : null;
        RangePair = null;
        if (valid && Matcher is { } rangeMatcher && Settings.BlockRangeBackground)
        {
            bool Accepts(BlockPair pair) => Settings.BlockSameLineBackground ||
                Snapshot!.GetLineNumberFromPosition(pair.Span.Start) != Snapshot.GetLineNumberFromPosition(pair.Span.End - 1);
            // 最近「符合背景設定」的一層：同行括號關閉時仍保留外層 BEGIN 的背景。
            RangePair = Settings.BlockRangeInside
                ? ContextPair is { } context && Accepts(context) ? context : BlockDisplayRules.FindContext(rangeMatcher, lookup, Settings, Accepts)
                : SelectedPair is { } endpoint && Accepts(endpoint) ? endpoint : null;
        }

        // 同一端點內移動或同區塊內打量文字，不讓六個呈現層重建相同內容。
        if (change == BlockChange.Caret && oldSelected == SelectedPair && oldContext == ContextPair &&
            oldRange == RangePair && oldStale == IsStale &&
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
        IsStale = false;
        SelectedPair = null;
        ContextPair = null;
        RangePair = null;
    }
}
