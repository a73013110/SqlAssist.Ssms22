using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Blocks;

[Export(typeof(ITaggerProvider))]
[ContentType("SQL")]
[TagType(typeof(IStructureTag))]
internal sealed class BlockStructureProvider : ITaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
        SqlAssistPlatformGuard.Create("建立區塊結構 Tagger", () =>
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                // 原生 outlining 轉接器使用 buffer aggregator，不能只匯出 view tagger。
                return new BlockStructureTagger(buffer, Dispatcher.CurrentDispatcher) as ITagger<T>;
            }));
}

/// <summary>只提供 Shell 的結構資料；導引線與摺疊均由原生編輯器繪製。</summary>
internal sealed class BlockStructureTagger : ITagger<IStructureTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly Dispatcher _dispatcher;
    private readonly BlockAnalysis _analysis;
    private volatile Cache? _cache;
    private bool _disposed;
    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    private sealed class Cache
    {
        public Cache(ITextSnapshot snapshot, BlockMatcher matcher, SqlAssistSettings settings)
        { Snapshot = snapshot; Matcher = matcher; Settings = settings; }
        public ITextSnapshot Snapshot { get; }
        public BlockMatcher Matcher { get; }
        public SqlAssistSettings Settings { get; }
        public ConcurrentDictionary<BlockPair, ITagSpan<IStructureTag>> Tags { get; } = new();
    }

    public BlockStructureTagger(ITextBuffer buffer, Dispatcher dispatcher)
    {
        _buffer = buffer;
        _dispatcher = dispatcher;
        _analysis = BlockAnalysis.Acquire(buffer, dispatcher);
        _analysis.Updated += OnChanged;
        SqlAssistSettingsStore.Changed += OnSettingsChanged;
        Refresh();
    }

    public IEnumerable<ITagSpan<IStructureTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        var cache = _cache;
        if (cache is null || spans.Count == 0 || spans[0].Snapshot != cache.Snapshot) yield break;
        var seen = new HashSet<BlockPair>();
        foreach (var requested in spans)
            foreach (var pair in cache.Matcher.GetIntersectingBlocks(requested.Start.Position, requested.Length))
            {
                if (!seen.Add(pair) || !BlockDisplayRules.IsKindEnabled(pair.Kind, cache.Settings)) continue;
                var first = cache.Snapshot.GetLineFromPosition(pair.Span.Start);
                var last = cache.Snapshot.GetLineFromPosition(pair.Span.End - 1);
                if (first.LineNumber == last.LineNumber) continue;
                yield return cache.Tags.GetOrAdd(pair, p => CreateTag(cache.Snapshot, p, first, cache.Settings.BlockOutlining));
            }
    }

    private static ITagSpan<IStructureTag> CreateTag(ITextSnapshot snapshot, BlockPair pair, ITextSnapshotLine header, bool outlining)
    {
        var span = new Span(pair.Span.Start, pair.Span.Length);
        var summary = BlockContextText.Summarize(snapshot.GetText(header.Start.Position,
            Math.Min(header.Length, BlockContextText.MaximumSummaryLength + 1)));
        var tag = new StructureTag(snapshot,
            outliningSpan: Span.FromBounds(header.End.Position, span.End),
            headerSpan: header.Extent.Span, guideLineSpan: span, guideLineHorizontalAnchor: span.Start,
            type: pair.Kind == BlockKind.Case || BlockDisplayRules.IsSymbol(pair.Kind)
                ? PredefinedStructureTagTypes.Expression : PredefinedStructureTagTypes.Statement,
            isCollapsible: outlining, isDefaultCollapsed: false, isImplementation: false,
            collapsedForm: "…", collapsedHintForm: $"{summary}（第 {header.LineNumber + 1} 行）");
        return new TagSpan<IStructureTag>(new SnapshotSpan(snapshot, span), tag);
    }

    private void OnChanged(object? sender, EventArgs args) => SqlAssistPlatformGuard.Run("更新區塊結構", Refresh);
    private void OnSettingsChanged(object? sender, EventArgs args) =>
        _dispatcher.BeginInvoke(new Action(() => SqlAssistPlatformGuard.Run("套用區塊結構設定", Refresh)));

    private void Refresh()
    {
        if (_disposed) return;
        var settings = SqlAssistSettingsStore.Current;
        Cache? next = null;
        if (settings.Enabled && settings.BlockMatchingEnabled && settings.BlockStructure &&
            _analysis.Snapshot is { } snapshot && _analysis.Matcher is { } matcher)
        {
            if (_cache is { } old && old.Snapshot == snapshot &&
                old.Settings.BlockOutlining == settings.BlockOutlining &&
                old.Settings.BlockMatchCase == settings.BlockMatchCase &&
                old.Settings.BlockMatchParentheses == settings.BlockMatchParentheses) return;
            next = new Cache(snapshot, matcher, settings);
        }
        if (_cache is null && next is null) return;
        _cache = next;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(_buffer.CurrentSnapshot, 0, _buffer.CurrentSnapshot.Length)));
    }

    public void Dispose()
    {
        // 每個 aggregator 擁有自己的 tagger；共用的只有分析服務，避免分割檢視互相 Dispose。
        if (_dispatcher.CheckAccess()) SqlAssistPlatformGuard.Run("關閉區塊結構 Tagger", DisposeCore);
        else _dispatcher.BeginInvoke(new Action(() => SqlAssistPlatformGuard.Run("關閉區塊結構 Tagger", DisposeCore)));
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        _analysis.Updated -= OnChanged;
        SqlAssistSettingsStore.Changed -= OnSettingsChanged;
        _analysis.Release();
        _cache = null;
    }
}
