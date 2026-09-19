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
    private static readonly DependencyProperty ImageBackdropProperty = DependencyProperty.RegisterAttached(
        "ImageBackdrop", typeof(Brush), typeof(SqlIcons), new PropertyMetadata(null));

    /// <summary>接上自製 UI 的原生圖示；必須在任何工具窗建立前呼叫，否則那些插槽會是空的。</summary>
    public static void RegisterImages()
    {
        SqlIconImage.Factory = CreateImage;
        SqlIconImage.CategoryFactory = CreateCategoryImage;
    }

#pragma warning disable CS8524
    // 刻意不寫預設分支：CS8524 只針對未命名的列舉值，留著它就得補 `_` 分支，
    // 而那會吃掉「SqlIcon 新增成員卻漏了對照」的 CS8509 編譯錯誤。
    public static ImageMoniker GetMoniker(SqlIcon icon) => icon switch
    {
        // 與 Menus.vsct 的 History／Favorites 使用完全相同的目錄識別。
        SqlIcon.History => KnownMonikers.History,
        SqlIcon.Favorite => KnownMonikers.Favorite,
        SqlIcon.All => KnownMonikers.All,
        SqlIcon.Execute => KnownMonikers.Execute,
        SqlIcon.Edit => KnownMonikers.Edit,
        SqlIcon.Calendar => KnownMonikers.Calendar,
        SqlIcon.AnyTime => KnownMonikers.Infinity,
        SqlIcon.Server => KnownMonikers.DataServer,
        // 與補全清單的資料庫同一顆，兩邊不各自挑。
        SqlIcon.Database => Database.Moniker,
        SqlIcon.Connection => KnownMonikers.ConnectToDatabase,
        SqlIcon.Search => KnownMonikers.Search,
        SqlIcon.Clear => KnownMonikers.Cancel,
        SqlIcon.MatchCase => KnownMonikers.MatchCase,
        SqlIcon.WholeWord => KnownMonikers.WholeWord,
        SqlIcon.Filter => KnownMonikers.Filter,
        SqlIcon.SelectAll => KnownMonikers.SelectAll,
        SqlIcon.Copy => KnownMonikers.Copy,
        SqlIcon.Open => KnownMonikers.OpenQuery,
        SqlIcon.Remove => KnownMonikers.Delete,
        SqlIcon.Wrap => KnownMonikers.WordWrap,
        SqlIcon.Refresh => KnownMonikers.Refresh,
        SqlIcon.Settings => KnownMonikers.Settings,
        SqlIcon.SortAscending => KnownMonikers.SortAscending,
        SqlIcon.SortDescending => KnownMonikers.SortDescending,
        // 依物件種類排序；與名稱 A–Z 共用排序按鈕，圖示要分得出排的是哪一種鍵。
        SqlIcon.SortByKind => KnownMonikers.SortByType,
        SqlIcon.Preview => KnownMonikers.ScriptPreview,
        SqlIcon.Compare => KnownMonikers.Diff,
        // 回溯是「以舊版本另存新版本」，借用復原的形狀；語意由標籤與確認框說清楚。
        SqlIcon.Revert => KnownMonikers.Undo,
        // 用量是「量表」而不是圖表：看的是離上限多遠，不是趨勢。
        SqlIcon.Usage => KnownMonikers.GaugeRound,
        SqlIcon.Cleanup => KnownMonikers.CleanData,
        // 壓縮只把檔案縮小，不刪任何資料；借收合的形狀，不用垃圾桶以免讀成刪除。
        SqlIcon.Compact => KnownMonikers.CollapseAll,
        SqlIcon.Maintain => KnownMonikers.Run,
        SqlIcon.Backup => KnownMonikers.SaveAs,
        SqlIcon.Folder => KnownMonikers.FolderOpened,
        SqlIcon.Warning => KnownMonikers.StatusWarning,
        SqlIcon.SelfTest => KnownMonikers.Test
    };
#pragma warning restore CS8524

    private static FrameworkElement? CreateImage(SqlIcon icon) => CreateImage(GetMoniker(icon));

    /// <summary>搜尋結果列的物件種類圖示；認不得的分類不畫圖示，那一列仍有標題、路徑與膠囊。</summary>
    private static FrameworkElement? CreateCategoryImage(string categoryId) =>
        TryGetCategoryMoniker(categoryId, out var moniker) ? CreateImage(moniker) : null;

    private static FrameworkElement? CreateImage(ImageMoniker moniker) =>
        SqlAssistPlatformGuard.Probe<FrameworkElement?>("原生圖示", () =>
        {
            var image = new CrispImage { Width = 16, Height = 16, Moniker = moniker };
            // ImageThemingUtilities 會從承載 CrispImage 的表面讀背景；透明 Host
            // 只提供這個主題上下文，不畫自己的底色或切斷膠囊。
            var host = new Border { Background = Brushes.Transparent, Child = image };
            // 透明幽靈按鈕要合成宿主底色；hover／高對比選取則以實際表面轉換原生配色。
            var background = new MultiBinding { Converter = ImageBackgroundConverter.Instance };
            background.Bindings.Add(new Binding(nameof(Border.Background))
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Border), 1) });
            host.SetResourceReference(ImageBackdropProperty, ThemeBrush.WindowBackground);
            background.Bindings.Add(new Binding { Source = host, Path = new PropertyPath(ImageBackdropProperty) });
            host.SetBinding(ImageThemingUtilities.ImageBackgroundColorProperty, background);
            return host;
        }, null);

    private sealed class ImageBackgroundConverter : IMultiValueConverter
    {
        public static readonly ImageBackgroundConverter Instance = new();
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var backdrop = values[1] is SolidColorBrush window ? window.Color : SystemColors.WindowColor;
            return values[0] is SolidColorBrush surface ? ThemeColorMath.Composite(surface.Color, backdrop) : backdrop;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
