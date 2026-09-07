using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;

namespace SqlAssist.Ssms22.UI;

/// <summary>每個檢視一份動態色帶資源，額外照顧與 SSMS 殼層不同的自訂編輯器底色。</summary>
internal sealed class EditorBlockTheme : IDisposable
{
    private static readonly ThemeBrush[] Roles =
    {
        ThemeBrush.Block, ThemeBrush.BlockTry, ThemeBrush.BlockCatch,
        ThemeBrush.BlockCase, ThemeBrush.BlockParenthesis, ThemeBrush.BlockBracket
    };
    private readonly IWpfTextView _view;
    private readonly ResourceDictionary _resources = new();
    private readonly ThemeRefreshQueue _queue;
    private bool _disposed;

    public static EditorBlockTheme Get(IWpfTextView view) =>
        view.Properties.GetOrCreateSingletonProperty(() => new EditorBlockTheme(view));

    private EditorBlockTheme(IWpfTextView view)
    {
        _view = view;
        _queue = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新編輯器區塊色帶", Refresh));
        VsThemeBrushes.Changed += OnChanged;
        view.BackgroundBrushChanged += OnChanged;
        view.Closed += OnClosed;
        Refresh();
    }

    public void Apply(FrameworkElement element)
    {
        // 這份字典置於共用主題之後，只覆寫六個色帶鍵，不另建介面樣式。
        if (!element.Resources.MergedDictionaries.Contains(_resources))
            element.Resources.MergedDictionaries.Add(_resources);
    }

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var background = (_view.Background as SolidColorBrush ??
            (SolidColorBrush)VsThemeBrushes.Get(ThemeBrush.ListBackground)).Color;
        foreach (var role in Roles)
        {
            var source = (SolidColorBrush)VsThemeBrushes.Get(role);
            var color = ThemeColorMath.EnsureGraphicContrast(source.Color, background);
            if (_resources[role] is SolidColorBrush current && current.Color == color) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _resources[role] = brush;
        }
    }

    private void OnChanged(object? sender, EventArgs args) => _queue.Request();
    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉編輯器區塊主題", Dispose);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Dispose();
        VsThemeBrushes.Changed -= OnChanged;
        _view.BackgroundBrushChanged -= OnChanged;
        _view.Closed -= OnClosed;
    }
}
