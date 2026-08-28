using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>
/// 編輯中的一筆 Snippet。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlSnippet"/> 分開：模型是不可變的，而編輯途中的內容本來就是
/// 半成品——捷徑可能暫時空白、程式碼可能只打了一半。把兩者混在一起會逼著
/// 模型接受無效狀態。
/// </remarks>
internal sealed class SnippetDraft : INotifyPropertyChanged
{
    private string _shortcut = string.Empty;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _code = string.Empty;
    private bool _triggerFollowUp;

    public SnippetDraft()
    {
    }

    public SnippetDraft(SqlSnippet snippet)
    {
        _shortcut = snippet.Shortcut;
        _title = snippet.Title;
        _description = snippet.Description;
        _code = snippet.Code;
        _triggerFollowUp = snippet.TriggerFollowUp;

        foreach (var placeholder in snippet.Placeholders)
        {
            Placeholders.Add(new PlaceholderDraft(placeholder));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Shortcut
    {
        get => _shortcut;
        set
        {
            if (Set(ref _shortcut, value))
            {
                // 清單左欄顯示的是「捷徑 — 標題」，改捷徑要即時反映。
                Notify(nameof(Caption));
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value))
            {
                Notify(nameof(Caption));
            }
        }
    }

    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    public string Code
    {
        get => _code;
        set
        {
            if (Set(ref _code, value))
            {
                SyncPlaceholders();
            }
        }
    }

    public bool TriggerFollowUp
    {
        get => _triggerFollowUp;
        set => Set(ref _triggerFollowUp, value);
    }

    public ObservableCollection<PlaceholderDraft> Placeholders { get; } = new();

    public string Caption =>
        string.IsNullOrWhiteSpace(Title) || string.Equals(Title, Shortcut, StringComparison.Ordinal)
            ? Shortcut
            : $"{Shortcut} — {Title}";

    /// <summary>依程式碼重算佔位符，保留已經設定好的預設值與說明。</summary>
    public void SyncPlaceholders()
    {
        var reconciled = SqlSnippetPlaceholders.Reconcile(
            Code,
            Placeholders.Select(item => item.ToPlaceholder()).ToArray());

        Placeholders.Clear();

        foreach (var placeholder in reconciled)
        {
            Placeholders.Add(new PlaceholderDraft(placeholder));
        }
    }

    public SqlSnippet ToSnippet()
    {
        return new SqlSnippet(
            Shortcut.Trim(),
            Code,
            Title.Trim(),
            Description.Trim(),
            TriggerFollowUp,
            Placeholders.Select(item => item.ToPlaceholder()).ToArray());
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>編輯中的一個佔位符。名稱由程式碼決定，只有預設值與說明可以改。</summary>
internal sealed class PlaceholderDraft
{
    public PlaceholderDraft(SqlSnippetPlaceholder placeholder)
    {
        Id = placeholder.Id;
        DefaultValue = placeholder.DefaultValue;
        ToolTip = placeholder.ToolTip;
    }

    public string Id { get; }

    public string DefaultValue { get; set; }

    public string ToolTip { get; set; }

    public SqlSnippetPlaceholder ToPlaceholder() => new(Id, DefaultValue ?? string.Empty, ToolTip ?? string.Empty);
}

/// <summary>
/// Snippet 管理員。
/// </summary>
/// <remarks>
/// 不放進 Unified Settings：那裡只收 boolean、integer、enum 與 string，
/// 一份可增刪的清單塞不進去，所以獨立成一個對話框，由「工具 → SqlAssist」進入。
///
/// 介面以程式碼建構而不是 XAML，與擴充內其他 WPF 介面（浮動預覽）一致：
/// VSIX 專案裡多一組 XAML 就多一組 BAML 資源與建置設定，對這種規模的視窗
/// 划不來。
/// </remarks>
internal sealed class SqlSnippetManagerWindow : DialogWindow
{
    private readonly ObservableCollection<SnippetDraft> _drafts = new();
    private readonly ListBox _list;
    private readonly TextBox _shortcutBox;
    private readonly TextBox _titleBox;
    private readonly TextBox _descriptionBox;
    private readonly TextBox _codeBox;
    private readonly CheckBox _followUpBox;
    private readonly DataGrid _placeholderGrid;
    private readonly TextBlock _statusText;
    private readonly StackPanel _editor;

    /// <summary>
    /// 字級與行高。
    /// </summary>
    /// <remarks>
    /// 與浮動預覽同一套推導，只是基準值固定：預覽的基準值是設定項，
    /// 因為它貼在程式碼旁邊要跟編輯器的字級一起讀，對話框沒有這個問題。
    /// </remarks>
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    public SqlSnippetManagerWindow()
    {
        Title = "SqlAssist — 程式碼片段";
        Width = 940;
        Height = 700;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = VsThemeBrushes.WindowBackground;
        Foreground = VsThemeBrushes.WindowForeground;
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = Metrics.Body;

        // 版面計算的模式交給排版而不是像素對齊：字距在小字級下才不會忽寬忽窄。
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        foreach (var snippet in SqlSnippetStore.Current.Snippets)
        {
            _drafts.Add(new SnippetDraft(snippet));
        }

        _list = new ListBox
        {
            ItemsSource = _drafts,
            DisplayMemberPath = nameof(SnippetDraft.Caption),

            // 底色與外框由外面那一層 Surface 負責，清單自己不再畫一次。
            Background = Brushes.Transparent,
            BorderThickness = default,
            Padding = new Thickness(4),
            ItemContainerStyle = SqlAssistChrome.CreateListItemStyle(Metrics)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += OnSelectionChanged;

        _shortcutBox = SqlAssistChrome.CreateTextBox(Metrics);
        _titleBox = SqlAssistChrome.CreateTextBox(Metrics);
        _descriptionBox = SqlAssistChrome.CreateTextBox(Metrics);

        _codeBox = SqlAssistChrome.CreateTextBox(Metrics);
        _codeBox.AcceptsReturn = true;
        _codeBox.AcceptsTab = true;
        _codeBox.TextWrapping = TextWrapping.NoWrap;
        _codeBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _codeBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _codeBox.FontFamily = SqlAssistChrome.CodeFont;
        _codeBox.MinHeight = 120;

        _followUpBox = new CheckBox
        {
            Content = "展開後立刻再顯示一次建議清單",
            Foreground = VsThemeBrushes.ListForeground,
            Margin = new Thickness(0, 16, 0, 0),
            Padding = default,
            Template = SqlAssistChrome.CreateCheckBoxTemplate()
        };

        _placeholderGrid = CreatePlaceholderGrid();

        _statusText = new TextBlock
        {
            FontSize = Metrics.Caption,
            Foreground = VsThemeBrushes.DimForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        _editor = BuildEditor();
        Content = BuildLayout();

        if (_drafts.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
        else
        {
            UpdateEditorEnabled();
        }

        ReportStoreError();
    }

    private SnippetDraft? Selected => _list.SelectedItem as SnippetDraft;

    /// <summary>
    /// 佔位符表。
    /// </summary>
    /// <remarks>
    /// 樣式與浮動預覽的欄位表同一套：不畫格線、交替底色分列、
    /// 欄位標題只有下緣一條細線。一百多列的欄位表需要安靜，
    /// 三、四列的佔位符表更沒有理由吵。
    /// </remarks>
    private DataGrid CreatePlaceholderGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserResizeRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brushes.Transparent,
            Foreground = VsThemeBrushes.ListForeground,
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = VsThemeBrushes.RowAlternate,
            AlternationCount = 2,
            BorderThickness = default,
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = Metrics.Body,
            RowHeight = Metrics.RowHeight,
            ColumnHeaderStyle = SqlAssistChrome.CreateColumnHeaderStyle(Metrics),
            CellStyle = SqlAssistChrome.CreateCellStyle(),
            MinHeight = 110,
            MaxHeight = 200
        };

        var cellText = SqlAssistChrome.CreateCellTextStyle();
        var cellEditor = SqlAssistChrome.CreateCellEditorStyle();

        // 名稱唯讀：它是從程式碼裡的 $名稱$ 推導出來的，在這裡改只會與程式碼分岔。
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "佔位符",
            Binding = new System.Windows.Data.Binding(nameof(PlaceholderDraft.Id)),
            IsReadOnly = true,
            ElementStyle = cellText,
            Width = new DataGridLength(140)
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "預設值",
            Binding = new System.Windows.Data.Binding(nameof(PlaceholderDraft.DefaultValue)),
            ElementStyle = cellText,
            EditingElementStyle = cellEditor,
            Width = new DataGridLength(200)
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "說明",
            Binding = new System.Windows.Data.Binding(nameof(PlaceholderDraft.ToolTip)),
            ElementStyle = cellText,
            EditingElementStyle = cellEditor,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        return grid;
    }

    private StackPanel BuildEditor()
    {
        var panel = new StackPanel();

        // 第一個標題不留上緣空白：它上面就是視窗邊，再留一次會歪掉。
        var shortcutLabel = SqlAssistChrome.CreateLabel("捷徑", Metrics);
        shortcutLabel.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(shortcutLabel);
        panel.Children.Add(_shortcutBox);
        panel.Children.Add(SqlAssistChrome.CreateHint(
            "在編輯器裡打這串字，就會在建議清單裡出現。只能用字母、數字與底線。", Metrics));

        panel.Children.Add(SqlAssistChrome.CreateLabel("標題", Metrics));
        panel.Children.Add(_titleBox);

        panel.Children.Add(SqlAssistChrome.CreateLabel("說明", Metrics));
        panel.Children.Add(_descriptionBox);

        panel.Children.Add(SqlAssistChrome.CreateLabel("程式碼", Metrics));
        panel.Children.Add(_codeBox);
        panel.Children.Add(SqlAssistChrome.CreateHint(
            "以 $名稱$ 標示佔位符，展開時會換成下面設定的預設值；" +
            "以 $end$ 標示展開後游標要停的位置。", Metrics));

        panel.Children.Add(_followUpBox);
        panel.Children.Add(SqlAssistChrome.CreateHint(
            "接續清單的內容由展開後的文字決定——程式碼結尾是 FROM 就只列資料表與檢視。",
            Metrics));

        panel.Children.Add(SqlAssistChrome.CreateLabel("佔位符", Metrics));

        var placeholders = SqlAssistChrome.CreateSurface(_placeholderGrid);
        placeholders.Padding = new Thickness(0, 0, 0, 4);
        panel.Children.Add(placeholders);

        return panel;
    }

    private Grid BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = new DockPanel();
        var listButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        listButtons.Children.Add(CreateButton("新增", OnAdd));
        listButtons.Children.Add(CreateButton("複製", OnDuplicate));
        listButtons.Children.Add(CreateButton("刪除", OnDelete));
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        left.Children.Add(SqlAssistChrome.CreateSurface(_list));

        Grid.SetRow(left, 0);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        var scroll = new ScrollViewer
        {
            Content = _editor,
            Margin = new Thickness(18, 0, 0, 0),

            // 捲軸出現時不要壓到欄位的右緣，先把它的寬度留出來。
            Padding = new Thickness(0, 0, 8, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 0);
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(CreateButton("開啟檔案位置", OnRevealFile));
        actions.Children.Add(CreateButton("還原預設", OnRestoreDefaults));

        var confirm = new StackPanel { Orientation = Orientation.Horizontal };

        // 整個視窗只有這一顆按鈕帶底色；主要動作只能有一個，多給一個就沒有主要。
        var save = CreateButton("儲存", OnSave, primary: true);
        save.IsDefault = true;
        confirm.Children.Add(save);

        var cancel = CreateButton("取消", (_, _) => Close());
        cancel.IsCancel = true;
        cancel.Margin = default;
        confirm.Children.Add(cancel);

        DockPanel.SetDock(actions, Dock.Left);
        DockPanel.SetDock(confirm, Dock.Right);
        footer.Children.Add(actions);
        footer.Children.Add(confirm);
        footer.Children.Add(_statusText);

        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static Button CreateButton(string text, RoutedEventHandler handler, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 78,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(12, 4, 12, 5),
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = Metrics.Body,
            Foreground = VsThemeBrushes.ListForeground,
            Template = primary
                ? SqlAssistChrome.CreatePrimaryButtonTemplate()
                : SqlAssistChrome.CreateGhostButtonTemplate()
        };

        button.Click += handler;
        return button;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        // 換選取之前先把畫面上的值收回上一筆，否則編輯到一半切走就沒了。
        foreach (var removed in eventArgs.RemovedItems.OfType<SnippetDraft>())
        {
            PullFromEditor(removed);
        }

        PushToEditor(Selected);
        UpdateEditorEnabled();
    }

    private void PushToEditor(SnippetDraft? draft)
    {
        _shortcutBox.Text = draft?.Shortcut ?? string.Empty;
        _titleBox.Text = draft?.Title ?? string.Empty;
        _descriptionBox.Text = draft?.Description ?? string.Empty;
        _codeBox.Text = draft?.Code ?? string.Empty;
        _followUpBox.IsChecked = draft?.TriggerFollowUp ?? false;
        _placeholderGrid.ItemsSource = draft?.Placeholders;
    }

    private void PullFromEditor(SnippetDraft? draft)
    {
        if (draft is null)
        {
            return;
        }

        // 格子還在編輯狀態時，繫結的值尚未寫回來源物件。
        _placeholderGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        draft.Shortcut = _shortcutBox.Text;
        draft.Title = _titleBox.Text;
        draft.Description = _descriptionBox.Text;
        draft.Code = _codeBox.Text;
        draft.TriggerFollowUp = _followUpBox.IsChecked == true;
    }

    /// <summary>
    /// 沒有選取任何一筆時把右半邊整個關掉。
    /// </summary>
    /// <remarks>
    /// 同時壓低透明度：關掉的控制項自己會變淡，但標題與說明是純文字，
    /// 不會有任何反應——只關掉不壓淡的結果是「一半的字看起來還是可以編輯」。
    /// </remarks>
    private void UpdateEditorEnabled()
    {
        var enabled = Selected is not null;
        _editor.IsEnabled = enabled;
        _editor.Opacity = enabled ? 1.0 : 0.4;
    }

    private void OnAdd(object sender, RoutedEventArgs eventArgs)
    {
        PullFromEditor(Selected);

        var draft = new SnippetDraft
        {
            Shortcut = NextShortcut(),
            Title = "新片段",
            Code = string.Empty
        };

        _drafts.Add(draft);
        _list.SelectedItem = draft;
        _shortcutBox.Focus();
        _shortcutBox.SelectAll();
    }

    private void OnDuplicate(object sender, RoutedEventArgs eventArgs)
    {
        PullFromEditor(Selected);

        if (Selected is not { } source)
        {
            return;
        }

        var copy = new SnippetDraft(source.ToSnippet())
        {
            Shortcut = NextShortcut(source.Shortcut)
        };

        _drafts.Add(copy);
        _list.SelectedItem = copy;
        _shortcutBox.Focus();
        _shortcutBox.SelectAll();
    }

    private void OnDelete(object sender, RoutedEventArgs eventArgs)
    {
        if (Selected is not { } draft)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"要刪除「{draft.Caption}」嗎？",
            "SqlAssist",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        var index = _drafts.IndexOf(draft);
        _drafts.Remove(draft);

        if (_drafts.Count > 0)
        {
            _list.SelectedIndex = Math.Min(index, _drafts.Count - 1);
        }
        else
        {
            PushToEditor(null);
            UpdateEditorEnabled();
        }
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs eventArgs)
    {
        var confirmed = MessageBox.Show(
            this,
            "要把清單換成內建的 ssf、ap、af 嗎？目前的內容會被取代，按「儲存」之後才會寫回檔案。",
            "SqlAssist",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        _drafts.Clear();

        foreach (var snippet in SqlSnippetLibrary.CreateDefault().Snippets)
        {
            _drafts.Add(new SnippetDraft(snippet));
        }

        _list.SelectedIndex = 0;
        UpdateEditorEnabled();
    }

    private void OnRevealFile(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var path = SqlSnippetStore.FilePath;

            // 檔案還沒建立時退而求其次開資料夾，總比彈一個「找不到」好。
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"開啟 Snippet 檔案位置失敗：{exception}");
            _statusText.Text = $"開啟檔案位置失敗：{exception.Message}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs eventArgs)
    {
        PullFromEditor(Selected);

        if (!TryBuildLibrary(out var library, out var invalid, out var error))
        {
            if (invalid is not null)
            {
                _list.SelectedItem = invalid;
                _shortcutBox.Focus();
            }

            _statusText.Text = error;
            return;
        }

        if (!SqlSnippetStore.Save(library!))
        {
            _statusText.Text = $"儲存失敗：{SqlSnippetStore.LastError}";
            return;
        }

        SqlAssistDiagnostics.WriteAlways($"Snippet 已更新，共 {library!.Count} 筆");
        DialogResult = true;
        Close();
    }

    private bool TryBuildLibrary(
        out SqlSnippetLibrary? library,
        out SnippetDraft? invalid,
        out string error)
    {
        library = null;
        var accumulated = SqlSnippetLibrary.Empty;

        foreach (var draft in _drafts)
        {
            // 逐筆累加而不是最後一次檢查：撞名要指得出是哪一筆，
            // 而「已經收進去的那些」正好就是判斷撞名的依據。
            if (!accumulated.ValidateShortcut(draft.Shortcut?.Trim(), null, out error))
            {
                invalid = draft;
                return false;
            }

            if (string.IsNullOrWhiteSpace(draft.Code))
            {
                invalid = draft;
                error = $"「{draft.Caption}」還沒有程式碼。";
                return false;
            }

            accumulated = accumulated.Set(draft.ToSnippet());
        }

        library = accumulated;
        invalid = null;
        error = string.Empty;
        return true;
    }

    private string NextShortcut(string? baseName = null)
    {
        var prefix = string.IsNullOrWhiteSpace(baseName) ? "new" : baseName!.Trim();
        var existing = new HashSet<string>(
            _drafts.Select(item => item.Shortcut ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(prefix))
        {
            return prefix;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return prefix;
    }

    private void ReportStoreError()
    {
        if (SqlSnippetStore.LastError is { } error)
        {
            // 檔案讀壞時清單是空的。這裡必須講清楚，否則使用者會以為
            // 自己的 Snippet 被刪光了，然後按下儲存把空清單寫回去。
            _statusText.Text = $"讀取檔案時發生問題：{error}";
        }
    }
}
