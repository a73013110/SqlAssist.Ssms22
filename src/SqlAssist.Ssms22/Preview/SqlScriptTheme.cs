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
    private IWpfTextView? _view;
    private readonly Control _host;
    private IClassificationFormatMap? _formatMap;
    private volatile bool _dirty = true;
    private volatile bool _disposed;
    private readonly ThemeRefreshQueue _refreshQueue;

    /// <param name="host">承載指令碼的控制項；資源掛在它身上，底色與前景跟著它。</param>
    public SqlScriptTheme(IWpfTextView? view, Control host)
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
        if (view is not null) view.BackgroundBrushChanged += OnAppearanceChanged;
    }

    public void SetView(IWpfTextView? view)
    {
        if (_disposed || ReferenceEquals(_view, view)) return;
        if (_view is not null) _view.BackgroundBrushChanged -= OnAppearanceChanged;
        _view = view;
        if (view is not null) view.BackgroundBrushChanged += OnAppearanceChanged;
        _dirty = true;
        EnsureCurrent();
    }

    public ResourceDictionary Resources { get; } = new();

    /// <summary>資源已換成新的外觀；自己繪製文字的表面據此重畫，文件式的呈現靠動態資源即可。</summary>
    public event EventHandler? Updated;

    public void EnsureCurrent()
    {
        _host.Dispatcher.VerifyAccess();
        if (_disposed || (!_dirty && (_formatMap is not null || _view is null)))
        {
            return;
        }

        _dirty = false;
        Refresh();
    }

    private void Refresh()
    {
        var font = _view is null ? SqlAssistChrome.CodeFont : FallbackFont;
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
            var view = _view is { IsClosed: false } ? _view : null;
            var map = view is null ? null : services?.TryGetTextFormatMap(view);
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

            if (map is null || services is null || view is null)
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
                    view.Background is SolidColorBrush editorBackground &&
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
        SetBrush(ScriptResource.Highlight, Highlight(comment, background));
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>命中底色：由主題強調色推導，對著<b>這一份指令碼</b>的底色與最淡的前景校正。</summary>
    /// <remarks>
    /// 不直接用 <see cref="ThemeBrush.AccentBackground"/>：那一份是對著工具窗的底色算的，
    /// 而指令碼的底色借自 SSMS 編輯器，兩者在深色主題下不一定相同——拿錯基準的症狀是
    /// 高亮幾乎看不見，而使用者會以為命中位置根本沒有標出來。
    ///
    /// 傳進去的前景是註解色，那是這幾種著色裡最淡的一個：它在高亮上讀得到，其餘就都讀得到。
    /// 高對比不必另外判斷——強調色在那時候已經等於前景色，校正過的結果本來就是實色反白。
    /// </remarks>
    private static Brush Highlight(Brush foreground, Brush background)
    {
        var accent = VsThemeBrushes.Get(ThemeBrush.AccentBorder);

        if (accent is not SolidColorBrush tint || foreground is not SolidColorBrush text ||
            background is not SolidColorBrush surface)
        {
            return accent;
        }

        // 先鋪一層半透明的強調色，再讓校正決定要往黑還是往白走；直接給實色會蓋掉語法著色。
        var candidate = Color.FromArgb(0x59, tint.Color.R, tint.Color.G, tint.Color.B);
        return new SolidColorBrush(
            ThemeColorMath.EnsureBackgroundForText(candidate, text.Color, surface.Color));
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
        if (_view is not null) _view.BackgroundBrushChanged -= OnAppearanceChanged;
        _host.IsVisibleChanged -= OnVisibilityChanged;
        if (_formatMap is not null)
        {
            _formatMap.ClassificationFormatMappingChanged -= OnAppearanceChanged;
            _formatMap = null;
        }
    }
}
