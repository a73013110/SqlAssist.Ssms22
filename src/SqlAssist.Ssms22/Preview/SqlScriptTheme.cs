using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Preview;

/// <summary>指令碼外觀的生命週期與查詢視窗一致；文件只消費動態資源。</summary>
internal sealed class SqlScriptTheme : IDisposable
{
    private static readonly FontFamily FallbackFont = new("Consolas");
    private readonly IWpfTextView _view;
    private readonly RichTextBox _host;
    private IClassificationFormatMap? _formatMap;
    private volatile bool _dirty = true;
    private volatile bool _disposed;
    private readonly ThemeRefreshQueue _refreshQueue;

    public SqlScriptTheme(IWpfTextView view, RichTextBox host)
    {
        _view = view;
        _host = host;
        _refreshQueue = new ThemeRefreshQueue(host.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新指令碼外觀", () =>
            {
                if (_host.IsVisible)
                {
                    EnsureCurrent();
                }
            }));
        EnsureCurrent();
        host.Resources.MergedDictionaries.Add(Resources);
        host.SetResourceReference(Control.BackgroundProperty, ScriptResource.Background);
        host.SetResourceReference(Control.ForegroundProperty, ScriptResource.Foreground);
        host.IsVisibleChanged += OnVisibilityChanged;
        VsThemeBrushes.Changed += OnAppearanceChanged;
        view.BackgroundBrushChanged += OnAppearanceChanged;
    }

    public ResourceDictionary Resources { get; } = new();

    public void EnsureCurrent()
    {
        _host.Dispatcher.VerifyAccess();
        if (_disposed || _view.IsClosed || (!_dirty && _formatMap is not null))
        {
            return;
        }

        _dirty = false;
        Refresh();
    }

    private void Refresh()
    {
        var font = FallbackFont;
        var fontSize = 12.5;
        var background = VsThemeBrushes.Get(ThemeBrush.ListBackground);
        var foreground = VsThemeBrushes.Get(ThemeBrush.ListForeground);
        var comment = VsThemeBrushes.Get(ThemeBrush.DimForeground);
        var keyword = foreground;
        var text = foreground;
        var number = foreground;

        SqlAssistPlatformGuard.Probe("解析 SQL 編輯器外觀", () =>
        {
            var services = SqlPreviewServices.Current;
            var map = services?.TryGetTextFormatMap(_view);
            if (!ReferenceEquals(map, _formatMap))
            {
                if (_formatMap is not null)
                {
                    _formatMap.ClassificationFormatMappingChanged -= OnAppearanceChanged;
                }

                _formatMap = map;
                if (map is not null)
                {
                    map.ClassificationFormatMappingChanged += OnAppearanceChanged;
                }
            }

            if (map is null || services is null)
            {
                return;
            }

            var defaults = map.DefaultTextProperties;
            if (!defaults.TypefaceEmpty)
            {
                font = defaults.Typeface.FontFamily;
            }

            if (!defaults.FontRenderingEmSizeEmpty && defaults.FontRenderingEmSize > 0)
            {
                fontSize = defaults.FontRenderingEmSize;
            }

            if (!SystemParameters.HighContrast)
            {
                // 分類色必須搭配同一個編輯器的底色，不能把 SQL 前景放到 Tooltip 底色上。
                if (!defaults.ForegroundBrushEmpty &&
                    defaults.ForegroundBrush is SolidColorBrush editorForeground &&
                    _view.Background is SolidColorBrush editorBackground &&
                    ThemeColorMath.Contrast(editorForeground.Color, editorBackground.Color) >= 4.5)
                {
                    background = editorBackground;
                    foreground = editorForeground;
                }

                // 殼層與分類映射的更新順序不固定；中途取不到某個分類時仍保留成對的備援。
                keyword = comment = text = number = foreground;
                var registry = services.ClassificationRegistry;
                keyword = Resolve(map, registry, PredefinedClassificationTypeNames.Keyword, foreground, background);
                comment = Resolve(map, registry, PredefinedClassificationTypeNames.Comment, foreground, background);
                text = Resolve(map, registry, PredefinedClassificationTypeNames.String, foreground, background);
                number = Resolve(map, registry, PredefinedClassificationTypeNames.Number, foreground, background);
            }
        });

        SetResource(ScriptResource.FontFamily, font);
        SetResource(ScriptResource.FontSize, fontSize);
        SetBrush(ScriptResource.Background, background);
        SetBrush(ScriptResource.Foreground, foreground);
        SetBrush(ScriptResource.Keyword, keyword);
        SetBrush(ScriptResource.Comment, comment);
        SetBrush(ScriptResource.String, text);
        SetBrush(ScriptResource.Number, number);
    }

    private static Brush Resolve(
        IClassificationFormatMap map, IClassificationTypeRegistryService registry,
        string name, Brush fallback, Brush background)
    {
        var classification = registry.GetClassificationType(name);
        if (classification is null)
        {
            return fallback;
        }

        var properties = map.GetTextProperties(classification);
        if (properties.ForegroundBrushEmpty)
        {
            return fallback;
        }

        var brush = properties.ForegroundBrush;
        return brush is SolidColorBrush color && background is SolidColorBrush surface &&
               ThemeColorMath.Contrast(color.Color, surface.Color) >= 4.5
            ? brush : fallback;
    }

    private void SetBrush(ScriptResource key, Brush value)
    {
        if (value is SolidColorBrush color && Resources[key] is SolidColorBrush existing &&
            color.Color == existing.Color)
        {
            return;
        }

        // 不凍結或修改編輯器借出的筆刷，避免干擾 SSMS 自己的外觀更新。
        var copy = value.CloneCurrentValue();
        if (copy.CanFreeze)
        {
            copy.Freeze();
        }

        Resources[key] = copy;
    }

    private void SetResource(ScriptResource key, object value)
    {
        if (!Equals(Resources[key], value))
        {
            Resources[key] = value;
        }
    }

    private void OnAppearanceChanged(object sender, EventArgs args)
    {
        _dirty = true;
        SqlAssistPlatformGuard.Probe("排程指令碼外觀更新", _refreshQueue.Request);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_host.IsVisible)
        {
            SqlAssistPlatformGuard.Probe("顯示目前的指令碼外觀", EnsureCurrent);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshQueue.Dispose();
        VsThemeBrushes.Changed -= OnAppearanceChanged;
        _view.BackgroundBrushChanged -= OnAppearanceChanged;
        _host.IsVisibleChanged -= OnVisibilityChanged;
        if (_formatMap is not null)
        {
            _formatMap.ClassificationFormatMappingChanged -= OnAppearanceChanged;
            _formatMap = null;
        }
    }
}
