using System;

namespace SqlAssist.Ssms22.UI;

/// <summary>去彈跳延遲的唯一出處。</summary>
/// <remarks>
/// 四個數字散在各自的檔案時，沒有人比得出它們的相對關係——而相對關係就是這一組數字
/// 的全部意義：打字驅動的搜尋要最短，選取驅動的預覽可以再等一下，估算與跨檔搜尋最慢。
/// 分開放的症狀是有人把預覽調成 120 ms，而它比搜尋還積極，方向鍵一路按下去就每一列
/// 都發一輪查詢。
/// </remarks>
internal static partial class SqlAssistChrome
{
    internal static class Debounce
    {
        /// <summary>SQL Search 的打字去彈跳；短到放開鍵就開始搜，長到連打不會每個字元一輪。</summary>
        public static readonly TimeSpan Search = TimeSpan.FromMilliseconds(200);

        /// <summary>主從區預覽的選取去彈跳；SQL Memory 與 SQL Search 同一個數字。</summary>
        public static readonly TimeSpan Preview = TimeSpan.FromMilliseconds(220);

        /// <summary>清理對話框的影響筆數估算；每一次估算都是一輪查詢，比預覽再寬一點。</summary>
        public static readonly TimeSpan CleanupEstimate = TimeSpan.FromMilliseconds(250);

        /// <summary>SQL Memory 清單的搜尋去彈跳；比對的是 SQL 全文，比物件名稱貴。</summary>
        public static readonly TimeSpan MemorySearch = TimeSpan.FromMilliseconds(300);
    }
}
