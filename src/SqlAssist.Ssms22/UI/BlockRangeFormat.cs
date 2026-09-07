using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.UI;

[Export(typeof(EditorFormatDefinition))]
[Name(FormatName)]
[UserVisible(false)]
internal sealed class BlockRangeFormat : MarkerFormatDefinition
{
    public const string FormatName = "SqlAssist.BlockRange";

    public BlockRangeFormat()
    {
        DisplayName = "SqlAssist 區塊區間背景";
        ZOrder = 0;
        BackgroundCustomizable = false;
        ForegroundCustomizable = false;
    }
}

/// <summary>只更新本擴充的 marker；基準色由統一設定管理，不把衍生色當作下次輸入。</summary>
internal sealed class BlockRangeAppearance : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly IEditorFormatMap _formats;
    private readonly ThemeRefreshQueue _queue;
    private readonly EditorBlockTheme _theme;
    private bool _disposed;

    public BlockRangeAppearance(IWpfTextView view, IEditorFormatMap formats)
    {
        _view = view;
        _formats = formats;
        _theme = EditorBlockTheme.Get(view);
        _theme.AttachFormats(formats);
        _queue = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新區塊區間配色", Refresh));
        _theme.Changed += OnChanged;
        formats.FormatMappingChanged += OnFormatChanged;
        _queue.Request();
    }

    private void OnChanged(object? sender, EventArgs args) => _queue.Request();
    private void OnFormatChanged(object? sender, FormatItemsEventArgs args)
    {
        if (EditorFormatChanges.Affects(args.ChangedItems, BlockRangeFormat.FormatName)) _queue.Request();
    }

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var properties = _formats.GetProperties(BlockRangeFormat.FormatName);
        var fill = _theme.Range;
        if (properties[MarkerFormatDefinition.FillId] is SolidColorBrush old &&
            old.Color == fill.Color && old.Opacity == fill.Opacity &&
            properties[EditorFormatDefinition.BackgroundColorId] is Color color && color == fill.Color &&
            properties[EditorFormatDefinition.BackgroundBrushId] is SolidColorBrush background &&
            background.Color == fill.Color && background.Opacity == fill.Opacity) return;

        // SSMS 優先讀 BackgroundColor；三個入口保持相同 Alpha，避免實心背景及逐輪變淡。
        properties[EditorFormatDefinition.BackgroundColorId] = fill.Color;
        properties[EditorFormatDefinition.BackgroundBrushId] = fill;
        properties[MarkerFormatDefinition.FillId] = fill;
        _formats.SetProperties(BlockRangeFormat.FormatName, properties);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Dispose();
        _theme.Changed -= OnChanged;
        _formats.FormatMappingChanged -= OnFormatChanged;
    }
}
