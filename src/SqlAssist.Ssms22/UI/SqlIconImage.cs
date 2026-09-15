using System;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>語意圖示插槽；影像由宿主依 <see cref="SqlIcon"/> 建立，每個插槽各自持有。</summary>
/// <remarks>
/// 這個型別不參照 VS SDK，純 WPF 測試才能直接編譯它。沒有接上 <see cref="Factory"/>
/// 或原生影像建立失敗時保留同尺寸空插槽：版面與對齊不因圖示有無而位移，也不另畫
/// 一套會與原生目錄長得不一樣的替代圖形。
/// </remarks>
internal sealed class SqlIconImage : Decorator
{
    /// <summary>套件初始化時接上原生 <c>CrispImage</c>；回傳的元素不得在插槽之間共用。</summary>
    internal static Func<SqlIcon, FrameworkElement?>? Factory { get; set; }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SqlIcon?), typeof(SqlIconImage),
        new PropertyMetadata(null, (sender, _) => ((SqlIconImage)sender).UpdateImage()));

    public SqlIcon? Icon
    {
        get => (SqlIcon?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public SqlIconImage()
    {
        Width = Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }

    private void UpdateImage() => Child = Icon is { } icon ? Factory?.Invoke(icon) : null;
}
