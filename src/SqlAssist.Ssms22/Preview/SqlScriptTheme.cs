using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Preview;

/// <summary>指令碼外觀的生命週期與查詢視窗一致；文件只消費動態資源。</summary>
internal sealed class SqlScriptTheme : IDisposable
{
    private IWpfTextView? _view;
    private readonly Control _host;
    private IClassificationFormatMap? _formatMap;
    private IEditorFormatMap? _editorFormats;
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
        // 還沒要到外觀就每次再試一次：殼層的 MEF 容器可能比第一次呼叫晚一步才備妥，
        // 而「一直沒有編輯器」不再是放棄的理由——沒有檢視時也問得到分類。
        if (_disposed || (!_dirty && _formatMap is not null))
        {
            return;
        }

        _dirty = false;
        Refresh();
    }

    private void Refresh()
    {
        // 問不到編輯器時才用這一組；CodeFont 自己就帶著 Cascadia Mono → Consolas → Courier New 的
        // 退路，字級沿用自製介面的內文字級，不另寫一個只有這裡看得到的數字。
        var font = SqlAssistChrome.CodeFont;
        var fontSize = SqlAssistChrome.DefaultMetrics.Body;
        var shell = (
            Background: ColorOf(ThemeBrush.ListBackground, Colors.Black),
            Foreground: ColorOf(ThemeBrush.ListForeground, Colors.White));
        var surface = shell;
        var comment = ColorOf(ThemeBrush.DimForeground, shell.Foreground);
        var keyword = surface.Foreground;
        var text = surface.Foreground;
        var number = surface.Foreground;

        SqlAssistPlatformGuard.Probe("解析 SQL 編輯器外觀", () =>
        {
            // 向殼層要，而不是只看編輯器登記過的那一份：只連了資料庫、一個查詢視窗都沒開時，
            // 登記那條路一次都沒有走過，而這裡仍要問得出著色分類。
            var services = SqlPreviewServices.Resolve();
            var view = _view is { IsClosed: false } ? _view : null;
            // 有編輯器就跟著那一個（它可能套了自己的外觀類別）；沒有就問「Text Editor」類別本身。
            // 兩條路要到的是同一份設定，所以使用者之後打開查詢視窗時顏色不會換一套。
            var map = services is null ? null
                : view is null ? services.TryGetDefaultTextFormatMap()
                : services.TryGetTextFormatMap(view);
            TrackFormatMap(map);
            var formats = services?.TryGetEditorFormatMap();
            TrackEditorFormats(formats);

            if (map is null || services is null)
            {
                return;
            }

            var defaults = map.DefaultTextProperties;

            // 字型與字級跟著同一份 Fonts and Colors，與底色、分類色同源：有檢視時它可能套了自己的
            // 外觀類別，沒有時問的是「Text Editor」類別本身，兩邊都是使用者替編輯器設的那個字型與字級。
            // 以「有沒有檢視」當條件的那一版在沒有查詢視窗時改用自己的字級，症狀是同一份 SQL
            // 在開查詢視窗前後大小會變。
            if (!defaults.TypefaceEmpty)
            {
                font = defaults.Typeface.FontFamily;
            }

            if (!defaults.FontRenderingEmSizeEmpty && defaults.FontRenderingEmSize > 0)
            {
                fontSize = defaults.FontRenderingEmSize;
            }

            if (SystemParameters.HighContrast)
            {
                return;
            }

            // 分類色必須搭配同一份 Fonts and Colors 的底色，不能把 SQL 前景放到 Tooltip 底色上；
            // 沒有查詢視窗時那組底色改由「Plain Text」那一格問出來，見 <see cref="EditorSurface"/>。
            surface = ScriptPalette.Surface(EditorSurface(view, defaults, formats), shell);

            // 殼層與分類映射的更新順序不固定；中途取不到某個分類時仍保留成對的備援。
            keyword = comment = text = number = surface.Foreground;
            var registry = services.ClassificationRegistry;
            keyword = Resolve(map, registry, PredefinedClassificationTypeNames.Keyword, surface);
            comment = Resolve(map, registry, PredefinedClassificationTypeNames.Comment, surface);
            text = Resolve(map, registry, PredefinedClassificationTypeNames.String, surface);
            number = Resolve(map, registry, PredefinedClassificationTypeNames.Number, surface);
        });

        SetResource(ScriptResource.FontFamily, font);
        SetResource(ScriptResource.FontSize, fontSize);
        SetBrush(ScriptResource.Background, surface.Background);
        SetBrush(ScriptResource.Foreground, surface.Foreground);
        SetBrush(ScriptResource.Keyword, keyword);
        SetBrush(ScriptResource.Comment, comment);
        SetBrush(ScriptResource.String, text);
        SetBrush(ScriptResource.Number, number);
        var highlight = Highlight(comment, surface.Background);
        SetBrush(ScriptResource.Highlight, highlight);
        var current = CurrentHighlight(surface.Background);
        SetBrush(ScriptResource.HighlightCurrent, current);
        SetBrush(ScriptResource.HighlightCurrentForeground,
            ThemeColorMath.EnsureTextContrast(surface.Foreground, current));
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 編輯器 Fonts and Colors 的底色與前景；缺任一個就回 null，不與工具窗那一組混用。
    /// </summary>
    /// <remarks>
    /// 底色有三個來源，依「離使用者看到的那個編輯器多近」排序：借得到檢視就用它的底色（它可能自己
    /// 覆寫過），否則用分類外觀的背景，再否則問「Plain Text」那一格。<b>第三條路不是備胎</b>：
    /// 編輯器的底色畫在檢視上而不在文字上，所以沒有查詢視窗時前兩條都是空的，而那正是這個問題發生
    /// 的時機——「比對佈景主題」加上彩色深色主題（月光、神秘森林、辣紅）時，工具窗的底色與編輯器
    /// 的底色不同深淺，分類色配上去對比不過關。
    /// </remarks>
    private static (Color Background, Color Foreground)? EditorSurface(
        IWpfTextView? view, TextFormattingRunProperties defaults, IEditorFormatMap? formats)
    {
        var plain = formats is null ? null : SqlAssistPlatformGuard.Probe<ResourceDictionary?>(
            "讀取編輯器純文字格式", () => formats.GetProperties(EditorFormatChanges.PlainText), fallback: null);

        var background = (view?.Background as SolidColorBrush)?.Color
            ?? (defaults.BackgroundBrushEmpty ? null : (defaults.BackgroundBrush as SolidColorBrush)?.Color)
            ?? plain?[EditorFormatDefinition.BackgroundColorId] as Color?
            ?? (plain?[EditorFormatDefinition.BackgroundBrushId] as SolidColorBrush)?.Color;
        var foreground = (defaults.ForegroundBrushEmpty ? null : (defaults.ForegroundBrush as SolidColorBrush)?.Color)
            ?? plain?[EditorFormatDefinition.ForegroundColorId] as Color?
            ?? (plain?[EditorFormatDefinition.ForegroundBrushId] as SolidColorBrush)?.Color;

        return background is { } surface && foreground is { } written ? (surface, written) : null;
    }

    /// <summary>
    /// 命中底色；推導與理由在 <see cref="ThemeColorMath.MatchBackground"/>，這裡只負責把
    /// <b>這一份指令碼自己的</b>底色與最淡的前景（註解色）當基準交出去。
    /// </summary>
    /// <remarks>
    /// 基準不能借工具窗那一份：指令碼的底色來自 SSMS 編輯器，兩者在深色主題下不一定相同。
    /// 高對比不必另外判斷——強調色在那時候已經等於前景色，校正過的結果本來就是實色反白。
    /// </remarks>
    private static Color Highlight(Color foreground, Color background) =>
        ThemeColorMath.MatchBackground(ColorOf(ThemeBrush.AccentBorder, foreground), foreground, background);

    /// <summary>目前那一處命中的底色；基準同樣是指令碼自己的底色。</summary>
    private static Color CurrentHighlight(Color background) =>
        ThemeColorMath.CurrentMatchBackground(ColorOf(ThemeBrush.AccentBorder, background), background);

    private static Color Resolve(
        IClassificationFormatMap map, IClassificationTypeRegistryService registry,
        string name, (Color Background, Color Foreground) surface)
    {
        var classification = registry.GetClassificationType(name);
        var properties = classification is null ? null : map.GetTextProperties(classification);
        var color = properties is { ForegroundBrushEmpty: false }
            ? (properties.ForegroundBrush as SolidColorBrush)?.Color
            : null;
        return ScriptPalette.Classification(color, surface.Foreground, surface.Background);
    }

    private static Color ColorOf(ThemeBrush key, Color fallback) =>
        VsThemeBrushes.Get(key) is SolidColorBrush brush ? brush.Color : fallback;

    private void TrackFormatMap(IClassificationFormatMap? map)
    {
        if (ReferenceEquals(map, _formatMap)) return;
        if (_formatMap is not null) _formatMap.ClassificationFormatMappingChanged -= OnAppearanceChanged;
        _formatMap = map;
        if (map is not null) map.ClassificationFormatMappingChanged += OnAppearanceChanged;
    }

    private void TrackEditorFormats(IEditorFormatMap? formats)
    {
        if (ReferenceEquals(formats, _editorFormats)) return;
        if (_editorFormats is not null) _editorFormats.FormatMappingChanged -= OnFormatsChanged;
        _editorFormats = formats;
        if (formats is not null) formats.FormatMappingChanged += OnFormatsChanged;
    }

    /// <summary>只有純文字那一格會換掉指令碼的底色；本擴充自己回寫的 marker 不是配色輸入。</summary>
    private void OnFormatsChanged(object sender, FormatItemsEventArgs args)
    {
        if (EditorFormatChanges.Affects(args.ChangedItems, EditorFormatChanges.PlainText))
        {
            OnAppearanceChanged(sender, args);
        }
    }

    /// <summary>不保存編輯器借出的筆刷：自己建一支凍結的，免得干擾 SSMS 的外觀更新。</summary>
    private void SetBrush(ScriptResource key, Color value)
    {
        if (Resources[key] is SolidColorBrush existing && existing.Color == value)
        {
            return;
        }

        var brush = new SolidColorBrush(value);
        brush.Freeze();
        Resources[key] = brush;
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

        if (_editorFormats is not null)
        {
            _editorFormats.FormatMappingChanged -= OnFormatsChanged;
            _editorFormats = null;
        }
    }
}
