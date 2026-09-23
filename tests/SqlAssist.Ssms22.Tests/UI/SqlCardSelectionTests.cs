using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SqlAssist.Core.Search;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Core.Tabular;
using SqlAssist.Ssms22.Search;
using SqlAssist.Ssms22.SqlMemory;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

/// <summary>清單多選：模式進出、範圍、全選、續頁、容器重用、複製內容，以及 SQL Search 接上同一份。</summary>
public sealed class SqlCardSelectionTests
{
    static SqlCardSelectionTests() => SqlIconImage.Factory = icon => new Border { Width = 16, Height = 16, Background = Brushes.Gray, Tag = icon };

    private static SqlMemoryRow Row(string name) => new(new SqlHistoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "content", new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero), SqlHistoryFilter.Executions, name,
        "SELECT * FROM Loan;", new SqlConnectionLabel("LibraryServer", "Library")));

    /// <summary>一份開了多選的 SQL Memory 清單，掛在隱藏的 presentation source 上，鍵盤事件才路由得到。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly HwndSource _source;

        public Harness(int count, double height = 300)
        {
            foreach (var index in Enumerable.Range(0, count)) Rows.Add(Row("Loan" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)));
            Selection = new SqlCardSelection<SqlMemoryRow, Guid>(Rows, row => row.Id);
            Selection.AddAction(new SqlSelectionAction(SqlIcon.Copy, "複製", () =>
            {
                Copied = SqlMemoryRow.CopyContent(Selection.CheckedRows(), favorites: false);
                return Task.FromResult(true);
            }, shortcutKey: Key.C, shortcutModifiers: ModifierKeys.Control));
            List = new SqlMemoryList { ModifierSource = () => Modifiers };
            List.ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle(motion: false, checkable: true);
            List.ItemTemplate = SqlAssistChrome.CreateSqlSummaryTemplate(motion: false);
            List.SetRowsSource(Rows, new Border { Height = 20 });
            List.EnableSelection(Selection);
            Search = new TextBox();
            Bar = new SqlSelectionBar(Selection, Search, motion: false) { ReturnFocus = List.FocusCurrentRow };
            var root = new DockPanel();
            var header = new SqlStack(4);
            header.Children.Add(Bar.Slot);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(List);
            _source = new HwndSource(new HwndSourceParameters("selection test") { Width = 440, Height = (int)height, WindowStyle = 0 })
            {
                RootVisual = root
            };
            root.Measure(new Size(440, height)); root.Arrange(new Rect(0, 0, 440, height)); root.UpdateLayout();
        }

        public ObservableCollection<SqlMemoryRow> Rows { get; } = new();
        public SqlCardSelection<SqlMemoryRow, Guid> Selection { get; }
        public SqlMemoryList List { get; }
        public SqlSelectionBar Bar { get; }
        public TextBox Search { get; }
        public ModifierKeys Modifiers { get; set; }
        public SqlTabularContent? Copied { get; private set; }

        public ListBoxItem Container(int index)
        {
            List.ScrollIntoView(Rows[index]);
            List.UpdateLayout();
            return (ListBoxItem)List.ItemContainerGenerator.ContainerFromIndex(index);
        }

        public bool Press(Key key, int index, ModifierKeys modifiers = ModifierKeys.None)
        {
            var args = new KeyEventArgs(new TestKeyboardDevice(modifiers), _source, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = Container(index) };
            List.RaiseEvent(args);
            return args.Handled;
        }

        public void Click(int index, ModifierKeys modifiers = ModifierKeys.None)
        {
            Modifiers = modifiers;
            var target = Descendants<TextBlock>(Container(index)).First(text => text.Text == Rows[index].Name);
            List.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = target });
            Modifiers = ModifierKeys.None;
        }

        public bool[] Checked => Rows.Select(row => row.IsChecked).ToArray();

        public void Dispose()
        {
            _source.RootVisual = null;
            _source.Dispose();
        }
    }

    [Fact]
    public void SpaceEntersModeAndEscOrLastUncheckLeavesIt()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(5);
            Assert.False(harness.Bar.IsShown);
            Assert.True(harness.Search.IsEnabled);

            Assert.True(harness.Press(Key.Space, 1));
            Assert.True(harness.Selection.IsActive);
            Assert.True(harness.Bar.IsShown);
            Assert.False(harness.Search.IsEnabled);
            Assert.Equal("已選 1 筆", harness.Bar.CountText);
            Assert.True(SqlRowCheck.GetIsActive(harness.Container(3)));

            // 最後一筆取消勾選就自動離開模式。
            Assert.True(harness.Press(Key.Space, 1));
            Assert.False(harness.Selection.IsActive);
            Assert.False(harness.Bar.IsShown);
            Assert.True(harness.Search.IsEnabled);

            harness.Press(Key.Space, 0); harness.Press(Key.Space, 2);
            Assert.True(harness.Press(Key.Escape, 2));
            Assert.All(harness.Checked, value => Assert.False(value));
            Assert.False(harness.Bar.IsShown);
            // 不在多選模式時 Esc 不攔，讓宿主照舊處理。
            Assert.False(harness.Press(Key.Escape, 2));
        });
    }

    [Fact]
    public void ShiftSelectsARangeFromTheAnchorAndCtrlASelectsAll()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(8);
            harness.Press(Key.Space, 1);
            Assert.True(harness.Press(Key.Space, 4, ModifierKeys.Shift));
            Assert.Equal(new[] { false, true, true, true, true, false, false, false }, harness.Checked);

            // 錨點不動：再 Shift 另一端，從同一個錨點往上勾。
            harness.Click(0, ModifierKeys.Shift);
            Assert.Equal(new[] { true, true, true, true, true, false, false, false }, harness.Checked);
            Assert.Same(harness.Rows[0], harness.List.SelectedItem);

            // Ctrl+A 與工具列的全選同一件事：全部符合；全都已經載入時說得出確切筆數。
            Assert.True(harness.Press(Key.A, 0, ModifierKeys.Control));
            Assert.All(harness.Checked, value => Assert.True(value));
            Assert.Equal(harness.Rows.Count, harness.Selection.Count);
            Assert.True(harness.Selection.IsAllMatching);
            Assert.Equal("已選全部 8 筆", harness.Bar.CountText);
            Assert.Equal("", harness.Bar.NoteText);
            Assert.False(harness.Bar.IsSelectAllEnabled);
        });
    }

    [Fact]
    public void ClicksToggleInModeAndMoveTheFocusRowWithoutDroppingIt()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(5);
            harness.List.SelectedIndex = 2;
            // 不在多選模式時單擊只是選取與預覽，不勾。
            harness.Click(3);
            Assert.False(harness.Selection.IsActive);

            harness.Click(2, ModifierKeys.Control);
            Assert.True(harness.Rows[2].IsChecked);
            // 單選 ListBox 的 Ctrl+點擊會把已選取的那一列取消選取；焦點與預覽不能因此清空。
            Assert.Same(harness.Rows[2], harness.List.SelectedItem);
            harness.Click(4);
            Assert.True(harness.Rows[4].IsChecked);
            Assert.Same(harness.Rows[4], harness.List.SelectedItem);
            harness.Click(4);
            Assert.False(harness.Rows[4].IsChecked);

            // ↑／↓ 只移動焦點，不改勾選。
            Assert.False(harness.Press(Key.Down, 2));
            Assert.Equal(new[] { false, false, true, false, false }, harness.Checked);
        });
    }

    [Fact]
    public void CheckColumnOnlyTakesSpaceInSelectionMode()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(3);
            double NameLeft(int index)
            {
                var container = harness.Container(index);
                var name = Descendants<TextBlock>(container).First(text => text.Text == harness.Rows[index].Name);
                return name.TranslatePoint(new Point(), container).X;
            }

            var check = Descendants<SqlRowCheckBox>(harness.Container(0)).Single();
            var before = NameLeft(0);
            // 平常名稱就是最左邊那一個，勾選欄連寬度都不佔。
            Assert.Equal(Visibility.Collapsed, check.Visibility);

            harness.Press(Key.Space, 1);
            harness.List.UpdateLayout();
            Assert.Equal(Visibility.Visible, check.Visibility);
            Assert.False(check.IsChecked);
            Assert.Equal(before + SqlAssistChrome.RowCheckColumnWidth, NameLeft(0), 1);
            Assert.Equal(new CornerRadius(7), Descendants<Border>(check).First().CornerRadius);

            harness.Press(Key.Escape, 1);
            harness.List.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, check.Visibility);
            Assert.Equal(before, NameLeft(0), 1);
        });
    }

    [Fact]
    public void RowCheckBoxGoesThroughTheSelectionInsteadOfFlippingItself()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(3);
            var check = Descendants<SqlRowCheckBox>(harness.Container(1)).Single();
            Assert.Equal("選取 Loan001", System.Windows.Automation.AutomationProperties.GetName(check));
            Assert.False(check.Focusable);
            Assert.Equal(Visibility.Collapsed, check.Visibility);

            var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(check);
            toggle.Toggle();
            Assert.True(harness.Rows[1].IsChecked);
            Assert.True(check.IsChecked);
            Assert.Equal(Visibility.Visible, check.Visibility);

            // 由控制器取消勾選，單向繫結仍然接得上：勾選框沒有自己的本機值。
            harness.Selection.Clear();
            Assert.False(check.IsChecked);
        });
    }

    [Fact]
    public void LoadMoreKeepsChecksAndSelectAllCoversRowsNotLoadedYet()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(3);
            harness.Selection.HasMore = true;
            harness.Press(Key.Space, 0);

            // 續頁：勾選留著，新列不勾。
            harness.Rows.Add(Row("Loan900"));
            Assert.Equal(new[] { true, false, false, false }, harness.Checked);
            Assert.True(harness.Bar.IsSelectAllEnabled);

            // 全選一步就是全部符合：還有沒載入的列時只說「全部」，已載入幾筆接在後面。
            ClickSelectAll(harness.Bar);
            Assert.True(harness.Selection.IsAllMatching);
            Assert.All(harness.Checked, value => Assert.True(value));
            Assert.Equal("已選全部符合的項目", harness.Bar.CountText);
            Assert.StartsWith("已載入 4 筆", harness.Bar.NoteText);
            Assert.False(harness.Bar.IsSelectAllEnabled);

            // 全部符合時續頁進來的列本來就在範圍裡；載入到底之後說得出確切筆數。
            harness.Rows.Add(Row("Loan901"));
            Assert.True(harness.Rows[4].IsChecked);
            Assert.StartsWith("已載入 5 筆", harness.Bar.NoteText);
            harness.Selection.HasMore = false;
            Assert.Equal("已選全部 5 筆", harness.Bar.CountText);
            Assert.Equal("", harness.Bar.NoteText);

            // 取消任何一列就不再是全部，全選又可以按。
            harness.Press(Key.Space, 0);
            Assert.False(harness.Selection.IsAllMatching);
            Assert.Equal("已選 4 筆", harness.Bar.CountText);
            Assert.True(harness.Bar.IsSelectAllEnabled);
        });
    }

    [Fact]
    public void ProgressTakesTheCountSlotAndOffersCancelInTheSameRow()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(3);
            harness.Press(Key.Space, 0);
            harness.Bar.Slot.UpdateLayout();
            var height = harness.Bar.Slot.ActualHeight;
            var cancelled = false;

            harness.Bar.ShowProgress("正在讀取，已讀 200 筆…", () => cancelled = true);
            harness.Bar.Slot.UpdateLayout();
            Assert.Equal("正在讀取，已讀 200 筆…", harness.Bar.CountText);
            Assert.False(harness.Bar.IsSelectAllEnabled);
            var cancel = Descendants<Button>(harness.Bar.Slot).Single(button => "取消".Equals(button.Content));
            Assert.Equal(Visibility.Visible, cancel.Visibility);
            // 進度與取消都在工具列那一格裡，不另起一列把清單往下推。
            Assert.Equal(height, harness.Bar.Slot.ActualHeight, 1);

            cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(cancelled);
            harness.Bar.ClearProgress();
            Assert.Equal("已選 1 筆", harness.Bar.CountText);
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
        });
    }

    [Fact]
    public void RefreshKeepsChecksByIdAndFilterChangeClearsThem()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(4);
            harness.Press(Key.Space, 1); harness.Press(Key.Space, 3);
            var kept = harness.Rows[1].Id;
            var removed = harness.Rows[3].Id;

            // 重新整理：同一批識別換成新的列物件，勾選照識別補回；不在第一頁的那一筆拿掉。
            var reloaded = harness.Rows.Take(3).Select(row => new SqlMemoryRow(row.History!)).ToArray();
            harness.Rows.Clear();
            foreach (var row in reloaded) harness.Rows.Add(row);
            harness.Selection.RetainLoaded();
            Assert.True(harness.Selection.IsChecked(kept));
            Assert.False(harness.Selection.IsChecked(removed));
            Assert.Equal(new[] { false, true, false }, harness.Checked);
            Assert.Equal("已選 1 筆", harness.Bar.CountText);

            // 單筆刪除成功：移出勾選，最後一筆移出就離開模式。
            harness.Selection.Remove(kept);
            Assert.False(harness.Selection.IsActive);
            Assert.False(harness.Bar.IsShown);

            // 篩選或分頁切換由宿主呼叫 Clear：旗標與模式一起清掉。
            harness.Press(Key.Space, 0);
            harness.Selection.Clear();
            Assert.All(harness.Checked, value => Assert.False(value));
            Assert.False(harness.Selection.IsActive);
        });
    }

    [Fact]
    public void RecycledContainersShowTheirOwnRowsCheckState()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(300, height: 240);
            harness.Press(Key.Space, 0);
            harness.Press(Key.Space, 150);
            var realized = RealizedContainers(harness.List).Count;
            Assert.True(realized < 60, $"虛擬化失效：產生了 {realized} 個容器");

            // 全選只改旗標，不逐一產生容器。
            harness.Selection.SelectAll();
            Assert.True(RealizedContainers(harness.List).Count < 60);
            harness.Selection.Clear();
            harness.Press(Key.Space, 150); harness.Press(Key.Space, 2);

            foreach (var index in new[] { 0, 299, 1, 151, 150 })
            {
                harness.Container(index);
                foreach (var container in RealizedContainers(harness.List))
                {
                    if (container.DataContext is not SqlMemoryRow row) continue;
                    var check = Descendants<SqlRowCheckBox>(container).Single();
                    Assert.Equal(row.IsChecked, check.IsChecked == true);
                    Assert.Equal(Visibility.Visible, check.Visibility);
                }
            }
        });
    }

    [Fact]
    public void CtrlCCopiesCheckedRowsInDisplayOrderOnlyInMode()
    {
        WpfTest.Run(() =>
        {
            using var harness = new Harness(4);
            // 不在多選模式：Ctrl+C 不攔，原本的單筆行為不變。
            Assert.False(harness.Press(Key.C, 0, ModifierKeys.Control));
            Assert.Null(harness.Copied);

            harness.Press(Key.Space, 3); harness.Press(Key.Space, 0); harness.Press(Key.Space, 2);
            Assert.True(harness.Press(Key.C, 0, ModifierKeys.Control));
            var lines = harness.Copied!.Tsv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("狀態\t名稱\t伺服器\t資料庫\t時間\t次數", lines[0]);
            Assert.Equal(new[] { "Loan000", "Loan002", "Loan003" }, lines.Skip(1).Select(line => line.Split('\t')[1]));
            Assert.Contains("<td>Loan000</td>", harness.Copied.Html);
            Assert.DoesNotContain("SELECT", harness.Copied.Tsv);

            // 同一個 DataObject 同時放 TSV 與 HTML，分兩次寫的話第二次會把第一次換掉。
            var data = SqlClipboard.CreateDataObject(harness.Copied);
            Assert.Equal(harness.Copied.Tsv, data.GetData(DataFormats.UnicodeText));
            Assert.Equal(harness.Copied.Html, data.GetData(DataFormats.Html));
        });
    }

    [Fact]
    public void ClipboardRetriesWhenLockedAndReportsWhenItStaysLocked()
    {
        WpfTest.Run(() =>
        {
            var data = new DataObject(DataFormats.UnicodeText, "a");
            var attempts = 0;
            var result = SqlClipboard.WriteAsync(data, _ =>
            {
                if (++attempts < SqlClipboard.Attempts) throw new COMException("CLIPBRD_E_CANT_OPEN", unchecked((int)0x800401D0));
            }).GetAwaiter().GetResult();
            Assert.Null(result);
            Assert.Equal(SqlClipboard.Attempts, attempts);

            attempts = 0;
            result = SqlClipboard.WriteAsync(data, _ => { attempts++; throw new COMException("locked"); }).GetAwaiter().GetResult();
            Assert.Equal(SqlClipboard.BusyMessage, result);
            Assert.Equal(SqlClipboard.Attempts, attempts);
        });
    }

    [Fact]
    public void ListsWithoutSelectionNeverShowTheCheckColumn()
    {
        WpfTest.Run(() =>
        {
            // 兩份樣板都有勾選欄；沒呼叫 EnableSelection 的清單不開，勾選欄永遠不出現。
            Assert.NotNull(FindName(SqlAssistChrome.CreateSearchHitTemplate(motion: false), SqlAssistChrome.RowCheckName));
            Assert.NotNull(FindName(SqlAssistChrome.CreateSqlSummaryTemplate(motion: false), SqlAssistChrome.RowCheckName));
            var search = new SqlSearchList();
            Assert.Null(search.Selection);
            Assert.False(SqlRowCheck.GetIsAvailable(search));
            Assert.Null(new SqlMemoryList().Selection);
        });
    }

    [Fact]
    public void SearchCtrlCCopiesTheFocusedNameOutsideModeAndCheckedRowsInside()
    {
        WpfTest.Run(() =>
        {
            var rows = new ObservableCollection<SqlSearchRow>
            {
                SearchRow("[dbo].[Loan]", "Table", "LibraryServer", "Library"),
                SearchRow("[dbo].[LoanDetail]", "Table", null, null),
                SearchRow("[dbo].[usp_Loan]", "Procedure", "LibraryServer", "Library"),
            };
            var selection = new SqlCardSelection<SqlSearchRow, string>(rows, row => row.Key, StringComparer.Ordinal);
            SqlTabularContent? copied = null;
            selection.AddAction(new SqlSelectionAction(SqlIcon.Copy, "複製", () =>
            {
                copied = SqlTabularText.Build(SqlSearchRow.CopyColumns, selection.CheckedRows());
                return Task.FromResult(true);
            }, shortcutKey: Key.C, shortcutModifiers: ModifierKeys.Control));
            var list = new SqlSearchList();
            list.SetRowsSource(rows);
            list.EnableSelection(selection);
            var single = new List<SqlSearchRowAction>();
            list.RowActionRequested += single.Add;
            using var source = new HwndSource(new HwndSourceParameters("search selection") { Width = 440, Height = 300, WindowStyle = 0 })
                { RootVisual = list };
            list.Measure(new Size(440, 300)); list.Arrange(new Rect(0, 0, 440, 300)); list.UpdateLayout();

            bool Press(Key key, int index, ModifierKeys modifiers = ModifierKeys.None)
            {
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                var args = new KeyEventArgs(new TestKeyboardDevice(modifiers), source, 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = container };
                list.RaiseEvent(args);
                return args.Handled;
            }

            // 不在多選模式：Ctrl+C 仍是複製這一筆的限定名稱。
            Assert.True(Press(Key.C, 0, ModifierKeys.Control));
            Assert.Equal(new[] { SqlSearchRowAction.Copy }, single.ToArray());
            Assert.Null(copied);

            Assert.True(Press(Key.Space, 2));
            Assert.True(Press(Key.Space, 1));
            var check = Descendants<SqlRowCheckBox>((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)).Single();
            Assert.Equal(Visibility.Visible, check.Visibility);

            // 多選模式：Ctrl+C 讓給選取的複製，照清單順序，只有勾起來的那幾筆。
            Assert.True(Press(Key.C, 0, ModifierKeys.Control));
            Assert.Single(single);
            var lines = copied!.Tsv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("名稱\t種類\t伺服器\t資料庫\t命中部位\t命中資料行", lines[0]);
            Assert.Equal("[dbo].[LoanDetail]\tTable\t\t\t名稱\t", lines[1]);
            Assert.StartsWith("[dbo].[usp_Loan]\tProcedure\tLibraryServer\tLibrary\t", lines[2]);
            Assert.Equal(3, lines.Length);

            // 重排或重跑同一組條件：新的列物件照鍵補回勾選，不在新結果裡的去掉。
            var reloaded = new[] { rows[2], rows[0] }.Select(row => new SqlSearchRow(row.Hit, row.CategoryLabel)).ToArray();
            rows.Clear();
            foreach (var row in reloaded) rows.Add(row);
            selection.RetainLoaded();
            Assert.Equal(new[] { true, false }, rows.Select(row => row.IsChecked).ToArray());
            Assert.Equal(1, selection.Count);
            source.RootVisual = null;
        });
    }

    private static SqlSearchRow SearchRow(string title, string category, string? server, string? database)
    {
        var badges = new List<SearchBadge>();
        if (server is not null) badges.Add(new SearchBadge(server, SearchBadge.ServerIcon));
        if (database is not null) badges.Add(new SearchBadge(database, SearchBadge.DatabaseIcon));
        var hit = new SearchHit("catalog", "catalog." + category.ToLowerInvariant(), SearchMatchTarget.Name, title,
            "catalog:" + title, 10, snippet: title, badges: badges);
        return new SqlSearchRow(hit, category);
    }

    private static void ClickSelectAll(SqlSelectionBar bar) =>
        Descendants<Button>(bar.Slot).Single(button => "全選".Equals(button.Content))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static object? FindName(DataTemplate template, string name)
    {
        var host = new ContentControl { ContentTemplate = template, Content = Row("Loan") };
        host.Measure(new Size(400, 100)); host.Arrange(new Rect(0, 0, 400, 100)); host.UpdateLayout();
        var presenter = Descendants<ContentPresenter>(host).First();
        return template.FindName(name, presenter);
    }

    private static List<ListBoxItem> RealizedContainers(ListBox list) =>
        Descendants<ListBoxItem>(list).Where(item => item is not SqlCardListFooter).ToList();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
