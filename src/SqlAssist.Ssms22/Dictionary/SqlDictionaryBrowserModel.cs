using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Connections;
using SqlAssist.Core.Tabular;
using SqlAssist.Metadata.Dictionary;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Dictionary;

/// <summary>資料字典的兩種看法。</summary>
internal enum SqlDictionaryView
{
    /// <summary>主從：左邊資料表，右邊那一張的資料行與資料表說明。</summary>
    Tables,

    /// <summary>資料字典：每一個資料行攤成一列，照名稱排，同名的排在一起比。</summary>
    Columns
}

/// <summary>
/// 資料字典工具窗的狀態：範圍、目前的字典、編輯、同名比較與過濾。零 VS 相依，寫得進回歸測試。
/// </summary>
/// <remarks>
/// 兩種檢視讀的是同一份：過濾規則（<see cref="SqlDictionaryFilter"/>）、編輯（<see cref="SqlDescriptionEdits"/>）
/// 與列物件都只有一份，切換檢視只換排法。
///
/// 編輯照「伺服器＋資料庫」各記一份：切到別的資料庫看一眼再切回來，還沒寫回的說明仍然在。
/// 換資料庫因此不必先問「要不要放棄變更」——那一問得擋在範圍列上，而範圍列是共用的，不該知道哪個視窗
/// 有未存的東西。關掉工具窗時才真的丟。
/// </remarks>
internal sealed class SqlDictionaryBrowserModel
{
    private readonly Dictionary<string, SqlDescriptionEdits> _editsByDatabase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SqlDescriptionTarget, SqlDictionaryObjectRow> _objectRows = new();
    private readonly Dictionary<SqlDescriptionTarget, SqlDictionaryColumnRow> _columnRows = new();
    private readonly Dictionary<string, List<SqlDictionaryColumnRow>> _rowsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SqlDescriptionTarget, SqlDictionaryObjectRow> _ownerRows = new();

    public SqlDictionaryBrowserModel()
    {
        Edits = new SqlDescriptionEdits();
    }

    /// <summary>伺服器單選、資料庫單選必選：字典一次只看一個資料庫。</summary>
    public SqlConnectionScope Scope { get; } =
        new(multipleServers: false, StringComparer.OrdinalIgnoreCase, multipleDatabases: false);

    /// <summary>目前這個資料庫的編輯。</summary>
    public SqlDescriptionEdits Edits { get; private set; }

    public SqlDataDictionary? Dictionary { get; private set; }

    public SqlColumnNameGroups Groups { get; private set; } = SqlColumnNameGroups.Empty;

    public SqlDictionaryFilter Filter { get; set; } = SqlDictionaryFilter.Default;

    public SqlDictionaryView View { get; set; } = SqlDictionaryView.Tables;

    /// <summary>還沒寫回的說明變更數改了；頁尾與套用按鈕跟著它。</summary>
    public event EventHandler? EditsChanged;

    /// <summary>
    /// 換到另一個資料庫（或還沒選）：編輯換成那一個資料庫的那一份，字典先清掉等載入。
    /// </summary>
    public void UseDatabase(string? serverName, string? databaseName)
    {
        Edits = string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(databaseName)
            ? new SqlDescriptionEdits()
            : EditsFor(serverName!, databaseName!);
        Load(null);
        EditsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 換上新載入的字典；列物件整份重建，編輯照目標留著，伺服器上已經是那一句的不再算變更。
    /// </summary>
    /// <remarks>寫回成功之後不另外收尾：重新載入就是收尾，規則見 <see cref="SqlDescriptionEdits.Settle"/>。</remarks>
    public void Load(SqlDataDictionary? dictionary)
    {
        Dictionary = dictionary;
        _objectRows.Clear();
        _columnRows.Clear();
        _rowsByName.Clear();
        _ownerRows.Clear();

        if (dictionary is not null)
        {
            foreach (var item in dictionary.Objects)
            {
                var owner = new SqlDictionaryObjectRow(this, item);
                _objectRows[item.Target] = owner;

                foreach (var column in item.Columns)
                {
                    var row = new SqlDictionaryColumnRow(this, item, column);
                    _columnRows[row.Target] = row;
                    _ownerRows[row.Target] = owner;

                    if (!_rowsByName.TryGetValue(column.Name, out var list)) _rowsByName[column.Name] = list = new List<SqlDictionaryColumnRow>();
                    list.Add(row);
                }
            }
        }

        var settled = dictionary is not null && Edits.Settle(OriginalOf).Count > 0;
        RebuildGroups();
        if (settled) EditsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>過濾之後主清單上的物件，照字典的順序。</summary>
    public IReadOnlyList<SqlDictionaryObjectRow> MatchingObjects()
    {
        if (Dictionary is null) return Array.Empty<SqlDictionaryObjectRow>();

        var rows = new List<SqlDictionaryObjectRow>();
        foreach (var item in Dictionary.Objects)
        {
            if (Filter.MatchingColumns(item, Edits.Current, Groups).Count > 0) rows.Add(_objectRows[item.Target]);
        }

        return rows;
    }

    /// <summary>一個物件留下的資料行，照 <c>column_id</c>。</summary>
    public IReadOnlyList<SqlDictionaryColumnRow> ColumnsOf(SqlDictionaryObject item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        return Filter.MatchingColumns(item, Edits.Current, Groups).Select(column => _columnRows[item.TargetOf(column)]).ToList();
    }

    /// <summary>
    /// 資料字典檢視：每一個留下來的資料行，照名稱排、同名的照物件排。
    /// </summary>
    /// <remarks>名稱不分大小寫排：<c>ReaderNo</c> 與 <c>READERNO</c> 排在一起，正是要比的那一組。</remarks>
    public IReadOnlyList<SqlDictionaryColumnRow> MatchingColumns()
    {
        if (Dictionary is null) return Array.Empty<SqlDictionaryColumnRow>();

        var rows = new List<SqlDictionaryColumnRow>(Dictionary.ColumnCount);
        foreach (var item in Dictionary.Objects)
        {
            foreach (var column in Filter.MatchingColumns(item, Edits.Current, Groups)) rows.Add(_columnRows[item.TargetOf(column)]);
        }

        return rows
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>寫一筆說明；真的改變了才重算同名比較並通知看得到它的那幾列。</summary>
    public bool SetDescription(SqlDescriptionTarget target, string? original, string? value)
    {
        if (!Edits.Set(target, original, value)) return false;
        Changed(new[] { target });
        return true;
    }

    /// <summary>
    /// 一次寫好幾筆（貼上、套用建議）：同名比較只重算一次。
    /// </summary>
    /// <returns>真的改變了幾筆。</returns>
    public int SetDescriptions(IEnumerable<KeyValuePair<SqlDescriptionTarget, string>> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));

        var changed = new List<SqlDescriptionTarget>();
        foreach (var pair in values)
        {
            if (Edits.Set(pair.Key, OriginalOf(pair.Key), pair.Value)) changed.Add(pair.Key);
        }

        if (changed.Count > 0) Changed(changed);
        return changed.Count;
    }

    /// <summary>
    /// 把剪貼簿上的表格貼到 <paramref name="rows"/>（畫面上的順序），從 <paramref name="startIndex"/> 那一列開始。
    /// </summary>
    public (int Applied, int Skipped) Paste(IReadOnlyList<SqlDictionaryColumnRow> rows, int startIndex, string? text)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));

        var slots = rows.Select(row => new SqlDescriptionPasteSlot(row.Owner, row.Column)).ToList();
        var plan = SqlDescriptionPaste.Plan(slots, startIndex, SqlTabularText.ParseTsv(text));
        var applied = SetDescriptions(plan.Assignments.Select(pair => new KeyValuePair<SqlDescriptionTarget, string>(pair.Key.Target, pair.Value)));
        return (applied, plan.Skipped);
    }

    /// <summary>這幾列裡空著、而同名的寫法一致的，照抄那一句。</summary>
    /// <returns>補了幾筆。</returns>
    public int ApplySuggestions(IEnumerable<SqlDictionaryColumnRow> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));

        return SetDescriptions(rows
            .Where(row => row.Suggestion is not null)
            .Select(row => new KeyValuePair<SqlDescriptionTarget, string>(row.Target, row.Suggestion!))
            .ToList());
    }

    /// <summary>放棄這幾個目標的編輯。</summary>
    public void Revert(IEnumerable<SqlDescriptionTarget> targets)
    {
        if (targets is null) throw new ArgumentNullException(nameof(targets));

        var reverted = targets.Where(Edits.Revert).ToList();
        if (reverted.Count > 0) Changed(reverted);
    }

    /// <summary>放棄這個資料庫全部的編輯。</summary>
    public void RevertAll()
    {
        var targets = Edits.Changes.Select(change => change.Target).ToList();
        if (Edits.Clear()) Changed(targets);
    }

    /// <summary>
    /// 寫回被擋下之後：換上伺服器上現在的值當原值，讓使用者看過之後再按一次就是覆蓋。
    /// </summary>
    /// <remarks>
    /// 不自動重試：別人剛改的那一句使用者還沒看過，自動覆蓋等於替他決定了誰的版本留下。
    /// 換了原值之後，那一格與伺服器上的值一樣的就不再算變更。
    /// </remarks>
    public void Rebase(IEnumerable<SqlDescriptionConflict> conflicts)
    {
        if (conflicts is null) throw new ArgumentNullException(nameof(conflicts));

        var targets = new List<SqlDescriptionTarget>();
        foreach (var conflict in conflicts)
        {
            var target = conflict.Change.Target;
            Edits.Revert(target);
            Edits.Set(target, conflict.Current, conflict.Change.Value);
            targets.Add(target);
        }

        if (targets.Count > 0) Changed(targets);
    }

    public bool HasColumnChanges(SqlDictionaryObject item) =>
        item.Columns.Any(column => Edits.IsChanged(item.TargetOf(column)));

    public int DescribedColumns(SqlDictionaryObject item) =>
        item.Columns.Count(column => Edits.Current(item.TargetOf(column), column.Description).Length > 0);

    /// <summary>這個目標在字典裡載入時的說明；字典裡沒有（已經換過資料庫）時是空的。</summary>
    private string OriginalOf(SqlDescriptionTarget target) =>
        _columnRows.TryGetValue(target, out var column) ? column.Column.Description
        : _objectRows.TryGetValue(target, out var item) ? item.Object.Description
        : "";

    private SqlDescriptionEdits EditsFor(string serverName, string databaseName)
    {
        var key = serverName + "\u001F" + databaseName;
        if (!_editsByDatabase.TryGetValue(key, out var edits)) _editsByDatabase[key] = edits = new SqlDescriptionEdits();
        return edits;
    }

    private void RebuildGroups() =>
        Groups = Dictionary is null ? SqlColumnNameGroups.Empty : SqlColumnNameGroups.Build(Dictionary, Edits.Current);

    /// <summary>
    /// 編輯變了：重算同名比較，通知改到的列、它們的物件列與同名的每一列。
    /// </summary>
    /// <remarks>
    /// 同名的每一列都要通知：補了一格說明，其他同名那幾格的建議與「說明不一」就跟著變。
    /// 只通知改到的那一列的症狀是建議還掛在剛被別人照抄過的格子上。
    /// </remarks>
    private void Changed(IReadOnlyCollection<SqlDescriptionTarget> targets)
    {
        RebuildGroups();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new HashSet<SqlDictionaryObjectRow>();

        foreach (var target in targets)
        {
            if (_columnRows.TryGetValue(target, out var row))
            {
                row.Refresh();
                names.Add(row.Name);
                if (_ownerRows.TryGetValue(target, out var owner)) owners.Add(owner);
            }
            else if (_objectRows.TryGetValue(target, out var item))
            {
                owners.Add(item);
            }
        }

        foreach (var name in names)
        {
            if (!_rowsByName.TryGetValue(name, out var rows)) continue;
            foreach (var row in rows) row.RefreshGroup();
        }

        foreach (var owner in owners) owner.Refresh();

        EditsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>頁尾左邊那一句：物件與資料行數，加上還沒寫回的變更。</summary>
    public string FooterSummary(int objects, int columns) =>
        Edits.Count == 0
            ? DictionaryText.Counts(objects, columns)
            : DictionaryText.CountsWithChanges(objects, columns, Edits.Count);

    /// <summary>沒有東西可看時那一塊說什麼；有東西時是 <see cref="SqlSurfaceState.None"/>。</summary>
    public SqlSurfaceState Surface(bool loading, bool failed, int visibleRows)
    {
        if (Scope.Servers.Count == 0) return SqlSurfaceState.Empty(DictionaryText.ChooseServer, DictionaryText.ChooseServerDetail, DictionaryText.ChooseServerAction);
        if (Scope.Databases.Count == 0) return SqlSurfaceState.Empty(DictionaryText.ChooseDatabase, DictionaryText.ChooseDatabaseDetail, DictionaryText.ChooseDatabaseAction);
        if (loading) return SqlSurfaceState.Loading;
        if (failed) return SqlSurfaceState.Unreadable(DictionaryText.LoadFailed);
        if (Dictionary is null) return SqlSurfaceState.Loading;
        if (Dictionary.Objects.Count == 0) return SqlSurfaceState.Empty(DictionaryText.EmptyDatabase);
        return visibleRows == 0 ? SqlSurfaceState.Empty(DictionaryText.NoMatch, DictionaryText.NoMatchDetail) : SqlSurfaceState.None;
    }
}
