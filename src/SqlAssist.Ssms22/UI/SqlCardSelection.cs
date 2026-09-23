using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>可以被多選勾起來的清單列；勾選狀態是列資料上的旗標，不存在容器上。</summary>
/// <remarks>
/// 清單是 recycling 虛擬化，同一個容器捲動時會換給別的列：狀態放在容器上，捲回來那一列就換成
/// 別人的勾。實作者要在 <see cref="IsChecked"/> 改變時發 <c>PropertyChanged</c>，樣板只繫結它。
/// </remarks>
internal interface ISqlCheckableRow
{
    bool IsChecked { get; set; }
}

/// <summary>
/// 選取工具列上的一個動作：圖示、文字、可用條件與執行。
/// </summary>
/// <remarks>
/// 工具列只照這份描述畫按鈕，所以之後加一個「刪除」只是多一筆描述，工具列本身不動。
/// 執行回報成功與否由動作自己負責（寫到工具窗的狀態列）；它回傳的布林只決定按鈕要不要
/// 播一次「完成」回饋，而且<b>不得擲出例外</b>：工具列的點擊沒有人接得住。
/// </remarks>
internal sealed class SqlSelectionAction
{
    /// <param name="canExecute">null 表示有勾選就能做。</param>
    /// <param name="shortcutKey">多選模式中的快捷鍵；null 表示沒有。</param>
    public SqlSelectionAction(SqlIcon icon, string label, Func<Task<bool>> executeAsync,
        Func<bool>? canExecute = null, Key? shortcutKey = null, ModifierKeys shortcutModifiers = ModifierKeys.None)
    {
        Icon = icon;
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        ShortcutKey = shortcutKey;
        ShortcutModifiers = shortcutModifiers;
    }

    private readonly Func<bool>? _canExecute;

    public SqlIcon Icon { get; }

    public string Label { get; }

    public Key? ShortcutKey { get; }

    public ModifierKeys ShortcutModifiers { get; }

    public Func<Task<bool>> ExecuteAsync { get; }

    public bool CanExecute() => _canExecute?.Invoke() ?? true;

    /// <summary>按鈕 Tooltip 與自動化說明；有快捷鍵就一起說出來。</summary>
    public string Description => ShortcutKey is { } key
        ? $"{Label}（{new KeyGesture(key, ShortcutModifiers).GetDisplayStringForCulture(System.Globalization.CultureInfo.InvariantCulture)}）"
        : Label;

    public bool Matches(Key key, ModifierKeys modifiers) => ShortcutKey == key && ShortcutModifiers == modifiers;
}

/// <summary>
/// 清單的多選狀態，不認得列的型別；清單、選取工具列與樣板都只看這一層。
/// </summary>
internal interface ISqlCardSelection
{
    /// <summary>有勾選任何一列；一列都沒勾就自動離開多選模式。</summary>
    bool IsActive { get; }

    /// <summary>勾起來的列數（已載入的那幾列）。</summary>
    int Count { get; }

    /// <summary>已載入的列數；頁尾不算。</summary>
    int LoadedCount { get; }

    /// <summary>全選過：條件下的每一筆都算，包括還沒載入的；取消任何一列就回到只算勾起來的那幾筆。</summary>
    bool IsAllMatching { get; }

    /// <summary>還有沒載入的列；全部符合時工具列據此說明已載入幾筆。</summary>
    bool HasMore { get; }

    /// <summary>有動作在執行；期間不接受第二個動作。</summary>
    bool IsBusy { get; }

    IReadOnlyList<SqlSelectionAction> Actions { get; }

    /// <summary>勾選、模式、全部符合或忙碌狀態變了。</summary>
    event EventHandler? Changed;

    /// <summary>一個動作執行完；第二個參數是它自己回報的成功與否。</summary>
    event Action<SqlSelectionAction, bool>? ActionCompleted;

    /// <summary>切換一列；不是這份清單的列時不做事。</summary>
    void Toggle(object item);

    /// <summary>從錨點勾到這一列（含兩端）。</summary>
    /// <param name="fallbackAnchor">還沒有錨點時從哪一列算起（通常是目前的焦點列）；null 時只勾這一列。</param>
    void SelectRange(object item, object? fallbackAnchor = null);

    /// <summary>
    /// 全選：勾起已載入的每一列，並把還沒載入的一起算進來（全部符合）。
    /// </summary>
    /// <remarks>
    /// 一步到位，不分「已載入」與「全部符合」兩層：使用者按全選要的是符合條件的全部，
    /// 載入到第幾頁是虛擬化與分頁的實作細節。還沒載入的那幾筆由動作自己去讀。
    /// </remarks>
    void SelectAll();

    /// <summary>清空勾選並離開多選模式。</summary>
    void Clear();

    Task<bool> InvokeAsync(SqlSelectionAction action);

    /// <summary>多選模式中按下某個動作的快捷鍵就執行它；不在多選模式時一律不接，原本的按鍵行為不變。</summary>
    bool TryInvokeShortcut(Key key, ModifierKeys modifiers);
}

/// <summary>
/// 以列識別為鍵的勾選集合、範圍錨點與動作派送；ListBox 的 <c>SelectedItem</c> 仍只代表焦點與預覽。
/// </summary>
/// <remarks>
/// 不用 <c>ListBox.SelectedItems</c>：那一份與鍵盤焦點、預覽綁在一起，改成多選就等於讓方向鍵
/// 一路改勾選，而且在 recycling 虛擬化下，沒有容器的列選不起來。這裡以識別為鍵，所以重新整理
/// 換了一批新的列物件，同一筆照樣勾著。
///
/// 切換一列是 O(1)：一次雜湊集合操作加一次旗標設定，不重建 <c>ItemsSource</c> 也不重新整理清單。
/// 全選與範圍只改列上的旗標，不逐一產生容器。新加入集合的列由這裡依識別補上旗標，
/// 所以續頁、收藏更新後重新插入的列都不必呼叫端自己記得。
/// </remarks>
internal sealed class SqlCardSelection<TRow, TKey> : ISqlCardSelection where TRow : class, ISqlCheckableRow
{
    private readonly ObservableCollection<TRow> _rows;
    private readonly Func<TRow, TKey> _key;
    private readonly HashSet<TKey> _checked;
    private readonly List<SqlSelectionAction> _actions = new();
    private TKey _anchor = default!;
    private bool _hasAnchor;
    private bool _allMatching;
    private bool _hasMore;
    private bool _busy;

    public SqlCardSelection(ObservableCollection<TRow> rows, Func<TRow, TKey> key, IEqualityComparer<TKey>? comparer = null)
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _checked = new HashSet<TKey>(comparer ?? EqualityComparer<TKey>.Default);
        _rows.CollectionChanged += OnRowsChanged;
    }

    public event EventHandler? Changed;

    public event Action<SqlSelectionAction, bool>? ActionCompleted;

    public bool IsActive => _checked.Count > 0;

    public int Count => _checked.Count;

    public int LoadedCount => _rows.Count;

    public bool IsAllMatching => _allMatching;

    public bool IsBusy => _busy;

    public IReadOnlyList<SqlSelectionAction> Actions => _actions;

    /// <summary>還有沒載入的列（下一頁，或還在分批套上的結果）；由擁有載入狀態的一方寫進來。</summary>
    public bool HasMore
    {
        get => _hasMore;
        set
        {
            if (_hasMore == value) return;
            _hasMore = value;
            Raise();
        }
    }

    public void AddAction(SqlSelectionAction action) => _actions.Add(action ?? throw new ArgumentNullException(nameof(action)));

    public bool IsChecked(TKey key) => _checked.Contains(key);

    /// <summary>依清單的顯示順序列出勾起來的列，不照點選的先後。</summary>
    public IEnumerable<TRow> CheckedRows()
    {
        foreach (var row in _rows)
            if (_checked.Contains(_key(row))) yield return row;
    }

    public void Toggle(object item)
    {
        if (item is not TRow row) return;
        var key = _key(row);
        var on = _checked.Add(key);
        if (!on)
        {
            _checked.Remove(key);
            _allMatching = false;
        }

        row.IsChecked = on;
        _anchor = key;
        _hasAnchor = true;
        Raise();
    }

    public void SelectRange(object item, object? fallbackAnchor = null)
    {
        if (item is not TRow row) return;
        var target = _rows.IndexOf(row);
        if (target < 0) return;
        var anchor = _hasAnchor ? IndexOf(_anchor) : -1;
        if (anchor < 0 && fallbackAnchor is TRow fallback && _rows.IndexOf(fallback) is var start and >= 0)
        {
            // 第一次 Shift+點擊與檔案總管相同：從目前的焦點列勾起，那一列也成為錨點。
            anchor = start;
            _anchor = _key(fallback);
            _hasAnchor = true;
        }

        if (anchor < 0)
        {
            // 沒有錨點（或錨點那一列已經不在）：這一列就是新的起點。
            if (!_checked.Contains(_key(row))) Toggle(row);
            else { _anchor = _key(row); _hasAnchor = true; }
            return;
        }

        // 錨點不動：Shift 再點另一列是改範圍的另一端，與檔案總管相同。
        for (var index = Math.Min(anchor, target); index <= Math.Max(anchor, target); index++) Check(_rows[index]);
        Raise();
    }

    public void SelectAll()
    {
        if (_rows.Count == 0) return;
        foreach (var row in _rows) Check(row);
        _allMatching = true;
        Raise();
    }

    public void Clear()
    {
        if (_checked.Count == 0 && !_allMatching) return;
        foreach (var row in _rows) row.IsChecked = false;
        _checked.Clear();
        _allMatching = false;
        _hasAnchor = false;
        Raise();
    }

    /// <summary>單筆刪除成功：把那一筆移出勾選；最後一筆移出就離開多選模式。</summary>
    public void Remove(TKey key)
    {
        if (_checked.Remove(key)) Raise();
    }

    /// <summary>
    /// 重新整理之後只留下仍在清單上的那幾筆。
    /// </summary>
    /// <remarks>
    /// 重新整理只重讀第一頁，所以「不在清單上」包括已被刪除與排到後面幾頁的兩種。
    /// 兩種都拿掉：留著看不到的勾，工具列上的「已選 N 筆」就對不上畫面，而且使用者取消不掉。
    /// </remarks>
    public void RetainLoaded()
    {
        if (_checked.Count == 0) return;
        var loaded = new HashSet<TKey>(_checked.Comparer);
        foreach (var row in _rows) loaded.Add(_key(row));
        var before = _checked.Count;
        _checked.IntersectWith(loaded);
        if (_hasAnchor && !_checked.Contains(_anchor)) _hasAnchor = false;
        if (_checked.Count != before) Raise();
    }

    public async Task<bool> InvokeAsync(SqlSelectionAction action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (_busy || !IsActive || !action.CanExecute()) return false;
        _busy = true;
        Raise();
        bool succeeded;
        try
        {
            succeeded = await action.ExecuteAsync().ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
            Raise();
        }

        ActionCompleted?.Invoke(action, succeeded);
        return succeeded;
    }

    public bool TryInvokeShortcut(Key key, ModifierKeys modifiers)
    {
        if (!IsActive) return false;
        foreach (var action in _actions)
        {
            if (!action.Matches(key, modifiers)) continue;
            _ = InvokeAsync(action);
            return true;
        }

        return false;
    }

    private void Check(TRow row)
    {
        _checked.Add(_key(row));
        row.IsChecked = true;
    }

    private int IndexOf(TKey key)
    {
        for (var index = 0; index < _rows.Count; index++)
            if (_checked.Comparer.Equals(_key(_rows[index]), key)) return index;
        return -1;
    }

    /// <summary>
    /// 新加入的列依識別補上旗標。
    /// </summary>
    /// <remarks>
    /// 續頁的新列預設不勾；已經是「全部符合」時新列本來就在範圍裡，照樣勾起來。
    /// 收藏更新後以新物件插回原處，識別相同，所以仍是勾著的。
    /// </remarks>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs change)
    {
        if (change.NewItems is { Count: > 0 } added)
        {
            foreach (TRow row in added)
            {
                var key = _key(row);
                if (_allMatching) _checked.Add(key);
                row.IsChecked = _checked.Contains(key);
            }
        }

        // 列數變了，「已載入的全部都勾了」可能跟著變；不在多選模式時工具列本來就收著，不必通知。
        if (IsActive) Raise();
    }

    private void Raise()
    {
        // 最後一筆離開勾選就是離開多選模式；全部符合與錨點都屬於那一輪，一起放掉。
        if (_checked.Count == 0)
        {
            _allMatching = false;
            _hasAnchor = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
