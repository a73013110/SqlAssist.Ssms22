using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Metadata.ResultGrid;

namespace SqlAssist.Ssms22.ResultGrid;

/// <summary>
/// 目前那一個 SSMS 查詢結果格線，以及從它身上取出一塊資料。
/// </summary>
/// <remarks>
/// 這是整組結果格線功能唯一碰得到 SSMS 內部型別的地方。取出來的東西一律是
/// <see cref="ResultGridTable"/>，之後的判斷與產指令碼全部在 <c>SqlAssist.Metadata</c>
/// 裡跑得了單元測試——CLAUDE.md 那條「禁止把只看文字就能判斷的邏輯寫進 Ssms22」
/// 在這裡的實際形狀就是這一條界線。
///
/// <b>兩套欄索引。</b>同一個儲存體上，<c>GetServerDataTypeName</c> 那一族吃 0 起算
/// 的資料欄，<c>GetCellData</c> 這一族吃格線欄——第 0 欄是列號欄，資料從 1 開始。
/// 換算只在這個檔案裡做一次。第一版探測假設兩邊一致，第 0 欄直接
/// <c>ArgumentOutOfRangeException</c>；那次是撞到邊界才炸的，欄位再多一個就會
/// 安靜地回傳列號，整份錯開一欄而每一格都還「有值」。
/// </remarks>
internal sealed class SsmsResultGrid
{
    /// <summary>控制項樹的搜尋深度上限。</summary>
    private const int MaxDepth = 24;

    private const string GridControlTypeName = "Microsoft.SqlServer.Management.UI.Grid.GridControl";

    /// <summary>格線欄索引減掉這個值才是資料欄索引；第 0 欄是列號欄。</summary>
    private const int RowNumberColumns = 1;

    private readonly object _grid;
    private readonly object _storage;

    private SsmsResultGrid(object grid, object storage)
    {
        _grid = grid;
        _storage = storage;
    }

    /// <summary>
    /// 找出使用者正在看的那一個結果格線。
    /// </summary>
    /// <remarks>
    /// 從作用中文件的 DocView 往下找，不掃全域的 HWND：DocView 就是那一個查詢
    /// 視窗，從它往下找到的格線一定屬於它。同時開著好幾個查詢視窗時，
    /// HWND 掃描會拿到別人的結果，而那個錯誤產出的指令碼看起來完全正常。
    ///
    /// 一個查詢視窗可以有好幾個結果格線（多個結果集）。優先取有焦點的那一個
    /// ——右鍵按下去的就是它；沒有焦點資訊時退而取有選取範圍的那一個。
    /// </remarks>
    public static bool TryGetActive(out SsmsResultGrid? grid, out string failure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        grid = null;
        failure = string.Empty;

        var docView = TryGetDocView();

        if (docView is null)
        {
            failure = "找不到作用中的查詢視窗。";
            return false;
        }

        var found = new List<object>();
        Collect(docView, 0, found);

        if (found.Count == 0)
        {
            failure = "這個查詢視窗裡沒有結果格線。先執行一次會回傳結果的查詢。";
            return false;
        }

        var chosen = Choose(found);

        if (GridReflection.Property(chosen, "GridStorage") is not { } storage)
        {
            failure = "這個結果格線還沒有資料。";
            return false;
        }

        grid = new SsmsResultGrid(chosen, storage);
        return true;
    }

    /// <summary>
    /// 把選取範圍（沒有選取就是整份結果）讀成一塊矩形資料。
    /// </summary>
    public bool TryRead(out ResultGridTable? table, out string failure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        table = null;

        var totalColumns = GridReflection.Property<int>(_storage, "NumberOfDataColumns") ?? 0;
        var totalRows = GridReflection.Property<long>(_storage, "TotalNumberOfRows") ?? 0L;

        if (!TryCheckColumnOrder(totalColumns, out failure))
        {
            return false;
        }

        if (!ResultGridSelectionPlan.TryResolve(
                ReadSelection(totalColumns),
                totalRows,
                totalColumns,
                out var rows,
                out var columns,
                out var isWholeResult,
                out failure))
        {
            return false;
        }

        var getCellData = GridReflection.BindCell(_storage, "GetCellData");
        var isCellDataNull = GridReflection.BindCellFlag(_storage, "IsCellDataNull");

        if (getCellData is null || isCellDataNull is null)
        {
            failure = "這個版本的 SSMS 沒有提供讀取儲存格的方法，無法從結果格線取值。";
            return false;
        }

        var descriptors = ReadColumns(columns);
        var data = new object?[rows.Count][];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var values = new object?[columns.Count];

            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var gridColumn = columns[columnIndex] + RowNumberColumns;

                // 一定要先問 IsCellDataNull。真正的 NULL 與內容剛好是 NULL 這四個字
                // 的字串，取回來之後分不出來——那正是這個功能不走剪貼簿的理由。
                values[columnIndex] = isCellDataNull(row, gridColumn)
                    ? null
                    : getCellData(row, gridColumn);
            }

            data[rowIndex] = values;
        }

        table = new ResultGridTable(descriptors, data, isWholeResult);
        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// 讀出使用者剛剛點的那一格。
    /// </summary>
    /// <remarks>
    /// 取的是選取範圍第一個區塊的左上角。SSMS 在按右鍵時會把游標下那一格選起來，
    /// 所以那一格就是它。不另外去問格線的「目前儲存格」：那個屬性的名稱與語意
    /// 沒有文件，而選取範圍這條路已經在其他命令上驗過。
    ///
    /// 只讀一格，不走選取範圍換算：那一層會把選取撐成矩形，而這裡要的就是一格。
    /// </remarks>
    public bool TryReadAnchorCell(out ResultGridColumn? column, out object? value, out string failure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        column = null;
        value = null;

        var totalColumns = GridReflection.Property<int>(_storage, "NumberOfDataColumns") ?? 0;
        var totalRows = GridReflection.Property<long>(_storage, "TotalNumberOfRows") ?? 0L;

        if (!TryCheckColumnOrder(totalColumns, out failure))
        {
            return false;
        }

        var blocks = ReadSelection(totalColumns);

        if (blocks.Count == 0)
        {
            failure = "先在結果格線裡點一格，再執行一次這個命令。";
            return false;
        }

        var row = blocks[0].Top;
        var dataColumn = blocks[0].Left;

        if (row < 0 || row >= totalRows || dataColumn < 0 || dataColumn >= totalColumns)
        {
            failure = "點到的位置不在資料範圍內。";
            return false;
        }

        var getCellData = GridReflection.BindCell(_storage, "GetCellData");
        var isCellDataNull = GridReflection.BindCellFlag(_storage, "IsCellDataNull");

        if (getCellData is null || isCellDataNull is null)
        {
            failure = "這個版本的 SSMS 沒有提供讀取儲存格的方法，無法從結果格線取值。";
            return false;
        }

        var gridColumn = dataColumn + RowNumberColumns;
        column = ReadColumns(new[] { dataColumn })[0];
        value = isCellDataNull(row, gridColumn) ? null : getCellData(row, gridColumn);
        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// 使用者拖動過欄位順序的話就整段拒絕。
    /// </summary>
    /// <remarks>
    /// 選取範圍給的欄座標是<b>畫面上</b>的位置，而儲存體吃的是原始順序。
    /// 兩者在沒有拖動過的時候相同，拖動之後就會錯開——而錯開的產出是
    /// 「每一格都有值、每一欄的名字也對，只是值來自別的欄」，
    /// 貼上去執行得動，看不出任何異狀。
    ///
    /// 這裡選擇拒絕而不是自己換算：<c>GetOriginalColumnIndex</c> 的方向
    /// （畫面到原始，或原始到畫面）沒有文件，猜錯的症狀與不換算完全一樣。
    /// 請使用者把欄位順序拉回去，比產一份錯的指令碼給他好。
    /// </remarks>
    private bool TryCheckColumnOrder(int totalColumns, out string failure)
    {
        failure = string.Empty;
        var original = GridReflection.BindByIndex(_grid, "GetOriginalColumnIndex");

        if (original is null)
        {
            return true;
        }

        for (var column = 0; column < totalColumns + RowNumberColumns; column++)
        {
            if (original(column) is int mapped && mapped != column)
            {
                failure = "這個結果格線的欄位順序被拖動過。SqlAssist 只在原始順序下取值，"
                    + "請先把欄位順序還原（或重新執行一次查詢）再試。";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 讀出選取的區塊，欄座標換算成 0 起算的資料欄。
    /// </summary>
    /// <remarks>
    /// 選取範圍不保證是矩形：按住 Ctrl 點六格拿到的就是六個 1×1 的區塊。
    /// 用整列選取（點列號）時區塊會從第 0 欄開始，也就是列號欄——
    /// 那一欄沒有對應的資料欄，所以往右夾一格。
    /// </remarks>
    private IReadOnlyList<ResultGridSelectionBlock> ReadSelection(int totalColumns)
    {
        var blocks = new List<ResultGridSelectionBlock>();

        if (GridReflection.Property(_grid, "SelectedCells") is not IEnumerable selected)
        {
            return blocks;
        }

        foreach (var block in selected)
        {
            if (block is null)
            {
                continue;
            }

            var x = GridReflection.Property<int>(block, "X") ?? 0;
            var width = GridReflection.Property<int>(block, "Width") ?? 0;
            var y = GridReflection.Property<long>(block, "Y") ?? 0L;
            var height = GridReflection.Property<long>(block, "Height") ?? 0L;

            var left = Math.Max(x, RowNumberColumns) - RowNumberColumns;
            var right = Math.Min(x + width - 1 - RowNumberColumns, totalColumns - 1);

            if (right < left || height <= 0)
            {
                continue;
            }

            blocks.Add(new ResultGridSelectionBlock(y, height, left, right - left + 1));
        }

        return blocks;
    }

    private ResultGridColumn[] ReadColumns(IReadOnlyList<int> columns)
    {
        var names = ReadColumnNames();
        var serverType = GridReflection.BindByIndex(_storage, "GetServerDataTypeName");
        var descriptors = new ResultGridColumn[columns.Count];

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];

            descriptors[index] = new ResultGridColumn(
                column < names.Count ? names[column] : null,
                serverType?.Invoke(column) as string);
        }

        return descriptors;
    }

    /// <remarks>
    /// 欄名取不到不是致命的：產指令碼那一層會補上 <c>Column1</c> 這種名字。
    /// 型別取不到才是致命的，那一層會整段拒絕。
    /// </remarks>
    private IReadOnlyList<string> ReadColumnNames()
    {
        var names = new List<string>();

        if (GridReflection.Property(_storage, "ColumnNames") is IEnumerable source)
        {
            foreach (var name in source)
            {
                names.Add(name?.ToString() ?? string.Empty);
            }
        }

        return names;
    }

    /// <remarks>
    /// 有焦點的那一個就是使用者剛剛按右鍵的那一個。焦點問不出來時退到
    /// 「有選取範圍的那一個」——同一個查詢視窗裡通常只有一個格線被選過。
    /// </remarks>
    private static object Choose(List<object> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (GridReflection.Property<bool>(candidate, "Focused") == true)
            {
                return candidate;
            }
        }

        foreach (var candidate in candidates)
        {
            if (GridReflection.Property(candidate, "SelectedCells") is IEnumerable selected)
            {
                foreach (var block in selected)
                {
                    _ = block;
                    return candidate;
                }
            }
        }

        return candidates[0];
    }

    private static object? TryGetDocView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is not IVsMonitorSelection monitor)
        {
            return null;
        }

        var hr = monitor.GetCurrentElementValue(
            (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame,
            out var frameObject);

        if (!ErrorHandler.Succeeded(hr) || frameObject is not IVsWindowFrame frame)
        {
            return null;
        }

        return ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view))
            ? view
            : null;
    }

    /// <summary>走訪 WinForms 控制項樹，收集所有格線。</summary>
    private static void Collect(object control, int depth, List<object> found)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (InheritsFrom(control.GetType(), GridControlTypeName))
        {
            found.Add(control);

            // 格線裡面不會再包著另一個格線。
            return;
        }

        if (GridReflection.Property(control, "Controls") is not IEnumerable children)
        {
            return;
        }

        foreach (var child in children)
        {
            if (child is not null)
            {
                Collect(child, depth + 1, found);
            }
        }
    }

    private static bool InheritsFrom(Type type, string baseTypeFullName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, baseTypeFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
