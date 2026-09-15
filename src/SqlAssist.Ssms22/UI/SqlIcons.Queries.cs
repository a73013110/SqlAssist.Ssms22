using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlIcons
{
    private static readonly DependencyProperty QueryImageBackdropProperty = DependencyProperty.RegisterAttached(
        "QueryImageBackdrop", typeof(Brush), typeof(SqlIcons), new PropertyMetadata(null));
    // 必須在 Browser 的欄位建立前接好；每個插槽各有 CrispImage，絕不共用視覺節點。
    public static void RegisterQueryImages() => SqlQueryIcon.NativeImageFactory = CreateQueryImage;

    private static FrameworkElement? CreateQueryImage(string name)
    {
        if (name == "Chevron") return null;
        return SqlAssistPlatformGuard.Probe<FrameworkElement?>("SQL Memory 原生圖示", () =>
        {
            var image = new CrispImage { Width = 16, Height = 16, Moniker = QueryMoniker(name) };
            // ImageThemingUtilities 會從承載 CrispImage 的表面讀背景；透明 Host
            // 只提供這個主題上下文，不畫自己的底色或切斷膠囊。
            var host = new Border { Background = Brushes.Transparent, Child = image };
            // 透明幽靈按鈕要合成宿主底色；hover／高對比選取則以實際表面轉換原生配色。
            var background = new MultiBinding { Converter = QueryImageBackgroundConverter.Instance };
            background.Bindings.Add(new Binding(nameof(Border.Background))
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Border), 1) });
            host.SetResourceReference(QueryImageBackdropProperty, ThemeBrush.WindowBackground);
            background.Bindings.Add(new Binding { Source = host, Path = new PropertyPath(QueryImageBackdropProperty) });
            host.SetBinding(ImageThemingUtilities.ImageBackgroundColorProperty, background);
            return host;
        }, null);
    }

    internal static ImageMoniker QueryMoniker(string name) => name switch
    {
        // 與 Menus.vsct 的 History／Favorites 使用完全相同的目錄識別。
        "History" => KnownMonikers.History, "Favorite" => KnownMonikers.Favorite,
        "Database" => Database.Moniker, "Server" => KnownMonikers.DataServer,
        "All" => KnownMonikers.All, "Execute" => KnownMonikers.Execute, "Edit" => KnownMonikers.Edit,
        "Calendar" => KnownMonikers.Calendar, "Any" => KnownMonikers.Infinity, "Global" => KnownMonikers.WorldLocal,
        "Search" => KnownMonikers.Search, "Clear" => KnownMonikers.Cancel,
        "Copy" => KnownMonikers.Copy, "Open" => KnownMonikers.OpenQuery, "Remove" => KnownMonikers.Delete,
        "Wrap" => KnownMonikers.WordWrap, "Connection" => KnownMonikers.ConnectToDatabase,
        "Refresh" => KnownMonikers.Refresh, "Settings" => KnownMonikers.Settings,
        "Recent" or "ReverseAlphabetical" => KnownMonikers.SortDescending,
        "Oldest" or "Alphabetical" => KnownMonikers.SortAscending,
        _ => KnownMonikers.History
    };

    private sealed class QueryImageBackgroundConverter : IMultiValueConverter
    {
        public static readonly QueryImageBackgroundConverter Instance = new();
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var backdrop = values[1] is SolidColorBrush window ? window.Color : SystemColors.WindowColor;
            return values[0] is SolidColorBrush surface ? ThemeColorMath.Composite(surface.Color, backdrop) : backdrop;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
