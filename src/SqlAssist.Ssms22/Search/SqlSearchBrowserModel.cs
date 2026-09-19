using System;
using System.Collections.Generic;
using System.Globalization;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>過濾下拉裡的一種物件種類。</summary>
/// <remarks>
/// 清單由 <see cref="SearchAggregator.Categories"/> 產生，不在 UI 寫死任何 <c>catalog.*</c> 字串：
/// 寫死的症狀是之後加一個 provider，它宣告的分類在過濾清單上一項都沒有，而結果照樣出現在清單裡。
///
/// 沒有「全部」這一項：多選的空集合<b>就是</b>全部，而多一顆與其他選項互斥的「全部」等於
/// 在同一份清單裡放兩種語意，使用者勾了第二種之後看不出第一種還算不算數。
/// </remarks>
internal sealed class SqlSearchCategoryOption
{
    internal SqlSearchCategoryOption(string id, string label)
    {
        Id = id;
        Label = label;
    }

    /// <summary>對應 <see cref="SearchCategory.Id"/>。</summary>
    public string Id { get; }

    public string Label { get; }
}

/// <summary>結果清單的排序鍵。</summary>
/// <remarks>
/// 取代原本的上下分組。分組把「名稱命中」與「定義本文命中」切成兩疊，使用者要找的那一筆
/// 可能在第二疊的底下；每一列掛一個命中部位徽章就分得出來，而排序才是他真正要換的東西。
/// </remarks>
internal enum SqlSearchSort
{
    /// <summary>聚合器排好的順序：命中部位的分組先後，再依分數。</summary>
    Relevance,

    /// <summary>限定名稱 A–Z。</summary>
    Name,

    /// <summary>物件種類，種類內再依限定名稱。</summary>
    Kind
}

/// <summary>排序選項的顯示字；按鈕與選單共用同一份，兩邊不會各自寫一次。</summary>
internal sealed class SqlSearchSortOption
{
    private SqlSearchSortOption(SqlSearchSort value, string label, string shortLabel)
    {
        Value = value;
        Label = label;
        ShortLabel = shortLabel;
    }

    public static IReadOnlyList<SqlSearchSortOption> All { get; } = new[]
    {
        new SqlSearchSortOption(SqlSearchSort.Relevance, "相關度", "相關度"),
        new SqlSearchSortOption(SqlSearchSort.Name, "名稱 A–Z", "名稱"),
        new SqlSearchSortOption(SqlSearchSort.Kind, "物件種類", "種類")
    };

    public SqlSearchSort Value { get; }

    public string Label { get; }

    /// <summary>按鈕上的字；工具列放不下完整說明。</summary>
    public string ShortLabel { get; }

    public static SqlSearchSortOption For(SqlSearchSort value)
    {
        foreach (var option in All) if (option.Value == value) return option;
        throw new ArgumentOutOfRangeException(nameof(value), value, "沒有這個排序。");
    }
}

/// <summary>已選條件那一列上的一顆 chip；按下十字就清掉它代表的那一個條件。</summary>
internal sealed class SqlSearchFilterChip
{
    internal SqlSearchFilterChip(SqlSearchFilterKind kind, string? value, string label)
    {
        Kind = kind;
        Value = value;
        Label = label;
    }

    public SqlSearchFilterKind Kind { get; }

    /// <summary>要清掉的那一個值；選項類（大小寫、全字）沒有值。</summary>
    public string? Value { get; }

    /// <summary>chip 上的字。</summary>
    public string Label { get; }
}

/// <summary>chip 代表哪一種條件；清掉時據此決定動哪一份集合。</summary>
internal enum SqlSearchFilterKind
{
    Category,
    Server,
    Database,
    MatchCasing,
    WholeWord
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
/// SQL Search 工具窗的純邏輯：輸入轉查詢、世代作廢、篩選摘要、排序、狀態與空狀態文字，
/// 以及重新整理後的選取還原。
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
    /// <summary>沒有勾任何一個分類時，按鈕上顯示的字。</summary>
    public const string AllCategoriesLabel = "全部";

    /// <summary>沒有指名資料庫時，按鈕上顯示的字。</summary>
    public const string CurrentConnectionLabel = "目前連線";

    /// <summary>沒有指名伺服器時，伺服器按鈕上顯示的字。</summary>
    /// <remarks>
    /// 與 <see cref="CurrentConnectionLabel"/> 分開：資料庫那一顆說的是「跟著這條連線的
    /// 資料庫」，伺服器這一顆說的是「跟著作用中的那個查詢視窗」——切換分頁會換一台，
    /// 而兩句話寫成同一句的話，使用者分不出哪一顆在跟著誰走。
    /// </remarks>
    public const string ActiveEditorServerLabel = "查詢視窗";

    /// <summary>
    /// 系統資料庫的名稱；下拉清單把它們與使用者資料庫分成兩段。
    /// </summary>
    /// <remarks>
    /// 名單寫在 UI 這一層而不是向伺服器問 <c>database_id &lt;= 4</c>：為了替一個下拉分段
    /// 而多送一輪查詢，等於在使用者沒有要求的時候連資料庫。這四個名稱在每一版 SQL Server
    /// 上都相同，分錯的代價也只是一個名字排在另一段裡。
    /// </remarks>
    private static readonly HashSet<string> SystemDatabaseNames =
        new(new[] { "master", "model", "msdb", "tempdb" }, StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _categoryIds = new(StringComparer.Ordinal);
    private readonly List<string> _databases = new();
    /// <summary>有來源這一輪整個讀不到時要補的那一句；沒有時是空字串。</summary>
    private string _unavailable = "";

    private IReadOnlyList<SqlSearchCategoryOption> _categories = Array.Empty<SqlSearchCategoryOption>();
    private Dictionary<string, int> _categoryOrder = new(StringComparer.Ordinal);

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

    /// <summary>
    /// 這一輪要比對物件的哪幾個部位。
    /// </summary>
    /// <remarks>
    /// 這是使用者切換最頻繁的一項，所以在工具列上是常駐的分段控制器而不是下拉：藏進下拉
    /// 會讓每一次切換多兩次點擊。值一路傳到 provider 的最內層迴圈——少掉
    /// <see cref="SearchTargets.Text"/> 的那一輪是<b>真的不去撈定義本文</b>，不是掃回來再丟。
    /// </remarks>
    public SearchTargets Targets { get; set; } = SearchTargets.All;

    /// <summary>
    /// 「怎麼比對」那三項收成一個字串，交給狀態存放區跨工作階段記住。
    /// </summary>
    /// <remarks>
    /// 一個字串而不是三個狀態項：三項各記一次的話，只有其中一項寫成功的那一次會半套還原，
    /// 而畫面上分不出是記壞了還是使用者上次真的這樣設。格式是
    /// <c>比對位置|大小寫|全字</c>，三段都是十進位整數。
    ///
    /// 記住的只有這三項。伺服器與資料庫綁在一條連線上，種類是一次調查裡的收斂，
    /// 兩者都不記——理由見 docs/search.md。
    /// </remarks>
    public string MatchStateToken =>
        ((int)Targets).ToString(CultureInfo.InvariantCulture) + "|" +
        (MatchCasing ? "1" : "0") + "|" + (WholeWord ? "1" : "0");

    /// <summary>
    /// 套回上一次記住的那三項。
    /// </summary>
    /// <returns>true 表示真的套用了，呼叫端要把控制項同步過去。</returns>
    /// <remarks>
    /// 認不得時<b>整組</b>維持預設，不逐項盡量還原：半套的狀態與「使用者上次真的這樣設」
    /// 在畫面上一模一樣，而預設值至少是一個說得出來的起點。
    /// 一個部位都不掃的比對位置也算認不得——那一輪找不到任何東西，
    /// 而畫面上與「這個字串不存在」相同。
    /// </remarks>
    public bool RestoreMatchState(string? token)
    {
        if (token is null) return false;

        var parts = token.Split('|');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var targets)) return false;
        if (targets == 0 || (targets & ~(int)SearchTargets.All) != 0) return false;
        if (!TryReadFlag(parts[1], out var casing) || !TryReadFlag(parts[2], out var wholeWord)) return false;

        Targets = (SearchTargets)targets;
        MatchCasing = casing;
        WholeWord = wholeWord;
        return true;
    }

    /// <remarks>「不是 1 就當成 false」會把記壞的字串讀成一個看起來正常的狀態。</remarks>
    private static bool TryReadFlag(string value, out bool flag)
    {
        flag = string.Equals(value, "1", StringComparison.Ordinal);
        return flag || string.Equals(value, "0", StringComparison.Ordinal);
    }

    /// <summary>結果清單的排序；取代原本的上下分組。</summary>
    public SqlSearchSort Sort { get; set; } = SqlSearchSort.Relevance;

    /// <summary>目前有沒有可以搜的連線；沒有時整輪不開始，畫面走「尚未連線」的空狀態。</summary>
    public bool HasConnection { get; set; }

    /// <summary>
    /// 指名的伺服器顯示名稱；null 表示跟著作用中的查詢視窗。
    /// </summary>
    /// <remarks>
    /// 只存<b>名稱</b>，不存伺服器物件也不存連線：這一層是純邏輯，連線由
    /// <c>SqlSearchCatalogs</c> 負責，而名稱是摘要、chip 與空狀態唯一要用到的東西。
    /// 指名的伺服器<b>不</b>進 <see cref="SearchScope.Servers"/>——那一格是給連結伺服器
    /// （四段式名稱）的，而換一台物件總管上的伺服器換的是整份目錄。混用的症狀是
    /// provider 看到指名的伺服器就整輪不回結果。
    /// </remarks>
    public string? Server { get; set; }

    /// <summary>目前勾選的分類；空表示不過濾。</summary>
    public IReadOnlyCollection<string> CategoryIds => _categoryIds;

    /// <summary>
    /// 指名的資料庫；空表示跟著目前查詢視窗那一個。
    /// </summary>
    /// <remarks>
    /// 預設空而不是列出所有進得去的資料庫：每指名一個就是一次含定義本文的全表掃描，
    /// 預先索引全部是明文禁止的。
    /// </remarks>
    public IReadOnlyList<string> Databases => _databases;

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
    /// 過濾下拉要列哪幾個分類。
    /// </summary>
    /// <remarks>
    /// 依 provider 宣告順序串接並以 Id 去重：兩個 provider 各自宣告同一個 Id 時，列兩項一模一樣的
    /// 選項只會讓使用者以為它們是兩種東西。顯示字取先出現的那一份。
    /// </remarks>
    public static IReadOnlyList<SqlSearchCategoryOption> CategoryOptions(IEnumerable<SearchCategory>? categories)
    {
        var options = new List<SqlSearchCategoryOption>();

        if (categories is null) return options;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            if (category is null) continue;
            if (seen.Add(category.Id)) options.Add(new SqlSearchCategoryOption(category.Id, category.DisplayName));
        }

        return options;
    }

    /// <summary>接上這一份分類清單；摘要文字、chip 標籤與「依種類排序」的先後都由它決定。</summary>
    public void UseCategories(IReadOnlyList<SqlSearchCategoryOption> categories)
    {
        _categories = categories ?? throw new ArgumentNullException(nameof(categories));
        _categoryOrder = new Dictionary<string, int>(categories.Count, StringComparer.Ordinal);
        for (var index = 0; index < categories.Count; index++) _categoryOrder[categories[index].Id] = index;

        // 分類清單換掉時，勾在上面而現在已經不存在的那幾個要一起走：留著的話，
        // 每一輪都以一個沒有 provider 認領的 Id 過濾，結果永遠是空的而畫面上看不出為什麼。
        _categoryIds.RemoveWhere(id => !_categoryOrder.ContainsKey(id));
    }

    /// <summary>這個分類現在勾著沒有。</summary>
    public bool IsCategorySelected(string categoryId) => _categoryIds.Contains(categoryId);

    /// <returns>true 表示勾選集合真的變了，呼叫端才重跑一輪。</returns>
    public bool SetCategorySelected(string categoryId, bool selected)
    {
        if (categoryId is null) throw new ArgumentNullException(nameof(categoryId));
        return selected ? _categoryIds.Add(categoryId) : _categoryIds.Remove(categoryId);
    }

    public bool ClearCategories()
    {
        if (_categoryIds.Count == 0) return false;
        _categoryIds.Clear();
        return true;
    }

    public bool IsDatabaseSelected(string database) =>
        _databases.FindIndex(name => string.Equals(name, database, StringComparison.OrdinalIgnoreCase)) >= 0;

    /// <returns>true 表示指名的資料庫真的變了。</returns>
    /// <remarks>
    /// 名稱以不分大小寫比對：資料庫名稱的大小寫規則由執行個體的定序決定，而同一台上
    /// <c>LibArchive</c> 與 <c>libarchive</c> 指的是同一個。兩份都留著的症狀是同一個資料庫
    /// 被索引兩次，而 chip 列上出現兩顆看起來重複的條件。
    /// </remarks>
    public bool SetDatabaseSelected(string database, bool selected)
    {
        if (string.IsNullOrEmpty(database)) throw new ArgumentException("資料庫名稱不可為空。", nameof(database));

        var index = _databases.FindIndex(name => string.Equals(name, database, StringComparison.OrdinalIgnoreCase));

        if (selected)
        {
            if (index >= 0) return false;
            _databases.Add(database);
            return true;
        }

        if (index < 0) return false;
        _databases.RemoveAt(index);
        return true;
    }

    public bool ClearDatabases()
    {
        if (_databases.Count == 0) return false;
        _databases.Clear();
        return true;
    }

    /// <summary>這個名稱屬於系統資料庫那一段。</summary>
    public static bool IsSystemDatabase(string database) => SystemDatabaseNames.Contains(database);

    /// <summary>種類按鈕上的摘要；十幾種物件攤成 pill 會佔掉兩列，在停靠面板裡等於少看四筆結果。</summary>
    public string CategorySummary() => Summarize(_categoryIds.Count, AllCategoriesLabel, SingleCategoryLabel());

    /// <summary>伺服器按鈕上的摘要；沒有指名時說的是「跟著查詢視窗」。</summary>
    public string ServerSummary() => Server ?? ActiveEditorServerLabel;

    /// <summary>資料庫按鈕上的摘要。</summary>
    public string DatabaseSummary() =>
        Summarize(_databases.Count, CurrentConnectionLabel, _databases.Count == 1 ? _databases[0] : null);

    /// <summary>
    /// 已選條件那一列要畫哪幾顆 chip；空表示整列收起。
    /// </summary>
    /// <remarks>
    /// 預設狀態不佔那一列，是這個版面空間極大化的關鍵。比對位置不在這裡——它在工具列上
    /// 常駐可見，再畫一顆 chip 等於同一件事說兩次。
    /// </remarks>
    public IReadOnlyList<SqlSearchFilterChip> Chips()
    {
        var chips = new List<SqlSearchFilterChip>();

        // 伺服器排在最前面，而且只在指名時出現：它是範圍最外面那一圈，換掉之後清單上
        // 每一筆的來源都變了。沒有這一顆的話，使用者切到別的查詢視窗會以為自己還在搜
        // 原本那一台——而兩台上同名的物件看起來一模一樣。
        if (Server is { Length: > 0 } server)
        {
            chips.Add(new SqlSearchFilterChip(SqlSearchFilterKind.Server, server, "伺服器: " + server));
        }

        // 依分類清單的宣告順序輸出，不依使用者勾選的先後：勾選順序會讓同一組條件每次
        // 排出不同的 chip 順序，而那看起來像是條件自己變了。
        foreach (var category in _categories)
        {
            if (!_categoryIds.Contains(category.Id)) continue;
            chips.Add(new SqlSearchFilterChip(SqlSearchFilterKind.Category, category.Id, "種類: " + category.Label));
        }

        foreach (var database in _databases)
        {
            chips.Add(new SqlSearchFilterChip(SqlSearchFilterKind.Database, database, "資料庫: " + database));
        }

        if (MatchCasing) chips.Add(new SqlSearchFilterChip(SqlSearchFilterKind.MatchCasing, null, "大小寫"));
        if (WholeWord) chips.Add(new SqlSearchFilterChip(SqlSearchFilterKind.WholeWord, null, "全字"));

        return chips;
    }

    /// <summary>清掉一顆 chip 代表的條件。</summary>
    /// <returns>true 表示條件真的變了。</returns>
    public bool Remove(SqlSearchFilterChip chip)
    {
        if (chip is null) throw new ArgumentNullException(nameof(chip));

        switch (chip.Kind)
        {
            case SqlSearchFilterKind.Category:
                return chip.Value is { } category && SetCategorySelected(category, selected: false);
            case SqlSearchFilterKind.Server:
                // 只清名稱；真的換回查詢視窗那一台是 SqlSearchCatalogs 的事，
                // 由呼叫端在收到 true 之後一起做。
                if (Server is null) return false;
                Server = null;
                return true;
            case SqlSearchFilterKind.Database:
                return chip.Value is { } database && SetDatabaseSelected(database, selected: false);
            case SqlSearchFilterKind.MatchCasing:
                if (!MatchCasing) return false;
                MatchCasing = false;
                return true;
            case SqlSearchFilterKind.WholeWord:
                if (!WholeWord) return false;
                WholeWord = false;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(chip));
        }
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
            new SearchQuery(Text, generation, BuildOptions(), BuildCategories(), BuildScope(), Targets),
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
        _unavailable = DescribeUnavailable(results.Progress);
        return true;
    }

    /// <summary>
    /// 依目前的排序把這一輪的結果排好。
    /// </summary>
    /// <remarks>
    /// <see cref="SqlSearchSort.Relevance"/> 原樣交回聚合器排好的那一份，不重排一次：
    /// 跨 provider 的分數可比是 provider 的責任，這一層再排一次只會把它們的約定弄丟。
    ///
    /// 另外兩種一律以 <see cref="SearchHit.SortKey"/> 打破平手。沒有這一道的話，同一組輸入
    /// 在不同次執行會排出不同順序（provider 是併發的），使用者看到的症狀是清單會自己跳。
    /// </remarks>
    public IReadOnlyList<SearchHit> Arrange(IReadOnlyList<SearchHit> hits)
    {
        if (hits is null) throw new ArgumentNullException(nameof(hits));
        if (Sort == SqlSearchSort.Relevance || hits.Count < 2) return hits;

        var ordered = new List<SearchHit>(hits);

        ordered.Sort((left, right) =>
        {
            if (Sort == SqlSearchSort.Kind)
            {
                var kind = CategoryRank(left.CategoryId).CompareTo(CategoryRank(right.CategoryId));
                if (kind != 0) return kind;
            }

            var name = string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase);
            if (name != 0) return name;

            // 只差大小寫的兩個名稱在不分大小寫的比較下同分；不再比一次的話，它們的先後
            // 仍然取決於 provider 誰先回來。
            var exact = string.CompareOrdinal(left.SortKey, right.SortKey);
            return exact != 0 ? exact : string.CompareOrdinal(left.DedupeKey, right.DedupeKey);
        });

        return ordered;
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

        // 整輪都失敗了，個別來源讀不到那一句已經沒有意義，留著只會讓頁尾說兩件事。
        _unavailable = "";
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
            if (!HasConnection || !_hasResult) return SqlSearchStatusTone.None;

            // 讀不到某一個來源不是失敗（那會讓頁尾整行變成紅字，而對一個本來就多半
            // 讀不到的來源，等於每一次搜尋都在報錯），但它確實表示這一份不完整。
            if (_unavailable.Length != 0) return SqlSearchStatusTone.Partial;
            if (_hitCount == 0) return SqlSearchStatusTone.None;
            return _isPartial ? SqlSearchStatusTone.Partial : SqlSearchStatusTone.Result;
        }
    }

    /// <summary>頁尾那一行；平時留空，只回報結果數、部分結果、讀不到的來源與失敗。</summary>
    /// <remarks>
    /// 「有一個來源讀不到」與「掃到一半停了」都會讓 <see cref="SearchResults.IsPartial"/>
    /// 為真，但要說的話不一樣：後者叫使用者縮小範圍或加長關鍵字，前者叫他去看權限。
    /// 兩句都貼上去的話，使用者會先照第一句試三次——而那一句對他的情況完全沒有用。
    /// 所以有讀不到的來源時就由它說明這一輪為什麼不完整，泛用的那一句讓位。
    /// </remarks>
    public string Status()
    {
        if (_failure.Length != 0) return _failure;
        if (!HasConnection || !_hasResult) return "";

        // 一筆都沒有時，「沒有相符項目」由空狀態說；頁尾只剩下讀不到的來源那一句。
        if (_hitCount == 0) return _unavailable;

        var count = _hitCount.ToString(CultureInfo.InvariantCulture);

        if (_unavailable.Length != 0) return "找到 " + count + " 項。" + _unavailable;

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
        // 指名了伺服器卻沒有目錄，是那一台連不上，不是「還沒連線」；叫使用者去開查詢視窗
        // 只會讓他做一件解決不了的事。
        if (!HasConnection)
        {
            return Server is { Length: > 0 } server
                ? $"連不上 {server}。物件總管上那一台可能已經中斷，換一台或回到查詢視窗。"
                : "尚未連線。在 SQL 查詢視窗連上資料庫，或在物件總管連上伺服器之後，這裡才有東西可以搜。";
        }

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

    private string? SingleCategoryLabel()
    {
        if (_categoryIds.Count != 1) return null;
        foreach (var category in _categories) if (_categoryIds.Contains(category.Id)) return category.Label;
        return null;
    }

    /// <remarks>
    /// 一個就寫名字，多個就寫數字。三個名字串起來會把按鈕撐到吃掉搜尋框的空間，
    /// 而工具列上真正要一直看得見的是搜尋框與比對位置。完整名單在 chip 列與 Tooltip 上。
    /// </remarks>
    private static string Summarize(int count, string allLabel, string? single) =>
        count == 0 ? allLabel
            : single is { Length: > 0 } name ? name
            : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>沒有宣告的分類排在最後；不認得的 Id 不該插在認得的中間。</summary>
    private int CategoryRank(string categoryId) =>
        _categoryOrder.TryGetValue(categoryId, out var rank) ? rank : int.MaxValue;

    private SearchOptions BuildOptions()
    {
        var options = SearchOptions.None;
        if (MatchCasing) options |= SearchOptions.MatchCasing;
        if (WholeWord) options |= SearchOptions.WholeWord;
        return options;
    }

    private IEnumerable<string>? BuildCategories() =>
        _categoryIds.Count == 0 ? null : new List<string>(_categoryIds);

    /// <remarks>
    /// 伺服器那一份永遠是空的：v1 沒有連結伺服器的索引，指名伺服器等於整輪不回結果。
    /// 範圍的伺服器由目前這條連線決定，UI 只把它顯示出來。
    /// </remarks>
    private SearchScope BuildScope() =>
        _databases.Count == 0 ? SearchScope.All : new SearchScope(null, _databases.ToArray());

    /// <summary>
    /// 有沒有哪一個來源這一輪整個讀不到；有的話回傳要補的那一句。
    /// </summary>
    /// <remarks>
    /// 這一層與 provider 無關：那一句話是 provider 自己寫的，這裡只挑第一句貼上去。
    /// 認得某一個 provider 的常數的那一版，每多一個「權限常常不足」的來源
    /// （複寫、Extended Events、Always On）就要在這裡多一個 <c>if</c>，而漏掉的那一個
    /// 只會安靜地退回泛用的「部分結果」——畫面上看不出是漏了還是真的沒掃完。
    ///
    /// 只貼第一句，其餘用數字帶過：狀態列是一行，而把三句話串起來會把它撐爆，
    /// 重點（有來源沒搜到、去看權限）第一句已經說完。
    /// </remarks>
    private static string DescribeUnavailable(IReadOnlyList<SearchProviderProgress> progress)
    {
        string? first = null;
        var count = 0;

        foreach (var entry in progress)
        {
            if (entry.UnavailableReason is not { Length: > 0 } reason) continue;

            count++;
            first ??= reason;
        }

        if (first is null) return "";

        return count == 1
            ? first
            : first + "（另有 " + (count - 1).ToString(CultureInfo.InvariantCulture) + " 個來源這一輪也讀不到）";
    }

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
