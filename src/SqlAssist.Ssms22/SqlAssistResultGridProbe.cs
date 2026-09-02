using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlAssist.Ssms22;

/// <summary>
/// 一次性的可行性探測：問清楚 SqlAssist 能不能從 SSMS 的結果格線拿到<b>型別化</b>的資料
/// ——真正的 <c>NULL</c>、CLR 型別與 SQL Server 型別名——而不是只有剪貼簿那份 TSV 文字。
/// </summary>
/// <remarks>
/// 為什麼要先探測：結果格線不在現代編輯器管線上，SSMS 沒有給它任何 MEF 進入點，
/// 跟 F12 那次是同一類問題（見 <c>docs/go-to-definition.md</c>）。
///
/// 為什麼非要型別不可：剪貼簿的 TSV 裡，資料庫的 <c>NULL</c> 和一個內容剛好是
/// <c>NULL</c> 這四個字的字串長得一模一樣，欄位型別也整個消失。據此產生的
/// <c>INSERT</c> 會「跑得動但資料是錯的」——正是 CLAUDE.md 那條「禁止在資料不齊時
/// 輸出半份可以執行的東西」講的失敗模式，而且比缺欄位更難發現。
///
/// 全程反射、不新增任何組件參照：探測的目的是問問題，不是把建置綁在可能會變的
/// SSMS 內部型別上。確認可行之後，公開的那一部分（<c>GridControl</c>、
/// <c>IGridResultSet</c>）才值得改成強型別。
///
/// 這裡不走 <see cref="SqlAssistPlatformGuard"/>：每一步的失敗本身就是探測結果，
/// 要逐步記下來，而不是收斂成一句「這一輪什麼都不做」。
///
/// <b>報告一律不含儲存格內容。</b>只記型別、是否為 <c>NULL</c>、字元長度，
/// 以及「字串化之後是不是剛好等於 NULL 這四個字」——那正是要分辨的那一組，
/// 而分辨它不需要看見任何一筆真實資料。
/// </remarks>
internal static class SqlAssistResultGridProbe
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string GridControlTypeName = "Microsoft.SqlServer.Management.UI.Grid.GridControl";

    private const string WinFormsControlTypeName =
        "System.Windows.Forms.Control, System.Windows.Forms, Version=4.0.0.0, "
        + "Culture=neutral, PublicKeyToken=b77a5c561934e089";

    /// <summary>控制項樹的搜尋深度上限，避免在意外的樹上走太久。</summary>
    private const int MaxDepth = 24;

    /// <summary>HWND 掃描的上限。一個 SSMS 主視窗底下的子視窗是數百個等級。</summary>
    private const int MaxScannedWindows = 20000;

    /// <summary>每個格線最多取樣幾格。探測不需要整份資料，只要證明取得到。</summary>
    private const int MaxSampledCells = 24;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    /// <summary>
    /// 跑一次探測，把結果寫進診斷紀錄，並回傳同一份文字。
    /// </summary>
    public static string Run()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var report = new List<string>
        {
            "===== 結果格線探測 =====",
            "時間：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        var found = new List<(string Path, object Grid)>();

        // 兩條進入點都跑，因為它們回答的是不同的問題：
        // DocView 那條是正式功能要走的路（它認得出格線屬於哪一個查詢視窗），
        // HWND 掃描那條只證明「格線在不在、資料拿不拿得到」，但一定走得到。
        // 第一版探測只試了 Form.ActiveForm，而 SSMS 22 的主視窗是 WPF，
        // WinForms 控制項是以 HWND 內嵌的，那裡根本沒有 Form。
        var controlType = Step(report, "解析 WinForms Control 型別", () => Type.GetType(WinFormsControlTypeName));

        ProbeDocView(report, found);
        ProbeWindowHandles(report, found, controlType);

        report.Add("找到格線數量：" + found.Count);

        if (found.Count == 0)
        {
            report.Add("(先執行一次會回傳結果的查詢、在結果格線裡選幾格，再跑一次探測。)");
        }

        for (var i = 0; i < found.Count; i++)
        {
            report.Add(string.Empty);
            report.Add("---- 格線 #" + (i + 1) + "：" + found[i].Path + " ----");
            DescribeGrid(report, found[i].Grid);
        }

        report.Add("===== 探測結束 =====");

        var text = string.Join(Environment.NewLine, report);
        SqlAssistDiagnostics.WriteAlways(text);
        return text;
    }

    /// <summary>
    /// 從作用中的文件視窗框架取 DocView，再往下走訪控制項樹。
    /// </summary>
    /// <remarks>
    /// 這是正式功能要走的路：DocView 就是那一個查詢視窗，從它往下找到的格線
    /// 一定屬於它，不會在多個查詢視窗開著的時候拿到別人的結果。
    /// 這一段就算失敗也要把 DocView 的實際型別記下來——那是下一步的線索。
    /// </remarks>
    private static void ProbeDocView(List<string> report, List<(string, object)> found)
    {
        report.Add("-- 進入點 A：作用中文件的 DocView --");

        var docView = Step(report, "取得 DocView", () =>
        {
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

            return ErrorHandler.Succeeded(
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view))
                ? view
                : null;
        });

        if (docView is null)
        {
            return;
        }

        report.Add("DocView 型別：" + docView.GetType().FullName);
        report.Add("DocView 的基底鏈：" + BaseChainOf(docView.GetType()));

        Step(report, "從 DocView 往下搜尋", () =>
        {
            var before = found.Count;
            Collect(docView, "DocView", 0, found);
            return found.Count - before;
        });
    }

    /// <summary>
    /// 掃 SSMS 主視窗底下的所有子 HWND，用 <c>Control.FromHandle</c> 換回受控控制項。
    /// </summary>
    /// <remarks>
    /// 格線自己有 HWND，所以這條路直接就能換到它，不必經過任何父容器。
    /// 缺點是分不出格線屬於哪一個查詢視窗——所以它只用來回答可行性，
    /// 正式功能要用 DocView 那條。
    /// </remarks>
    private static void ProbeWindowHandles(
        List<string> report,
        List<(string, object)> found,
        Type? controlType)
    {
        report.Add("-- 進入點 B：主視窗 HWND 掃描 --");

        if (controlType is null)
        {
            report.Add("!! 解析不到 WinForms Control 型別，跳過這條路。");
            return;
        }

        var fromHandle = controlType.GetMethod(
            "FromHandle",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            new[] { typeof(IntPtr) },
            modifiers: null);

        if (fromHandle is null)
        {
            report.Add("!! 找不到 Control.FromHandle，跳過這條路。");
            return;
        }

        var root = Step(report, "主視窗 HWND", () =>
        {
            using var process = Process.GetCurrentProcess();
            return process.MainWindowHandle;
        });

        if (root == IntPtr.Zero)
        {
            return;
        }

        var scanned = 0;
        var mapped = 0;

        Step(report, "掃描子視窗", () =>
        {
            // EnumChildWindows 本身就會走到所有後代，不必自己再遞迴一次。
            EnumChildWindows(
                root,
                (window, _) =>
                {
                    if (++scanned > MaxScannedWindows)
                    {
                        return false;
                    }

                    object? control;

                    try
                    {
                        control = fromHandle.Invoke(null, new object[] { window });
                    }
                    catch (Exception)
                    {
                        // 這一個 HWND 換不到受控控制項是常態（絕大多數本來就不是 WinForms）。
                        return true;
                    }

                    if (control is null)
                    {
                        return true;
                    }

                    mapped++;

                    if (InheritsFrom(control.GetType(), GridControlTypeName) && !AlreadyFound(found, control))
                    {
                        found.Add(("HWND 0x" + window.ToInt64().ToString("X"), control));
                    }

                    return true;
                },
                IntPtr.Zero);

            return "掃了 " + scanned + " 個視窗，其中 " + mapped + " 個對應到受控控制項";
        });
    }

    private static bool AlreadyFound(List<(string, object)> found, object candidate)
    {
        foreach (var entry in found)
        {
            if (ReferenceEquals(entry.Item2, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>遞迴走訪 WinForms 控制項樹，收集所有繼承自 GridControl 的控制項。</summary>
    private static void Collect(object control, string path, int depth, List<(string, object)> found)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (InheritsFrom(control.GetType(), GridControlTypeName))
        {
            if (!AlreadyFound(found, control))
            {
                found.Add((path, control));
            }

            // 格線裡面不會再包著另一個格線，不往下走。
            return;
        }

        if (control.GetType().GetProperty("Controls", Any)?.GetValue(control) is not IEnumerable children)
        {
            return;
        }

        var index = 0;

        foreach (var child in children)
        {
            if (child is not null)
            {
                Collect(child, path + "/" + child.GetType().Name + "[" + index + "]", depth + 1, found);
            }

            index++;
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

    private static string BaseChainOf(Type type)
    {
        var chain = new List<string>();

        for (Type? current = type; current is not null && chain.Count < 8; current = current.BaseType)
        {
            chain.Add(current.Name);
        }

        return string.Join(" → ", chain);
    }

    private static void DescribeGrid(List<string> report, object grid)
    {
        report.Add("型別：" + grid.GetType().FullName);

        // ContextMenuStrip 是不是 null，決定右鍵選單能不能直接接上：有值代表是
        // WinForms 選單（掛得上去），null 代表 SSMS 用的是殼層選單——那就得找出
        // 命令表的 GUID:ID，或改走工具選單與快捷鍵。
        Step(report, "ContextMenuStrip", () =>
            Get(grid, "ContextMenuStrip")?.GetType().FullName ?? "(null)");

        var columnsNumber = Step(report, "ColumnsNumber", () => Get(grid, "ColumnsNumber"));
        Step(report, "SelectionType", () => Get(grid, "SelectionType"));
        Step(report, "NumberOfCharsToShow（顯示截斷上限）", () => Get(grid, "NumberOfCharsToShow"));

        DescribeSelection(report, grid);

        var storage = Step(report, "GridStorage", () => Get(grid, "GridStorage"));

        if (storage is null)
        {
            report.Add("!! 拿不到 GridStorage，這個格線沒有資料來源。");
            return;
        }

        report.Add("儲存體實作的介面：" + string.Join(
            ", ",
            Array.ConvertAll(storage.GetType().GetInterfaces(), t => t.Name)));

        var dataColumns = Step(report, "NumberOfDataColumns", () => Get(storage, "NumberOfDataColumns"));
        var totalRows = Step(report, "TotalNumberOfRows", () => Get(storage, "TotalNumberOfRows"));
        Step(report, "StoredAllData（是否已取回全部列）", () => Get(storage, "StoredAllData"));
        Step(report, "NumRows()", () => Invoke(storage, "NumRows"));

        // 格線的第 0 欄是列號欄，儲存體沒有它。這個位移搞錯的症狀是整份資料錯開
        // 一欄，而每一格都還是「有值」，看起來完全正常。
        if (columnsNumber is int gridColumns && dataColumns is int storageColumns)
        {
            report.Add(
                "欄位索引位移：格線 " + gridColumns + " 欄 vs 儲存體 " + storageColumns
                + " 欄 → 位移 " + (gridColumns - storageColumns));
        }

        Step(report, "ColumnNames", () =>
            Get(storage, "ColumnNames") is IEnumerable names
                ? CountOf(names) + " 個（內容不記錄）"
                : "(null)");

        DescribeColumnTypes(report, storage, dataColumns as int?);
        SampleCells(report, storage, totalRows as long?, dataColumns as int?);
    }

    private static void DescribeSelection(List<string> report, object grid)
    {
        Step(report, "SelectedCells", () =>
        {
            if (Get(grid, "SelectedCells") is not IEnumerable blocks)
            {
                return "(null)";
            }

            var text = new StringBuilder();
            var count = 0;

            foreach (var block in blocks)
            {
                if (block is null)
                {
                    continue;
                }

                count++;
                text.Append(" [X=").Append(Get(block, "X"))
                    .Append(" Y=").Append(Get(block, "Y"))
                    .Append(" W=").Append(Get(block, "Width"))
                    .Append(" H=").Append(Get(block, "Height"))
                    .Append(']');
            }

            return count == 0 ? "(空)" : count + " 個區塊：" + text;
        });
    }

    /// <summary>
    /// 每一欄的型別資訊。這是整份探測的重點：有了這些，產生的 <c>INSERT</c>
    /// 才寫得出正確的 <c>CAST</c> 與常值格式。
    /// </summary>
    private static void DescribeColumnTypes(List<string> report, object storage, int? dataColumns)
    {
        if (dataColumns is not int columns || columns <= 0)
        {
            return;
        }

        report.Add("欄位型別（欄名不記錄）：");

        for (var column = 0; column < columns; column++)
        {
            var index = column;

            report.Add(
                "  第 " + index + " 欄："
                + "CLR=" + Describe(() => Invoke(storage, "GetFieldType", index))
                + " Server=" + Describe(() => Invoke(storage, "GetServerDataTypeName", index))
                + " Formatted=" + Describe(() => Invoke(storage, "GetFormattedDataTypeName", index))
                + " XML=" + Describe(() => Invoke(storage, "IsXMLColumn", index))
                + " JSON=" + Describe(() => Invoke(storage, "IsJsonColumn", index))
                + " 有 SchemaRow=" + Describe(() => Invoke(storage, "GetSchemaRow", index) is not null));
        }
    }

    /// <summary>
    /// 取樣幾格，證明「拿得到值，而且分得出 NULL」。
    /// </summary>
    /// <remarks>
    /// 記的是型別、<c>IsCellDataNull</c> 的答案、字串化後的長度，以及字串化的結果
    /// 是不是剛好等於 <c>NULL</c> 這四個字——最後這一項就是剪貼簿路線分不出來、
    /// 而這條路線分得出來的那一組。
    ///
    /// <b>欄索引用的是格線的基準，不是資料的基準。</b>同一個 <c>QEResultSet</c> 上
    /// 有兩套：<c>GetFieldType</c> 那一族吃 0 起算的資料欄，
    /// <c>GetCellData</c> 這一族吃格線欄——第 0 欄是列號欄，資料從 1 開始。
    /// 第一版探測假設兩邊一致，結果第 0 欄三個呼叫全部
    /// <c>ArgumentOutOfRangeException</c>。那次是撞到邊界才炸的；欄位再多一個，
    /// 第 0 欄就會安靜地回傳列號，後面整份錯開一欄而每一格都還「有值」。
    ///
    /// 一律從第 0 列、第 1 欄開始掃，不跟著使用者的選取跑：探測要看的是
    /// 有沒有 NULL 這種特殊值，而那不一定落在選取範圍裡。
    /// </remarks>
    private static void SampleCells(
        List<string> report,
        object storage,
        long? totalRows,
        int? dataColumns)
    {
        if (totalRows is not long rows || rows <= 0 || dataColumns is not int columns || columns <= 0)
        {
            report.Add("取樣：沒有資料列可取樣。");
            return;
        }

        report.Add("取樣（欄索引為格線基準，1 起算；括號內是對應的資料欄）：");

        var sampled = 0;

        for (var row = 0L; row < rows && sampled < MaxSampledCells; row++)
        {
            for (var column = 1; column <= columns && sampled < MaxSampledCells; column++)
            {
                sampled++;
                var r = row;
                var c = column;

                report.Add(
                    "  列 " + r + " 格線欄 " + c + "（資料欄 " + (c - 1) + "）："
                    + "GetCellData=" + DescribeCellValue(() => Invoke(storage, "GetCellData", r, c))
                    + " IsCellDataNull=" + Describe(() => Invoke(storage, "IsCellDataNull", r, c))
                    + " AsString: " + Describe(() =>
                    {
                        var text = Invoke(storage, "GetCellDataAsString", r, c) as string;

                        return text is null
                            ? "(null)"
                            : "長度=" + text.Length
                                + ", 是否等於字面 NULL="
                                + string.Equals(text, "NULL", StringComparison.Ordinal);
                    }));
            }
        }
    }

    private static int CountOf(IEnumerable items)
    {
        var count = 0;

        foreach (var item in items)
        {
            _ = item;
            count++;
        }

        return count;
    }

    private static object? Get(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName, Any)?.GetValue(instance);

    private static object? Invoke(object instance, string methodName, params object[] arguments)
    {
        var types = Array.ConvertAll(arguments, a => a.GetType());
        var method = instance.GetType().GetMethod(methodName, Any, binder: null, types, modifiers: null)
            ?? throw new MissingMethodException(instance.GetType().FullName, methodName);

        return method.Invoke(instance, arguments);
    }

    /// <summary>
    /// 儲存格的值<b>只記型別，永遠不記內容</b>。
    /// </summary>
    /// <remarks>
    /// 這份報告會被貼進工單與對話裡，而結果格線的每一格都可能是真實的個資或客戶
    /// 資料。第一版用的是下面那個通用的 <see cref="Describe"/>，它的預設分支會把
    /// 值本身印出來——實測時就把一個人的姓名、證號與生日寫進了紀錄檔。
    ///
    /// 探測要回答的問題只有「拿不拿得到值、分不分得出 NULL」，那不需要看見任何
    /// 一筆內容。<c>SqlTypes</c> 的值另外報 <c>IsNull</c>，因為
    /// <c>SqlString.Null</c> 與有值的 <c>SqlString</c> 是同一個型別。
    /// </remarks>
    private static string DescribeCellValue(Func<object?> work)
    {
        try
        {
            var value = work();

            return value switch
            {
                null => "(null)",
                DBNull => "DBNull",
                INullable nullable => value.GetType().Name + (nullable.IsNull ? "(Null)" : "(有值)"),
                _ => value.GetType().Name,
            };
        }
        catch (Exception exception)
        {
            var actual = Unwrap(exception);
            return "!!" + actual.GetType().Name + ": " + actual.Message;
        }
    }

    /// <summary>
    /// 把一次呼叫變成一段記得下來的文字；失敗也是結果，照樣記。
    /// </summary>
    /// <remarks>
    /// 預設分支只印型別名稱，不印值。這裡經手的都是結構描述層的答案
    /// （型別名稱、布林旗標），本來就不該出現任何一筆資料——但預設分支印出值
    /// 的話，下一次有人拿它包一個新的呼叫就會安靜地把資料寫進紀錄檔。
    /// 儲存格的值走 <see cref="DescribeCellValue"/>。
    /// </remarks>
    private static string Describe(Func<object?> work)
    {
        try
        {
            var value = work();

            return value switch
            {
                null => "(null)",
                DBNull => "DBNull",
                bool flag => flag.ToString(),
                string text => text,
                Type type => type.Name,
                _ => value.GetType().Name,
            };
        }
        catch (Exception exception)
        {
            var actual = Unwrap(exception);
            return "!!" + actual.GetType().Name + ": " + actual.Message;
        }
    }

    private static T? Step<T>(List<string> report, string label, Func<T?> work)
    {
        try
        {
            var value = work();
            report.Add(label + "：" + (value?.ToString() ?? "(null)"));
            return value;
        }
        catch (Exception exception)
        {
            var actual = Unwrap(exception);
            report.Add(label + "：!! " + actual.GetType().Name + ": " + actual.Message);
            return default;
        }
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
}
