using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Blocks;

internal sealed class BlockGlyphTag : IGlyphTag
{
    public BlockGlyphTag(BlockKind kind, int firstLine, int lastLine)
    { Kind = kind; Description = $"{BlockContextText.KindName(kind)}：第 {firstLine + 1}–{lastLine + 1} 行"; }
    public BlockKind Kind { get; }
    public string Description { get; }
}

[Export(typeof(IViewTaggerProvider))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[TagType(typeof(BlockGlyphTag))]
internal sealed class BlockGlyphTaggerProvider : IViewTaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag =>
        SqlAssistPlatformGuard.Create("建立區塊邊欄 Tagger", () =>
            textView is IWpfTextView view && !view.IsClosed && buffer == view.TextBuffer
                ? view.Properties.GetOrCreateSingletonProperty(() => new BlockGlyphTagger(view)) as ITagger<T> : null);
}

[Export(typeof(IGlyphFactoryProvider))]
[Name("SqlAssist.BlockGlyph")]
[ContentType("SQL")]
[TagType(typeof(BlockGlyphTag))]
internal sealed class BlockGlyphFactoryProvider : IGlyphFactoryProvider
{
    public IGlyphFactory GetGlyphFactory(IWpfTextView view, IWpfTextViewMargin margin) => new Factory();

    private sealed class Factory : IGlyphFactory
    {
        public UIElement? GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag) =>
            SqlAssistPlatformGuard.Create("繪製區塊邊欄色帶", () => tag is BlockGlyphTag block
                ? SqlAssistChrome.CreateBlockGlyph(block.Kind, line.Height, block.Description) : null);
    }
}

internal sealed class BlockGlyphTagger : ITagger<BlockGlyphTag>, IDisposable
{
    private readonly IWpfTextView _view;
    private readonly BlockViewState _state;
    private volatile Selection? _selection;
    private bool _disposed;
    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    private sealed class Selection
    {
        public Selection(SnapshotSpan range, BlockGlyphTag tag) { Range = range; Tag = tag; }
        public SnapshotSpan Range { get; }
        public BlockGlyphTag Tag { get; }
    }

    public BlockGlyphTagger(IWpfTextView view)
    {
        _view = view;
        _state = BlockViewState.Get(view);
        _state.Changed += OnChanged;
        view.Closed += OnClosed;
        Refresh();
    }

    public IEnumerable<ITagSpan<BlockGlyphTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        var selection = _selection;
        if (selection is null || spans.Count == 0 || spans[0].Snapshot != selection.Range.Snapshot) yield break;
        var range = selection.Range;
        var tag = selection.Tag;
        var seen = new HashSet<int>();
        foreach (var requested in spans)
        {
            if (requested.IsEmpty && range.Contains(requested.Start))
            {
                var blank = requested.Start.GetContainingLine();
                if (seen.Add(blank.LineNumber)) yield return new TagSpan<BlockGlyphTag>(blank.ExtentIncludingLineBreak, tag);
                continue;
            }
            var overlap = requested.Intersection(range);
            if (overlap is not { } span || span.IsEmpty) continue;
            var first = span.Start.GetContainingLine().LineNumber;
            var last = (span.End - 1).GetContainingLine().LineNumber;
            // 只為平台要求的行產生色帶，萬行區塊也不預建萬個 WPF 元素。
            for (var i = first; i <= last; i++)
                if (seen.Add(i)) yield return new TagSpan<BlockGlyphTag>(range.Snapshot.GetLineFromLineNumber(i).ExtentIncludingLineBreak, tag);
        }
    }

    private void OnChanged(object? sender, BlockChangedEventArgs args) => Refresh();
    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var previous = _selection?.Range;
        SnapshotSpan? next = null;
        var pair = _state.SelectedPair;
        if (_state.Settings.BlockGlyphs && pair is not null && _state.Snapshot is { } snapshot)
        {
            var first = snapshot.GetLineFromPosition(pair.Span.Start);
            var last = snapshot.GetLineFromPosition(pair.Span.End - 1);
            next = new SnapshotSpan(snapshot, Span.FromBounds(first.Start.Position, last.EndIncludingLineBreak.Position));
        }
        if (previous == next && (pair is null || _selection?.Tag.Kind == pair.Kind)) return;
        _selection = next is { } range && pair is not null ? new Selection(range, new BlockGlyphTag(pair.Kind,
            range.Start.GetContainingLine().LineNumber, (range.End - 1).GetContainingLine().LineNumber)) : null;
        foreach (var span in new[] { previous, next })
            if (span is { } changed) TagsChanged?.Invoke(this,
                new SnapshotSpanEventArgs(changed.TranslateTo(_view.TextSnapshot, SpanTrackingMode.EdgeExclusive)));
    }

    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉區塊邊欄", Dispose);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Closed -= OnClosed;
        _state.Changed -= OnChanged;
        _selection = null;
    }
}
