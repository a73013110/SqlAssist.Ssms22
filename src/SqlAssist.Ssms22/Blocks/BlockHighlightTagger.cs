using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Blocks;

[Export(typeof(IViewTaggerProvider))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[TagType(typeof(TextMarkerTag))]
internal sealed class BlockHighlightProvider : IViewTaggerProvider
{
    [Import]
    internal IEditorFormatMapService FormatMaps { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag =>
        SqlAssistPlatformGuard.Create("建立區塊高亮", () =>
            textView is IWpfTextView view && buffer == view.TextBuffer
                ? view.Properties.GetOrCreateSingletonProperty(() => new BlockHighlightTagger(view, FormatMaps.GetEditorFormatMap(view))) as ITagger<T>
                : null);
}

internal sealed class BlockHighlightTagger : ITagger<TextMarkerTag>, IDisposable
{
    private static readonly TextMarkerTag Highlight = new("bracehighlight");
    private static readonly TextMarkerTag Range = new(BlockRangeFormat.FormatName);
    private readonly IWpfTextView _view;
    private readonly BlockViewState _state;
    private readonly BlockRangeAppearance _appearance;
    private ITagSpan<TextMarkerTag>[] _tags = Array.Empty<ITagSpan<TextMarkerTag>>();
    private bool _disposed;
    private bool _reportedTags;
    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public BlockHighlightTagger(IWpfTextView view, IEditorFormatMap formats)
    {
        _view = view;
        _state = BlockViewState.Get(view);
        _appearance = new BlockRangeAppearance(view, formats);
        _state.Changed += OnUpdated;
        _view.Closed += OnClosed;
        VsThemeBrushes.Changed += OnAppearanceChanged;
        SqlAssistDiagnostics.WriteAlways($"區塊配對 ContentType={view.TextBuffer.ContentType.TypeName}; SQL={view.TextBuffer.ContentType.IsOfType("SQL")}");
        Refresh();
    }

    public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        // 此路徑只讀至多四個端點與一段背景，絕不取全文或啟動同步解析。
        var tags = _tags;
        if (!_reportedTags && tags.Length > 0)
        {
            _reportedTags = true;
            SqlAssistDiagnostics.WriteAlways($"區塊 GetTags 首次提供 {tags.Length} 個端點；要求範圍 {spans.Count}");
        }
        foreach (var tag in tags)
            if (spans.Count > 0 && tag.Span.Snapshot == spans[0].Snapshot && spans.IntersectsWith(tag.Span))
                yield return tag;
    }

    private void OnUpdated(object? sender, BlockChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("顯示區塊配對結果", Refresh);
    private void OnAppearanceChanged(object? sender, EventArgs args) =>
        TextViewDispatch.AfterCurrentCommand(_view, "套用區塊高對比顯示", _ => Refresh());
    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉區塊高亮", Dispose);

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var snapshot = _view.TextSnapshot;
        var settings = _state.Settings;
        var pair = _state.Snapshot == snapshot ? _state.SelectedPair : null;

        var nextTags = new List<ITagSpan<TextMarkerTag>>(5);
        if (pair is not null)
        {
            if (settings.BlockKeywordHighlight)
                foreach (var span in pair.Opening.Concat(pair.Closing))
                    nextTags.Add(new TagSpan<TextMarkerTag>(new SnapshotSpan(snapshot, span.Start, span.Length), Highlight));
            var sameLine = snapshot.GetLineNumberFromPosition(pair.Span.Start) ==
                snapshot.GetLineNumberFromPosition(pair.Span.End - 1);
            if (BlockDisplayRules.ShowRange(settings, sameLine, SystemParameters.HighContrast))
                nextTags.Add(new TagSpan<TextMarkerTag>(new SnapshotSpan(snapshot, pair.Span.Start, pair.Span.Length), Range));
        }
        var next = nextTags.ToArray();
        var previous = _tags;
        if (previous.Length == next.Length && previous.Select(t => (t.Span, t.Tag.Type)).SequenceEqual(next.Select(t => (t.Span, t.Tag.Type)))) return;
        _tags = next;
        // 只失效新舊端點，而不是每次游標移動都讓整份文件重新取 Tag。
        foreach (var tag in previous.Concat(next))
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(tag.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Closed -= OnClosed;
        _state.Changed -= OnUpdated;
        VsThemeBrushes.Changed -= OnAppearanceChanged;
        _appearance.Dispose();
        _tags = Array.Empty<ITagSpan<TextMarkerTag>>();
    }
}
