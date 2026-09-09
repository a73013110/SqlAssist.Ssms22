using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Blocks;

[Export(typeof(IViewTaggerProvider))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[TagType(typeof(ClassificationTag))]
internal sealed class BlockEndpointProvider : IViewTaggerProvider
{
    [Import] internal IClassificationTypeRegistryService Types { get; set; } = null!;
    [Import] internal IEditorFormatMapService Formats { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag =>
        SqlAssistPlatformGuard.Create("建立區塊端點分類", () =>
            textView is IWpfTextView view && !view.IsClosed && buffer == view.TextBuffer
                ? new BlockEndpointTagger(view, Formats.GetEditorFormatMap(view), Types) as ITagger<T> : null);
}

/// <summary>只覆寫被選中端點的前背景；與其他呈現共用配對索引，不更改全域 SQL 分類色。</summary>
internal sealed class BlockEndpointTagger : ITagger<ClassificationTag>, IDisposable
{
    private readonly IWpfTextView _view;
    private readonly IEditorFormatMap _formats;
    private readonly BlockViewState _state;
    private readonly EditorBlockTheme _theme;
    private readonly ThemeRefreshQueue _queue;
    private readonly ClassificationTag? _keyword;
    private readonly ClassificationTag? _symbol;
    private ITagSpan<ClassificationTag>[] _tags = Array.Empty<ITagSpan<ClassificationTag>>();
    private bool _disposed;
    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public BlockEndpointTagger(IWpfTextView view, IEditorFormatMap formats, IClassificationTypeRegistryService types)
    {
        _view = view;
        _formats = formats;
        _state = BlockViewState.Get(view);
        _theme = EditorBlockTheme.Get(view);
        _theme.AttachFormats(formats);
        _keyword = ResolveTag(types, BlockEndpointFormat.Keyword);
        _symbol = ResolveTag(types, BlockEndpointFormat.Symbol);
        _queue = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新區塊端點配色", UpdateColors));
        _state.Changed += OnState;
        _theme.Changed += OnTheme;
        _formats.FormatMappingChanged += OnFormat;
        view.Closed += OnClosed;
        _queue.Request();
        Refresh();
    }

    public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        // GetTags 至多檢查四個端點；沒有 SQL 解析、取色或平台服務查詢。
        foreach (var tag in _tags)
            if (spans.Count > 0 && spans[0].Snapshot == tag.Span.Snapshot && spans.IntersectsWith(tag.Span))
                yield return tag;
    }

    /// <summary>
    /// 分類型別由本擴充自己匯出；註冊表沒有它時不上色，而不是讓編輯器建立失敗。
    /// </summary>
    /// <remarks>
    /// 真的取不到就是 MEF 快取過期那一類問題（見[偵錯]），症狀本來就會是「整組功能
    /// 消失」；在這裡丟例外只會多一個看不懂的對話框，所以記一行再讓其餘功能照常。
    /// </remarks>
    private static ClassificationTag? ResolveTag(IClassificationTypeRegistryService types, string name)
    {
        if (types.GetClassificationType(name) is { } type)
        {
            return new ClassificationTag(type);
        }

        SqlAssistDiagnostics.WriteAlways($"找不到分類型別 {name}，區塊端點這一輪不上色");
        return null;
    }

    private void UpdateColors()
    {
        if (_disposed || _view.IsClosed) return;
        Apply(BlockEndpointFormat.Keyword, ThemeBrush.BlockKeywordForeground, ThemeBrush.BlockKeywordBackground);
        Apply(BlockEndpointFormat.Symbol, ThemeBrush.BlockSymbolForeground, ThemeBrush.BlockSymbolBackground);
    }

    private void Apply(string name, ThemeBrush foreground, ThemeBrush background)
    {
        var properties = _formats.GetProperties(name);
        var ink = _theme.GetBrush(foreground);
        var fill = _theme.GetBrush(background);
        if (properties[EditorFormatDefinition.ForegroundColorId] is Color oldInk && oldInk == ink.Color &&
            properties[EditorFormatDefinition.BackgroundColorId] is Color oldFill && oldFill == fill.Color &&
            properties[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush oldInkBrush && oldInkBrush.Color == ink.Color && oldInkBrush.Opacity == 1 &&
            properties[EditorFormatDefinition.BackgroundBrushId] is SolidColorBrush oldFillBrush && oldFillBrush.Color == fill.Color && oldFillBrush.Opacity == 1) return;
        properties[EditorFormatDefinition.ForegroundColorId] = ink.Color;
        properties[EditorFormatDefinition.ForegroundBrushId] = ink;
        properties[EditorFormatDefinition.BackgroundColorId] = fill.Color;
        properties[EditorFormatDefinition.BackgroundBrushId] = fill;
        _formats.SetProperties(name, properties);
        SqlAssistDiagnostics.Write($"區塊端點配色 {name}：字色={ink.Color}，背景={fill.Color}");
    }

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var snapshot = _view.TextSnapshot;
        var pair = _state.Settings.BlockKeywordHighlight && _state.Snapshot == snapshot ? _state.SelectedPair : null;
        var keyword = _keyword;
        var symbol = _symbol;
        var next = pair is null || keyword is null || symbol is null ? Array.Empty<ITagSpan<ClassificationTag>>() :
            pair.Opening.Concat(pair.Closing).Select(span => (ITagSpan<ClassificationTag>)new TagSpan<ClassificationTag>(
                new SnapshotSpan(snapshot, span.Start, span.Length), BlockDisplayRules.IsSymbol(pair.Kind) ? symbol : keyword)).ToArray();
        if (_tags.Length == next.Length && _tags.Select(t => (t.Span, t.Tag)).SequenceEqual(next.Select(t => (t.Span, t.Tag)))) return;
        var old = _tags;
        _tags = next;
        foreach (var tag in old.Concat(next))
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(tag.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive)));
    }

    private void OnState(object? sender, BlockChangedEventArgs args) => Refresh();
    private void OnTheme(object? sender, EventArgs args) => _queue.Request();
    private void OnFormat(object? sender, FormatItemsEventArgs args)
    {
        if (EditorFormatChanges.Affects(args.ChangedItems, BlockEndpointFormat.Keyword, BlockEndpointFormat.Symbol)) _queue.Request();
    }
    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉區塊端點分類", Dispose);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Dispose();
        _state.Changed -= OnState;
        _theme.Changed -= OnTheme;
        _formats.FormatMappingChanged -= OnFormat;
        _view.Closed -= OnClosed;
        _tags = Array.Empty<ITagSpan<ClassificationTag>>();
    }
}
