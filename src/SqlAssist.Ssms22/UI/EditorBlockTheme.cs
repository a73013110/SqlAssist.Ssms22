using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.UI;

/// <summary>每個檢視一份配色與通知；背景、色帶及提示共用，換色不接觸 SQL 分析。</summary>
internal sealed class EditorBlockTheme : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly ThemeResourceSet _resources = new();
    private readonly ThemeRefreshQueue _queue;
    private IEditorFormatMap? _formats;
    private SqlAssistSettings? _settings;
    private bool _disposed;
    public event EventHandler? Changed;

    public SolidColorBrush Range => (SolidColorBrush)_resources.Get(ThemeBrush.BlockRange);
    public SolidColorBrush GetBrush(ThemeBrush role) => (SolidColorBrush)_resources.Get(role);

    public static EditorBlockTheme Get(IWpfTextView view) =>
        view.Properties.GetOrCreateSingletonProperty(() => new EditorBlockTheme(view));

    private EditorBlockTheme(IWpfTextView view)
    {
        _view = view;
        _queue = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新編輯器區塊配色", Refresh));
        VsThemeBrushes.Changed += OnChanged;
        SqlAssistSettingsStore.Changed += OnSettings;
        view.BackgroundBrushChanged += OnChanged;
        view.Closed += OnClosed;
        Refresh();
    }

    public void Apply(FrameworkElement element)
    {
        // 局部資源只覆寫區塊鍵，不影響 SSMS 或其他工具視窗。
        if (!element.Resources.MergedDictionaries.Contains(_resources.Resources))
            element.Resources.MergedDictionaries.Add(_resources.Resources);
    }

    public void AttachFormats(IEditorFormatMap formats)
    {
        if (_disposed || ReferenceEquals(_formats, formats)) return;
        if (_formats is not null) _formats.FormatMappingChanged -= OnFormatChanged;
        _formats = formats;
        _formats.FormatMappingChanged += OnFormatChanged;
        _queue.Request();
    }

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var background = (_view.Background as SolidColorBrush ??
            (SolidColorBrush)VsThemeBrushes.Get(ThemeBrush.ListBackground)).Color;
        var plain = _formats?.GetProperties("Plain Text");
        var foreground = plain?[EditorFormatDefinition.ForegroundColorId] as Color? ??
            (plain?[EditorFormatDefinition.ForegroundBrushId] as SolidColorBrush ??
                (SolidColorBrush)VsThemeBrushes.Get(ThemeBrush.ListForeground)).Color;
        var highContrast = SystemParameters.HighContrast;
        // 高對比以系統配對色為優先，不把使用者基準色混進可及性配色。
        if (highContrast) { background = SystemColors.WindowColor; foreground = SystemColors.WindowTextColor; }
        _settings = SqlAssistSettingsStore.Current;
        var colors = BlockPalette.Create(background, foreground,
            ((SolidColorBrush)VsThemeBrushes.Get(ThemeBrush.AccentBorder)).Color, _settings.BlockAccentColor, highContrast, _settings);
        var changed = false;
        foreach (var pair in colors)
        {
            if (_resources.Resources[pair.Key] is not SolidColorBrush current || current.Color != pair.Value)
                changed = true;
        }
        if (!changed) return;
        _resources.Update(colors);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnChanged(object? sender, EventArgs args) => _queue.Request();
    private void OnFormatChanged(object? sender, FormatItemsEventArgs args)
    {
        // 本擴充回寫的端點／背景格式不是配色輸入，不能再引發整輪色票重算。
        if (EditorFormatChanges.Affects(args.ChangedItems, "Plain Text")) _queue.Request();
    }
    private void OnSettings(object? sender, EventArgs args)
    {
        var next = SqlAssistSettingsStore.Current;
        if (_settings?.BlockAccentColor != next.BlockAccentColor ||
            _settings?.BlockKeywordForeground != next.BlockKeywordForeground || _settings?.BlockKeywordBackground != next.BlockKeywordBackground ||
            _settings?.BlockSymbolForeground != next.BlockSymbolForeground || _settings?.BlockSymbolBackground != next.BlockSymbolBackground)
            _queue.Request();
    }
    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉編輯器區塊主題", Dispose);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Dispose();
        VsThemeBrushes.Changed -= OnChanged;
        SqlAssistSettingsStore.Changed -= OnSettings;
        if (_formats is not null) _formats.FormatMappingChanged -= OnFormatChanged;
        _view.BackgroundBrushChanged -= OnChanged;
        _view.Closed -= OnClosed;
        Changed = null;
    }
}
