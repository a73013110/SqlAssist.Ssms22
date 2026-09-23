using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using SqlAssist.Core.Tabular;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 表格內容寫進剪貼簿：同一個 <see cref="DataObject"/> 放 UnicodeText（TSV）與 HTML 兩種格式。
/// </summary>
internal static class SqlClipboard
{
    /// <summary>剪貼簿被別的程式開著時再試幾次。</summary>
    internal const int Attempts = 3;

    /// <summary>兩次之間等多久；非同步等，不卡住 UI 執行緒。</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>剪貼簿一直打不開時的訊息；工具窗的狀態列與測試共用。</summary>
    internal const string BusyMessage = "剪貼簿正被其他程式使用，未複製；請稍後再試。";

    /// <summary>勾起來的列都已不在（例如剛被刪除）時的訊息。</summary>
    internal const string EmptyMessage = "沒有可複製的項目。";

    /// <summary>表格複製成功的訊息；SQL Memory 與 SQL Search 同一句。</summary>
    public static string CopiedMessage(int rows) =>
        "已複製 " + rows.ToString("N0", CultureInfo.CurrentCulture) + " 筆（含標頭列，可直接貼到 Excel）。";

    /// <remarks>
    /// 兩種格式在同一個物件裡：貼的地方自己挑讀得懂的那一種。分兩次寫的話，第二次會把第一次整個換掉。
    /// </remarks>
    public static DataObject CreateDataObject(SqlTabularContent content)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, content.Tsv);
        // WPF 把 HTML 格式的字串以 UTF-8 寫進剪貼簿，與 CF_HTML 標頭裡的位元組位移同一種單位。
        data.SetData(DataFormats.Html, content.Html);
        return data;
    }

    /// <summary>
    /// 寫進剪貼簿；被別的程式鎖住時非同步重試，仍然不行就回傳失敗訊息。
    /// </summary>
    /// <remarks>
    /// 這是「有例外篩選的預期失敗」，所以在這裡接 <see cref="ExternalException"/>（<c>COMException</c> 的基底，
    /// <c>CLIPBRD_E_CANT_OPEN</c> 就是它），不走平台 Guard：剪貼簿被遠端桌面或剪貼簿管理員短暫開著
    /// 是常態，使用者按了複製就要看得到「沒有複製成功」，而不是擴充當掉或什麼都沒發生。
    /// 其他例外照樣往上丟，由呼叫端的使用者動作邊界回報。
    /// </remarks>
    /// <param name="write">實際寫入；測試換掉它，不去動真正的系統剪貼簿。</param>
    /// <returns>null 表示成功；否則是要顯示給使用者的訊息。</returns>
    public static async Task<string?> WriteAsync(IDataObject data, Action<IDataObject>? write = null)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        write ??= value => Clipboard.SetDataObject(value, copy: true);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write(data);
                return null;
            }
            catch (ExternalException) when (attempt < Attempts)
            {
            }
            catch (ExternalException)
            {
                return BusyMessage;
            }

            await Task.Delay(RetryDelay).ConfigureAwait(true);
        }
    }
}
