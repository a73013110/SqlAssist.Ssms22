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
[UserVisible(true)]
internal sealed class BlockRangeFormat : MarkerFormatDefinition
{
    public const string FormatName = "SqlAssist.BlockRange";

    public BlockRangeFormat()
    {
        DisplayName = "SqlAssist 區塊區間背景";
        ZOrder = 0;
        BackgroundCustomizable = true;
        ForegroundCustomizable = false;
    }
}

/// <summary>只更新本擴充的 marker；保留字型與色彩的使用者設定，且不改動原生 bracehighlight。</summary>
internal sealed class BlockRangeAppearance : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly IEditorFormatMap _formats;
    private readonly ThemeRefreshQueue _queue;

    public BlockRangeAppearance(IWpfTextView view, IEditorFormatMap formats)
    {
        _view = view;
        _formats = formats;
        _queue = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新區塊區間配色", Refresh));
        VsThemeBrushes.Changed += OnChanged;
        view.BackgroundBrushChanged += OnChanged;
        formats.FormatMappingChanged += OnFormatChanged;
        _queue.Request();
    }

    private void OnChanged(object? sender, EventArgs args) => _queue.Request();
    private void OnFormatChanged(object? sender, FormatItemsEventArgs args) => _queue.Request();

    private void Refresh()
    {
        if (_view.IsClosed) return;
        var properties = _formats.GetProperties(BlockRangeFormat.FormatName);
        var plain = _formats.GetProperties("Plain Text");
        var custom = properties[EditorFormatDefinition.BackgroundColorId] as Color?;
        var source = plain[EditorFormatDefinition.ForegroundBrushId] as SolidColorBrush ??
            (SolidColorBrush)VsThemeBrushes.Get(ThemeBrush.ListForeground);
        var color = custom ?? source.Color;
        var opacity = SystemParameters.HighContrast ? 0 : 0.08;
        var fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)) { Opacity = opacity };
        fill.Freeze();
        var markerColor = Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B);
        if (properties[MarkerFormatDefinition.FillId] is SolidColorBrush old &&
            old.Color == fill.Color && old.Opacity == fill.Opacity && (!custom.HasValue || custom == markerColor)) return;

        // SSMS 的 marker renderer 優先讀 BackgroundColor；自訂色也須套透明度，且不可逐輪相乘。
        if (custom.HasValue) properties[EditorFormatDefinition.BackgroundColorId] = markerColor;
        properties[MarkerFormatDefinition.FillId] = fill;
        _formats.SetProperties(BlockRangeFormat.FormatName, properties);
    }

    public void Dispose()
    {
        _queue.Dispose();
        VsThemeBrushes.Changed -= OnChanged;
        _view.BackgroundBrushChanged -= OnChanged;
        _formats.FormatMappingChanged -= OnFormatChanged;
    }
}
