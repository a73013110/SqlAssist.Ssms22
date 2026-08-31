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
using SqlAssist.Core.Keywords;
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
    private string _id = SqlSnippetIdentity.NewCustomId();
    private string _shortcut = string.Empty;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _code = string.Empty;
    private bool _triggerFollowUp;
    private SqlSnippetCategory _category = SqlSnippetCategory.Other;
    private bool _isDestructive;
    private SqlSnippetExpansionMode _expansionMode = SqlSnippetExpansionMode.TabStops;
    private SqlKeywordPosition _positions = SqlKeywordPosition.Any;
    private bool _isCustomized;
    private bool _isDisabled;
    private bool _isShadowed;

    public SnippetDraft()
    {
    }

    public SnippetDraft(SqlSnippet snippet)
    {
        _id = string.IsNullOrWhiteSpace(snippet.Id) ? SqlSnippetIdentity.NewCustomId() : snippet.Id;
        _shortcut = snippet.Shortcut;
        _title = snippet.Title;
        _description = snippet.Description;
        _code = snippet.Code;
        _triggerFollowUp = snippet.TriggerFollowUp;
        _category = snippet.Category;
        _isDestructive = snippet.IsDestructive;
        _expansionMode = snippet.ExpansionMode;
        _positions = snippet.Positions;

        foreach (var placeholder in snippet.Placeholders)
        {
            Placeholders.Add(new PlaceholderDraft(placeholder));
        }
    }

    public SnippetDraft(SqlSnippetConfigurationEntry entry) : this(entry.Snippet)
    {
        IsBuiltIn = entry.IsBuiltIn;
        _isCustomized = entry.IsCustomized;
        _isDisabled = entry.IsDisabled;
        _isShadowed = entry.IsShadowed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _id;

    public bool IsBuiltIn { get; private set; }

    public bool IsCustomized
    {
        get => _isCustomized;
        private set
        {
            if (Set(ref _isCustomized, value))
            {
                Notify(nameof(Caption));
            }
        }
    }

    public bool IsDisabled
    {
        get => _isDisabled;
        set
        {
            if (Set(ref _isDisabled, value))
            {
                if (IsBuiltIn)
                {
                    IsCustomized = true;
                }

                Notify(nameof(Caption));
            }
        }
    }

    /// <summary>
    /// 捷徑被另一筆優先的項目佔走。
    /// </summary>
    /// <remarks>
    /// 只是這一輪的計算結果，不是使用者的決定，因此不能寫成停用紀錄——
    /// 改掉撞名的那一筆之後這一筆就會自己回來。改了捷徑就當場解除，
    /// 使用者才看得出「這樣改就對了」。
    /// </remarks>
    public bool IsShadowed
    {
        get => _isShadowed;
        private set
        {
            if (Set(ref _isShadowed, value))
            {
                Notify(nameof(Caption));
            }
        }
    }

    public string Shortcut
    {
        get => _shortcut;
        set
        {
            if (Set(ref _shortcut, value))
            {
                // 改了捷徑就不再撞名，遮住的狀態當場解除。
                IsShadowed = false;

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

    public SqlSnippetCategory Category
    {
        get => _category;
        set
        {
            if (Set(ref _category, value))
            {
                Notify(nameof(Caption));
            }
        }
    }

    public bool IsDestructive
    {
        get => _isDestructive;
        set => Set(ref _isDestructive, value);
    }

    public SqlSnippetExpansionMode ExpansionMode
    {
        get => _expansionMode;
        set => Set(ref _expansionMode, value);
    }

    public SqlKeywordPosition Positions
    {
        get => _positions;
        set => Set(ref _positions, value);
    }

    public ObservableCollection<PlaceholderDraft> Placeholders { get; } = new();

    public string Caption
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Title) ||
                          string.Equals(Title, Shortcut, StringComparison.Ordinal)
                ? Shortcut
                : $"{Shortcut} — {Title}";
            var caption = $"[{CategoryLabel(Category)}] {title}";

            if (IsDisabled)
            {
                return caption + "（已停用）";
            }

            if (IsShadowed)
            {
                return caption + "（捷徑被其他片段佔用）";
            }

            return IsBuiltIn && IsCustomized ? caption + "（已自訂）" : caption;
        }
    }

    private static string CategoryLabel(SqlSnippetCategory category)
    {
        return category switch
        {
            SqlSnippetCategory.Select => "SELECT",
            SqlSnippetCategory.Dml => "DML",
            SqlSnippetCategory.Ddl => "DDL",
            SqlSnippetCategory.ControlFlow => "流程",
            SqlSnippetCategory.Clause => "子句",
            _ => "其他"
        };
    }

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
            Placeholders.Select(item => item.ToPlaceholder()).ToArray(),
            Id,
            Category,
            IsDestructive,
            ExpansionMode,
            Positions);
    }

    public void Restore(SqlSnippet definition)
    {
        _id = definition.Id;
        Shortcut = definition.Shortcut;
        Title = definition.Title;
        Description = definition.Description;
        Code = definition.Code;
        TriggerFollowUp = definition.TriggerFollowUp;
        Category = definition.Category;
        IsDestructive = definition.IsDestructive;
        ExpansionMode = definition.ExpansionMode;
        Positions = definition.Positions;
        Placeholders.Clear();

        foreach (var placeholder in definition.Placeholders)
        {
            Placeholders.Add(new PlaceholderDraft(placeholder));
        }

        IsDisabled = false;
        IsCustomized = false;
        Notify(nameof(Caption));
    }

    public void MakeCustomCopy()
    {
        _id = SqlSnippetIdentity.NewCustomId();
        IsBuiltIn = false;
        IsCustomized = false;
        IsDisabled = false;
        Notify(nameof(Caption));
    }

    public void RefreshCustomization(SqlSnippet definition)
    {
        if (IsBuiltIn)
        {
            IsCustomized = IsDisabled || !SqlSnippetMerger.AreEquivalent(ToSnippet(), definition);
        }
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

internal sealed class Choice<T>
{
    public Choice(T value, string label)
    {
        Value = value;
        Label = label;
    }

    public T Value { get; }

    public string Label { get; }
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
    private readonly ComboBox _categoryBox;
    private readonly ComboBox _expansionModeBox;
    private readonly CheckBox _destructiveBox;
    private readonly CheckBox _followUpBox;
    private readonly DataGrid _placeholderGrid;
    private readonly TextBlock _statusText;
    private readonly StackPanel _editor;
    private readonly Button _restoreSelectedButton;
    private readonly Button _saveButton;
    private readonly bool _isReadOnly;

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

        var configuration = SqlSnippetStore.Configuration;
        _isReadOnly = SqlSnippetStore.IsReadOnly;

        foreach (var entry in configuration.Entries)
        {
            _drafts.Add(new SnippetDraft(entry));
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

        _categoryBox = CreateChoiceBox(
            new[]
            {
                new Choice<SqlSnippetCategory>(SqlSnippetCategory.Select, "SELECT"),
                new Choice<SqlSnippetCategory>(SqlSnippetCategory.Dml, "DML"),
                new Choice<SqlSnippetCategory>(SqlSnippetCategory.Ddl, "DDL"),
                new Choice<SqlSnippetCategory>(SqlSnippetCategory.ControlFlow, "流程控制／交易"),
                new Choice<SqlSnippetCategory>(SqlSnippetCategory.Clause, "查詢子句／其他"),
                new Choice<SqlSnippetCategory>(SqlSnippetCategory.Other, "其他")
            });
        _expansionModeBox = CreateChoiceBox(
            new[]
            {
                new Choice<SqlSnippetExpansionMode>(SqlSnippetExpansionMode.TabStops, "依序按 Tab 跳轉"),
                new Choice<SqlSnippetExpansionMode>(SqlSnippetExpansionMode.Caret, "只移動游標")
            });

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
        _expansionModeBox.SelectionChanged += (_, _) => UpdateFollowUpAvailability();

        _destructiveBox = new CheckBox
        {
            Content = "危險操作（無輸入前綴時隱藏）",
            Foreground = VsThemeBrushes.ListForeground,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = default,
            Template = SqlAssistChrome.CreateCheckBoxTemplate()
        };

        _placeholderGrid = CreatePlaceholderGrid();

        _statusText = SqlAssistChrome.CreateStatusText(Metrics);
        _statusText.Margin = new Thickness(0, 0, 12, 0);

        _editor = BuildEditor();
        _restoreSelectedButton = CreateButton("還原此預設", OnRestoreSelected);
        _saveButton = CreateButton("儲存", OnSave, primary: true);
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

        if (_isReadOnly)
        {
            _saveButton.IsEnabled = false;
        }
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
        var grid = SqlAssistChrome.CreateDataGrid(Metrics, Brushes.Transparent);
        grid.MinHeight = 110;
        grid.MaxHeight = 200;

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

        panel.Children.Add(SqlAssistChrome.CreateLabel("分類", Metrics));
        panel.Children.Add(_categoryBox);

        panel.Children.Add(SqlAssistChrome.CreateLabel("展開模式", Metrics));
        panel.Children.Add(_expansionModeBox);

        panel.Children.Add(SqlAssistChrome.CreateLabel("程式碼", Metrics));
        panel.Children.Add(_codeBox);
        panel.Children.Add(SqlAssistChrome.CreateHint(
            "以 $名稱$ 標示佔位符，展開時會換成下面設定的預設值；" +
            "以 $end$ 標示展開後游標要停的位置。", Metrics));

        panel.Children.Add(_destructiveBox);

        panel.Children.Add(_followUpBox);
        panel.Children.Add(SqlAssistChrome.CreateHint(
            "只適用於「只移動游標」模式；接續內容由展開後的 SQL 上下文決定。",
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
        actions.Children.Add(_restoreSelectedButton);
        actions.Children.Add(CreateButton("還原預設", OnRestoreDefaults));

        var confirm = new StackPanel { Orientation = Orientation.Horizontal };

        // 整個視窗只有這一顆按鈕帶底色；主要動作只能有一個，多給一個就沒有主要。
        _saveButton.IsDefault = true;
        confirm.Children.Add(_saveButton);

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
        var button = SqlAssistChrome.CreateButton(text, Metrics, primary);

        // 對話框底部那一排要對齊，最窄的按鈕也不能比「取消」窄。
        button.MinWidth = 78;
        button.Margin = new Thickness(0, 0, 6, 0);
        button.Click += handler;
        return button;
    }

    private static ComboBox CreateChoiceBox<T>(IEnumerable<Choice<T>> choices)
    {
        var box = SqlAssistChrome.CreateComboBox(Metrics);
        box.ItemsSource = choices;
        box.DisplayMemberPath = nameof(Choice<T>.Label);
        box.SelectedValuePath = nameof(Choice<T>.Value);
        return box;
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
        _categoryBox.SelectedValue = draft?.Category ?? SqlSnippetCategory.Other;
        _expansionModeBox.SelectedValue = draft?.ExpansionMode ?? SqlSnippetExpansionMode.Caret;
        _destructiveBox.IsChecked = draft?.IsDestructive ?? false;
        _followUpBox.IsChecked = draft?.TriggerFollowUp ?? false;
        _placeholderGrid.ItemsSource = draft?.Placeholders;
        UpdateFollowUpAvailability();
    }

    private void PullFromEditor(SnippetDraft? draft)
    {
        if (draft is null || draft.IsDisabled)
        {
            return;
        }

        // 格子還在編輯狀態時，繫結的值尚未寫回來源物件。
        _placeholderGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        draft.Shortcut = _shortcutBox.Text;
        draft.Title = _titleBox.Text;
        draft.Description = _descriptionBox.Text;
        draft.Code = _codeBox.Text;
        draft.Category = _categoryBox.SelectedValue is SqlSnippetCategory category
            ? category
            : SqlSnippetCategory.Other;
        draft.ExpansionMode = _expansionModeBox.SelectedValue is SqlSnippetExpansionMode mode
            ? mode
            : SqlSnippetExpansionMode.Caret;
        draft.IsDestructive = _destructiveBox.IsChecked == true;
        draft.TriggerFollowUp = _followUpBox.IsChecked == true;

        if (draft.IsBuiltIn && SqlSnippetDefaults.Current.TryGetById(draft.Id, out var definition))
        {
            draft.RefreshCustomization(definition);
        }
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
        var enabled = !_isReadOnly && Selected is { IsDisabled: false };
        _editor.IsEnabled = enabled;
        _editor.Opacity = enabled ? 1.0 : 0.4;
        _restoreSelectedButton.IsEnabled = !_isReadOnly && Selected is
        {
            IsBuiltIn: true,
            IsCustomized: true
        };
    }

    private void OnAdd(object sender, RoutedEventArgs eventArgs)
    {
        if (RejectReadOnly())
        {
            return;
        }

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
        if (RejectReadOnly())
        {
            return;
        }

        PullFromEditor(Selected);

        if (Selected is not { } source)
        {
            return;
        }

        var copy = new SnippetDraft(source.ToSnippet())
        {
            Shortcut = NextShortcut(source.Shortcut)
        };
        copy.MakeCustomCopy();

        _drafts.Add(copy);
        _list.SelectedItem = copy;
        _shortcutBox.Focus();
        _shortcutBox.SelectAll();
    }

    private void OnDelete(object sender, RoutedEventArgs eventArgs)
    {
        if (RejectReadOnly())
        {
            return;
        }

        if (Selected is not { } draft)
        {
            return;
        }

        var action = draft.IsBuiltIn ? "停用" : "刪除";
        var confirmed = MessageBox.Show(
            this,
            $"要{action}「{draft.Caption}」嗎？",
            "SqlAssist",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        if (draft.IsBuiltIn)
        {
            draft.IsDisabled = true;
            PushToEditor(draft);
            UpdateEditorEnabled();
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
        if (RejectReadOnly())
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            "要還原全部 43 筆內建片段並移除自訂片段嗎？按「儲存」之後才會寫回檔案。",
            "SqlAssist",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        _drafts.Clear();

        foreach (var snippet in SqlSnippetDefaults.Current.Snippets)
        {
            _drafts.Add(new SnippetDraft(new SqlSnippetConfigurationEntry(
                snippet,
                isBuiltIn: true,
                isCustomized: false,
                isDisabled: false)));
        }

        _list.SelectedIndex = 0;
        UpdateEditorEnabled();
    }

    private void OnRestoreSelected(object sender, RoutedEventArgs eventArgs)
    {
        if (RejectReadOnly())
        {
            return;
        }

        PullFromEditor(Selected);

        if (Selected is not { IsBuiltIn: true } draft ||
            !SqlSnippetDefaults.Current.TryGetById(draft.Id, out var definition))
        {
            return;
        }

        draft.Restore(definition);
        PushToEditor(draft);
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
            // 不走 SqlAssistPlatformGuard：使用者按了按鈕卻什麼都沒開，
            // 沒有訊息的話只會被當成按鈕壞了。
            SqlAssistDiagnostics.WriteAlways($"開啟 Snippet 檔案位置失敗：{exception}");
            _statusText.Text = $"開啟檔案位置失敗：{exception.Message}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs eventArgs)
    {
        if (RejectReadOnly())
        {
            return;
        }

        PullFromEditor(Selected);

        if (!TryBuildEntries(out var entries, out var invalid, out var error))
        {
            if (invalid is not null)
            {
                _list.SelectedItem = invalid;
                _shortcutBox.Focus();
            }

            _statusText.Text = error;
            return;
        }

        if (!SqlSnippetStore.Save(entries!))
        {
            _statusText.Text = $"儲存失敗：{SqlSnippetStore.LastError}";
            return;
        }

        SqlAssistDiagnostics.WriteAlways(
            $"Snippet 已更新，共 {SqlSnippetStore.Current.Count} 筆生效");
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 把清單裡的每一筆轉成要寫回檔案的項目。
    /// </summary>
    /// <remarks>
    /// 停用的項目也要交出去：內建片段的停用是使用者的決定，必須寫成停用紀錄，
    /// 否則下一次載入又會回來。被遮住的同樣要交出去——那是計算結果不是決定，
    /// 漏掉它就等於在檔案裡把它刪掉。
    /// </remarks>
    private bool TryBuildEntries(
        out IReadOnlyList<SqlSnippetConfigurationEntry>? entries,
        out SnippetDraft? invalid,
        out string error)
    {
        entries = null;
        var result = new List<SqlSnippetConfigurationEntry>(_drafts.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var draft in _drafts)
        {
            var shortcut = draft.Shortcut?.Trim() ?? string.Empty;

            if (!draft.IsDisabled)
            {
                // 逐筆累加而不是最後一次檢查：撞名要指得出是哪一筆，
                // 而「已經收進去的那些」正好就是判斷撞名的依據。
                //
                // 被遮住的項目跳過這一關：它的撞名是手改檔案帶進來的，
                // 擋在這裡只會讓使用者連別的欄位都存不回去。合併時仍會
                // 挑出同一個贏家，改掉捷徑就自己解除。
                if (!draft.IsShadowed && !ValidateShortcut(shortcut, taken, out error))
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

                taken.Add(shortcut);
            }

            result.Add(new SqlSnippetConfigurationEntry(
                draft.ToSnippet(),
                draft.IsBuiltIn,
                draft.IsCustomized,
                draft.IsDisabled,
                draft.IsShadowed));
        }

        entries = result;
        invalid = null;
        error = string.Empty;
        return true;
    }

    /// <summary>捷徑的格式與唯一性；格式那一條沿用模型的判斷。</summary>
    private static bool ValidateShortcut(string shortcut, ICollection<string> taken, out string error)
    {
        if (!SqlSnippetLibrary.Empty.ValidateShortcut(shortcut, null, out error))
        {
            return false;
        }

        if (taken.Contains(shortcut))
        {
            error = $"捷徑「{shortcut}」已經有人用了。";
            return false;
        }

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
            // 檔案讀壞時畫面仍列內建值，但使用者資料沒有套上；必須講清楚並保持唯讀，
            // 否則看起來像「自訂項目被刪光」，再存一次就真的覆蓋原檔。
            _statusText.Text = $"讀取檔案時發生問題：{error}";
        }
    }

    private void UpdateFollowUpAvailability()
    {
        var enabled = _expansionModeBox.SelectedValue is SqlSnippetExpansionMode.Caret;
        _followUpBox.IsEnabled = enabled;

        if (!enabled)
        {
            _followUpBox.IsChecked = false;
        }
    }

    private bool RejectReadOnly()
    {
        if (!_isReadOnly)
        {
            return false;
        }

        _statusText.Text = SqlSnippetStore.LastError ?? "Snippet 檔案目前是唯讀狀態。";
        return true;
    }
}
