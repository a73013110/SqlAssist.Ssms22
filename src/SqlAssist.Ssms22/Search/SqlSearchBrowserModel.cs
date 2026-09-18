using System;
using System.Collections.Generic;
using System.Globalization;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>過濾列上的一顆分類 pill；「全部」的 <see cref="Id"/> 是 null。</summary>
/// <remarks>
/// 清單由 <see cref="SearchAggregator.Categories"/> 產生，不在 UI 寫死任何 <c>catalog.*</c> 字串：
/// 寫死的症狀是之後加一個 provider，它宣告的分類在過濾列上一顆都沒有，而結果照樣出現在清單裡。
/// </remarks>
internal sealed class SqlSearchCategoryOption
{
    internal SqlSearchCategoryOption(string? id, string label)
    {
        Id = id;
        Label = label;
    }

    /// <summary>對應 <see cref="SearchCategory.Id"/>；null 表示不過濾。</summary>
    public string? Id { get; }

    public string Label { get; }
}

/// <summary>頁尾那一行現在在說哪一件事；動畫用它判斷「同一狀態不重播」。</summary>
internal enum SqlSearchStatusTone
{
    /// <summary>沒有東西要說；頁尾收起。</summary>
    None,

    /// <summary>掃完了，回報筆數。</summary>
    Result,

    /// <summary>沒掃完；筆數之外還要說清楚這一份不完整。</summary>
    Partial,

    /// <summary>有來源失敗。</summary>
    Failure,
}

/// <summary>一輪搜尋的身分證：世代、查詢，以及這一輪會不會先卡在建索引上。</summary>
internal sealed class SqlSearchRound
{
    internal SqlSearchRound(long generation, SearchQuery query, bool needsIndex)
    {
        Generation = generation;
        Query = query;
        NeedsIndex = needsIndex;
    }

    public long Generation { get; }

    public SearchQuery Query { get; }

    /// <summary>這一輪的目標資料庫還沒有索引，可能要全表掃描一次；載入表面靠它決定要不要出現。</summary>
    public bool NeedsIndex { get; }
}

/// <summary>
/// SQL Search 工具窗的純邏輯：輸入轉查詢、世代作廢、狀態與空狀態文字，以及重新整理後的選取還原。
/// </summary>
/// <remarks>
/// 只在 UI 執行緒使用，不做 I/O，也刻意不碰 WPF 型別——它與 <see cref="SqlSearchBrowser"/> 的分工
/// 和 SQL Memory 那一對相同：控制項的值寫進來，回應交回來由這裡決定要不要採用。
///
/// 取消撤不回已經派送出去的那一輪，所以晚到的回應一律以世代過濾；
/// <see cref="SearchResults.IsStale"/> 的結果直接丟棄，而且<b>不清空清單</b>——
/// 每打一個字先清一次的症狀是清單一路閃白，而上一份結果其實還讀得懂。
/// </remarks>
internal sealed class SqlSearchBrowserModel
{
    /// <summary>「全部」pill 的顯示字；它不是任何一個 provider 宣告的分類。</summary>
    public const string AllCategoriesLabel = "全部";

    private long _generation;

    /// <summary>目前在跑的那一輪；-1 表示沒有。</summary>
    private long _running = -1;

    private bool _pending;
    private bool _hasResult;
    private bool _isPartial;
    private int _hitCount;
    private string _failure = "";
    private string? _restoreKey;

    /// <summary>使用者打進去的原文；空字串是「還沒開始搜尋」，不是「搜尋空字串」。</summary>
    public string Text { get; set; } = "";

    public bool MatchCasing { get; set; }

    public bool WholeWord { get; set; }

    /// <summary>指名的資料庫；null 表示跟著目前查詢視窗那一個。</summary>
    /// <remarks>
    /// 預設 null 而不是列出所有進得去的資料庫：每指名一個就是一次含定義本文的全表掃描，
    /// 預先索引全部是明文禁止的。
    /// </remarks>
    public string? Database { get; set; }

    /// <summary>目前選的分類；null 表示不過濾。</summary>
    public string? CategoryId { get; set; }

    /// <summary>目前有沒有可以搜的連線；沒有時整輪不開始，畫面走「尚未連線」的空狀態。</summary>
    public bool HasConnection { get; set; }

    /// <summary>這一輪要搜的範圍；呼叫端用它先問「索引建好了沒」，再決定要不要顯示載入表面。</summary>
    public SearchScope Scope => BuildScope();

    /// <summary>目前這一輪的世代；每一次新輸入加一。</summary>
    public long Generation => _generation;

    /// <summary>去彈跳計時器還沒到期：已經有新輸入，但這一輪還沒送出去。</summary>
    public bool IsPending => _pending;

    public bool IsRunning => _running >= 0;

    /// <summary>目前這一輪要先建索引；載入表面出現，而不是讓視窗看起來當掉。</summary>
    public bool IsIndexing { get; private set; }

    /// <summary>
    /// 過濾列要畫哪幾顆 pill。
    /// </summary>
    /// <remarks>
    /// 依 provider 宣告順序串接並以 Id 去重：兩個 provider 各自宣告同一個 Id 時，畫兩顆一模一樣的 pill
    /// 只會讓使用者以為它們是兩種東西。顯示字取先出現的那一份。
    /// </remarks>
    public static IReadOnlyList<SqlSearchCategoryOption> CategoryOptions(IEnumerable<SearchCategory>? categories)
    {
        var options = new List<SqlSearchCategoryOption> { new(null, AllCategoriesLabel) };

        if (categories is null) return options;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            if (category is null) continue;
            if (seen.Add(category.Id)) options.Add(new SqlSearchCategoryOption(category.Id, category.DisplayName));
        }

        return options;
    }

    /// <summary>輸入或篩選改變：這一份結果已經不代表畫面上的條件，但清單留著等新結果。</summary>
    public void Invalidate()
    {
        _pending = true;
        _hasResult = false;
        _failure = "";
    }

    /// <summary>
    /// 開始下一輪。
    /// </summary>
    /// <param name="indexed">目標資料庫已經有索引；false 表示這一輪可能要先掃一次全表。</param>
    /// <returns>沒有連線或還沒輸入時 null，呼叫端不送出任何查詢，並把清單清掉。</returns>
    /// <remarks>
    /// 空輸入刻意不開一輪。provider 對空樣式會列一份預設清單，但那一輪仍要先把整個資料庫
    /// （含定義本文）索引一次——使用者只是打開了視窗，還沒有說要搜什麼，而那一次掃描以秒計。
    /// </remarks>
    public SqlSearchRound? Begin(bool indexed)
    {
        _pending = false;

        if (!HasConnection || Text.Length == 0)
        {
            _running = -1;
            IsIndexing = false;
            _hasResult = false;
            return null;
        }

        var generation = ++_generation;
        _running = generation;
        IsIndexing = !indexed;

        return new SqlSearchRound(
            generation,
            new SearchQuery(Text, generation, BuildOptions(), BuildCategories(), BuildScope()),
            !indexed);
    }

    /// <summary>這一輪還是畫面上的那一輪；失敗訊息與結果都要先問過它。</summary>
    public bool IsCurrent(SqlSearchRound round)
    {
        if (round is null) throw new ArgumentNullException(nameof(round));
        return round.Generation == _generation && HasConnection;
    }

    /// <summary>
    /// 採用一輪結果。
    /// </summary>
    /// <returns>false 表示這一份已經過期，呼叫端一列都不要動。</returns>
    public bool Accept(SqlSearchRound round, SearchResults results)
    {
        if (round is null) throw new ArgumentNullException(nameof(round));
        if (results is null) throw new ArgumentNullException(nameof(results));

        // IsStale 與「什麼都沒找到」是兩件事：過期的維持上一份清單，等新的那一輪回來。
        if (!IsCurrent(round) || results.IsStale) return false;

        _hasResult = true;
        _isPartial = results.IsPartial;
        _hitCount = results.Hits.Count;
        _failure = Describe(results.Failures);
        return true;
    }

    /// <summary>這一輪整個失敗了（例外冒到聚合器外面）；舊查詢的失敗不得蓋掉新的狀態。</summary>
    public void Fail(SqlSearchRound round, string message)
    {
        if (round is null) throw new ArgumentNullException(nameof(round));
        if (!IsCurrent(round)) return;

        _hasResult = true;
        _isPartial = true;
        _hitCount = 0;
        _failure = message ?? "";
    }

    /// <summary>這一輪結束（成功、失敗或放棄）；只放開同一世代的旗標。</summary>
    public void End(SqlSearchRound round)
    {
        if (round is null) throw new ArgumentNullException(nameof(round));
        if (_running != round.Generation) return;

        _running = -1;
        IsIndexing = false;
    }

    /// <summary>頁尾現在在說哪一件事；換了一種說法才播一次狀態回饋。</summary>
    public SqlSearchStatusTone Tone
    {
        get
        {
            if (_failure.Length != 0) return SqlSearchStatusTone.Failure;
            if (!HasConnection || !_hasResult || _hitCount == 0) return SqlSearchStatusTone.None;
            return _isPartial ? SqlSearchStatusTone.Partial : SqlSearchStatusTone.Result;
        }
    }

    /// <summary>頁尾那一行；平時留空，只回報結果數、部分結果與失敗。</summary>
    public string Status()
    {
        if (_failure.Length != 0) return _failure;
        if (!HasConnection || !_hasResult || _hitCount == 0) return "";

        var count = _hitCount.ToString(CultureInfo.InvariantCulture);

        // 部分結果一定要說：與「這個字串在這個資料庫裡不存在」在畫面上一模一樣。
        return _isPartial
            ? "找到 " + count + " 項（部分結果；縮小範圍或加長關鍵字可以掃得更完整）"
            : "找到 " + count + " 項";
    }

    /// <summary>
    /// 主內容區的空狀態；沒有東西可說時回空字串。
    /// </summary>
    /// <param name="rowCount">目前清單的列數。</param>
    /// <remarks>
    /// 沒有連線是明確的一句話，不是空白也不是錯誤：使用者要知道下一步是去連線，而不是換關鍵字。
    /// 正在跑的那一輪交給載入表面，這裡留空，否則會同時出現兩種「等一下」。
    /// </remarks>
    public string EmptyState(int rowCount)
    {
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (!HasConnection) return "尚未連線。在 SQL 查詢視窗連上資料庫之後，這裡才有東西可以搜。";
        if (rowCount > 0 || IsRunning) return "";
        if (Text.Length == 0) return "輸入關鍵字，搜尋這個資料庫的物件名稱、資料行與定義本文。";
        return _hasResult && !_pending ? "沒有相符項目。" : "";
    }

    /// <summary>載入表面要不要出現：第一次建索引，或這一輪還沒有任何一列可看。</summary>
    public bool ShowLoading(int rowCount)
    {
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        return IsRunning && (IsIndexing || rowCount == 0);
    }

    /// <summary>重新整理前記下目前選取；新結果載入後若還在，就選回它。</summary>
    public void RememberSelection(string? key) => _restoreKey = key;

    /// <summary>採用新結果之後要選取哪一列；null 表示維持現狀。</summary>
    /// <param name="keys">目前清單全部列的識別字，依顯示順序。</param>
    /// <param name="hasSelection">清單目前是否已有選取。</param>
    public int? ResolveSelection(IReadOnlyList<string> keys, bool hasSelection)
    {
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        var restore = _restoreKey;
        _restoreKey = null;

        if (restore is not null)
        {
            for (var index = 0; index < keys.Count; index++)
            {
                if (string.Equals(keys[index], restore, StringComparison.Ordinal)) return index;
            }
        }

        // 沒有要還原的列時預覽第一筆，但不搶已有的選取。
        return !hasSelection && keys.Count > 0 ? 0 : null;
    }

    private SearchOptions BuildOptions()
    {
        var options = SearchOptions.None;
        if (MatchCasing) options |= SearchOptions.MatchCasing;
        if (WholeWord) options |= SearchOptions.WholeWord;
        return options;
    }

    private IEnumerable<string>? BuildCategories() =>
        CategoryId is { Length: > 0 } category ? new[] { category } : null;

    /// <remarks>
    /// 伺服器那一份永遠是空的：v1 沒有連結伺服器的索引，指名伺服器等於整輪不回結果。
    /// 範圍的伺服器由目前這條連線決定，UI 只把它顯示出來。
    /// </remarks>
    private SearchScope BuildScope() =>
        Database is { Length: > 0 } database ? new SearchScope(null, new[] { database }) : SearchScope.All;

    private static string Describe(IReadOnlyList<SearchProviderFailure> failures)
    {
        if (failures.Count == 0) return "";

        // 只寫第一個來源的訊息加上還有幾個：一行狀態塞不下三段堆疊，而第一句已經說得出是哪一類失敗。
        var first = failures[0];

        return failures.Count == 1
            ? "「" + first.ProviderId + "」這一輪失敗：" + first.Message
            : "「" + first.ProviderId + "」等 " + failures.Count.ToString(CultureInfo.InvariantCulture) +
              " 個來源這一輪失敗：" + first.Message;
    }
}
