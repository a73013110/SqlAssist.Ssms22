using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>一次取回的結果：指令碼，或是取不到時要說的那一句。</summary>
internal readonly struct SqlSearchDefinitionText
{
    internal SqlSearchDefinitionText(string script, string? failure)
    {
        Script = script;
        Failure = failure;
    }

    /// <summary>完整定義；取不到時是空字串。</summary>
    internal string Script { get; }

    /// <summary>取不到的原因；成功時為 null。<b>不會</b>是空白——靜默的空白面板等於故障。</summary>
    internal string? Failure { get; }
}

/// <summary>
/// 選取過的定義；同一個物件重選不重查。
/// </summary>
/// <remarks>
/// 存的是<b>組好的指令碼</b>而不是結構：中繼資料那一層本來就有第四層的快取，但重排一次
/// 要重跑結構健檢，而使用者只是用方向鍵在兩列之間來回。
///
/// 只在 UI 執行緒上讀寫，所以沒有鎖。上限是兩道——筆數擋住「一直往下捲」，字元數擋住
/// 「其中一份是一萬行的預存程序」；少了後者，二十四份大定義就是幾十 MB 常駐在工具窗上。
/// </remarks>
internal sealed class SqlSearchDefinitionCache
{
    /// <summary>最多記幾份定義。</summary>
    internal const int MaximumEntries = 24;

    /// <summary>全部加起來最多幾個字元（約 4 MB）。</summary>
    internal const int MaximumCharacters = 2_000_000;

    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);

    /// <summary>最近用過的排最前面；滿了從最後面丟。</summary>
    private readonly LinkedList<Entry> _order = new();

    private int _characters;

    internal int Count => _index.Count;

    internal bool TryGet(string key, out string script)
    {
        if (!_index.TryGetValue(key, out var node))
        {
            script = "";
            return false;
        }

        _order.Remove(node);
        _order.AddFirst(node);
        script = node.Value.Script;
        return true;
    }

    /// <remarks>
    /// 單獨一份就超過上限的不收：收了會把其餘全部擠掉，然後自己也留不住，
    /// 等於每選一次就把整份快取清空一次。
    /// </remarks>
    internal void Add(string key, string script)
    {
        if (_index.ContainsKey(key) || script.Length > MaximumCharacters) return;

        _order.AddFirst(new Entry(key, script));
        _index[key] = _order.First!;
        _characters += script.Length;

        while (_index.Count > MaximumEntries || _characters > MaximumCharacters)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _index.Remove(last.Value.Key);
            _characters -= last.Value.Script.Length;
        }
    }

    /// <summary>連線換了或使用者按了重新整理；記著的那幾份可能已經不是現在這台伺服器的。</summary>
    internal void Clear()
    {
        _index.Clear();
        _order.Clear();
        _characters = 0;
    }

    private readonly struct Entry
    {
        internal Entry(string key, string script)
        {
            Key = key;
            Script = script;
        }

        internal string Key { get; }

        internal string Script { get; }
    }
}

/// <summary>
/// 把一列結果的每一處命中對到完整定義裡的位置。
/// </summary>
/// <remarks>
/// 換算本身在 <see cref="MatchProjection"/>；這裡只決定「拿哪幾段去找、從哪裡開始找」，
/// 而那兩件事要認得 SQL 才答得出來。
///
/// 找的是<b>每一次</b>出現而不是第一次：一個資料行名稱在 <c>CREATE TABLE</c> 裡出現一次，
/// 在後面那一串 <c>sp_addextendedproperty</c> 裡還會再出現一次，而「上一個／下一個命中」
/// 要走得過去。聚合器併成一列的那幾筆全部算進來（<see cref="SearchHit.Matches"/>），
/// 否則一張靠三個資料行命中的表只標得出其中一行。
///
/// <b>對不上就不高亮。</b>畫錯位置的高亮看起來像是比對錯了，而使用者會開始懷疑整份清單。
/// </remarks>
internal static class SqlSearchDefinitionHighlight
{
    /// <summary>這一列所有命中在 <paramref name="script"/> 上的標記；對不上時是空的。</summary>
    /// <remarks>
    /// 這裡只決定「拿哪幾段去找」；併段、上限與少標的那一句在 <see cref="MatchHighlights"/>，
    /// 與 SQL Memory 預覽同一份。
    /// </remarks>
    internal static MatchHighlightSet Locate(SearchHit hit, string script)
    {
        if (hit is null) throw new ArgumentNullException(nameof(hit));
        if (script.Length == 0) return MatchHighlightSet.Empty;

        var found = new List<MatchSpan>();

        foreach (var match in hit.Matches)
        {
            if (match.Snippet.Length == 0 || match.SnippetSpans.Count == 0) continue;
            Collect(match, script, found);
        }

        // 重疊會發生在兩個命中指向同一塊文字的時候（打 Due，而這張表上同時有 Due 與 DueDate 兩行）；
        // 併成一段而不是丟掉其中一個：那塊文字確實被標出來了，算一處。
        return MatchHighlights.Merge(found);
    }

    private static void Collect(SearchHit hit, string script, List<MatchSpan> found)
    {
        // 三種命中的片段都是「區段索引落在它上面」的那一段文字：名稱命中是名稱本體，
        // 資料行命中是資料行名稱，本文命中是定義裡的那一行。拿片段去找，換算就一定對得起來。
        var identifier = hit.MatchTarget != SearchMatchTarget.Text;

        // 識別字不分大小寫（provider 那一端也是），而且要整個字：搜 No 不該高亮 CopyNo
        // 裡面那兩個字。方括號算詞界，所以同一段程式碼認得 [CopyNo] 與 CopyNo 兩種寫法。
        var matcher = new TextMatcher(
            hit.Snippet, identifier ? TextMatchOptions.WholeWord : TextMatchOptions.MatchCasing);

        // 檔頭註解裡也有物件名稱，而使用者要看的是 CREATE 那一行；先跳過開頭的註解再找。
        // 本文命中不跳：模組的定義本身就可能以註解開頭，跳過去會連命中那一行一起錯過。
        var start = identifier ? SqlTrivia.Skip(script, 0, script.Length) : 0;
        var offsets = matcher.FindAll(script, start);

        // 跳過檔頭之後一處都沒有，就整份再找一次：資料行也可能只出現在檔頭的摘要裡。
        if (offsets.Count == 0 && start > 0) offsets = matcher.FindAll(script);

        foreach (var offset in offsets)
        {
            found.AddRange(MatchProjection.Shift(hit.SnippetSpans, offset, hit.Snippet.Length, script.Length));
        }
    }
}
