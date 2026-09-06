using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 把敘述裡的一個資料來源攤平成「欄位從哪裡來」。
/// </summary>
/// <remarks>
/// 資料表與檢視交給中繼資料層，子查詢與 CTE 的輸出欄位則直接讀它們的選取清單
/// ——那份名單就寫在指令碼裡。內層自己又是 <c>*</c> 時遞迴下去，
/// 把最外層的別名一路帶著走。
///
/// 這一份是<b>唯一出處</b>。萬用字元展開與建議清單的欄位建議各寫一份的話，
/// 分岔的症狀是同一個別名在兩個功能得到不同的答案——實際發生過：
/// <c>SELECT a.*</c> 展得開的衍生資料表，<c>SELECT a.</c> 卻一個建議都沒有。
///
/// 建構後可以重複解析同一份文字裡的多個來源，CTE 名冊只收集一次。
/// </remarks>
public sealed class SqlColumnSourceResolver
{
    /// <summary>子查詢與 CTE 的巢狀深度上限。</summary>
    /// <remarks>
    /// 遞迴 CTE 已經另外用「正在展開的名稱」擋掉了，這個上限是為了病態的輸入：
    /// 一份互相參照的 CTE 或幾百層的衍生資料表不該讓分析器把堆疊用完。
    /// 八層在真實的指令碼裡遠遠夠用。
    /// </remarks>
    private const int MaximumDepth = 8;

    /// <summary>可以夾在 <c>SELECT</c> 與選取清單之間的字。</summary>
    internal static readonly HashSet<string> SelectListPrelude =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DISTINCT", "ALL", "PERCENT", "TIES", "WITH"
        };

    /// <summary>出現在選取清單裡就代表「這裡已經不是選取清單」的字。</summary>
    /// <remarks>
    /// 往回找 <c>SELECT</c> 時用它當煞車：<c>ORDER BY a, *</c> 的逗號往回走會先
    /// 遇到 <c>BY</c>，那時就該停手，而不是一路走到更前面某個 <c>SELECT</c> 去。
    /// </remarks>
    internal static readonly HashSet<string> SelectListTerminators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "BY", "INTO", "SET",
            "VALUES", "ON", "USING", "WHEN", "THEN", "ELSE", "END", "UNION",
            "EXCEPT", "INTERSECT", "OPTION", "FOR", "PIVOT", "UNPIVOT", "JOIN",
            "APPLY", "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "EXECUTE",
            "DECLARE", "CREATE", "ALTER", "DROP", "IF", "WHILE", "BEGIN",
            "RETURN", "PRINT", "USE", "OUTPUT", "TABLE", "AS", "GO"
        };

    /// <summary>選取清單到這些字為止。</summary>
    private static readonly HashSet<string> SelectListEnd =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "INTO", "UNION", "EXCEPT", "INTERSECT", "ORDER", "GROUP",
            "HAVING", "WHERE", "OPTION", "FOR"
        };

    private static readonly Dictionary<string, SqlCommonTableExpression> NoCommonTableExpressions =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SqlSelectIntoTable> NoSelectIntoTables =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<SqlToken> _tokens;

    /// <summary>指令碼裡宣告的暫存資料表與資料表變數，第一次真的要用到才收集。</summary>
    /// <remarks>與 <see cref="_commonTableExpressions"/> 同一條理由與同一個時機。</remarks>
    private IReadOnlyDictionary<string, SqlScriptTable>? _scriptTables;

    /// <summary>
    /// 指令碼裡的 CTE 名冊，第一次真的要用到才收集。
    /// </summary>
    /// <remarks>
    /// 這條路徑在每一次按鍵上，而絕大多數敘述裡一個 CTE 都沒有。
    /// 衍生資料表與帶結構描述的名稱（<c>dbo.PUBLISHER</c>）根本不必問名冊，
    /// 那些情形連掃描都省下來。
    /// </remarks>
    private IReadOnlyDictionary<string, SqlCommonTableExpression>? _commonTableExpressions;

    /// <summary><c>SELECT … INTO #tmp</c> 建立的暫存資料表，第一次真的要用到才收集。</summary>
    /// <remarks>
    /// 與上面兩份同一條理由與同一個時機，而且比它們更少用得到：只有帶資料行定義的
    /// 宣告落空之後才會問到這一份。
    /// </remarks>
    private IReadOnlyDictionary<string, SqlSelectIntoTable>? _selectIntoTables;

    /// <summary>已經包好的投影資料表；同一個名稱問幾次都是同一個物件。</summary>
    /// <remarks>
    /// 同一個名稱在一輪操作裡會被問好幾次（清單選到、按向右鍵看預覽、提交展開），
    /// 而資料行掛在物件上算一次就記住。每次重包一個新的，那份延後就等於沒有。
    /// </remarks>
    private Dictionary<string, SqlScriptTable>? _projectedTables;

    public SqlColumnSourceResolver(IReadOnlyList<SqlToken> tokens)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// 攤平單一資料來源；解析不出來時回傳 null。
    /// </summary>
    /// <remarks>
    /// 解析不出來與「這個來源沒有欄位」刻意不分成兩種回傳值：兩者對呼叫端都是
    /// 「這個名稱給不出欄位」，而空清單會讓呼叫端誤以為答案是有效的。
    /// </remarks>
    public IReadOnlyList<SqlColumnSource>? Resolve(SqlTableReference reference)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        var sources = new List<SqlColumnSource>();

        return TryResolve(reference, sources) ? sources : null;
    }

    /// <summary>
    /// 攤平一整組資料來源，<b>任何一個</b>解析不出來就整個放棄。
    /// </summary>
    /// <remarks>
    /// 展開 <c>SELECT *</c> 走這裡：少了幾個欄位的 <c>SELECT</c> 仍然可以執行，
    /// 卻執行出錯的結果，那比什麼都不做糟糕得多。
    /// </remarks>
    public IReadOnlyList<SqlColumnSource>? ResolveAll(IReadOnlyList<SqlTableReference> references)
    {
        if (references is null)
        {
            throw new ArgumentNullException(nameof(references));
        }

        var sources = new List<SqlColumnSource>();

        foreach (var reference in references)
        {
            if (!TryResolve(reference, sources))
            {
                return null;
            }
        }

        return sources.Count > 0 ? sources : null;
    }

    /// <summary>
    /// 攤平一整組資料來源，解析不出來的那一個跳過。
    /// </summary>
    /// <remarks>
    /// 沒有限定字的位置（<c>SELECT |</c>、<c>WHERE |</c>）走這裡：那裡列的是
    /// 「敘述看得到的欄位」，少列一個資料表變數的欄位不影響其他來源的正確性。
    /// </remarks>
    public IReadOnlyList<SqlColumnSource> ResolveAvailable(IReadOnlyList<SqlTableReference> references)
    {
        if (references is null)
        {
            throw new ArgumentNullException(nameof(references));
        }

        if (references.Count == 0)
        {
            return Array.Empty<SqlColumnSource>();
        }

        var sources = new List<SqlColumnSource>();

        foreach (var reference in references)
        {
            TryResolve(reference, sources);
        }

        return sources;
    }

    /// <summary>
    /// 攤平一個來源，失敗時把已經放進去的部分收回。
    /// </summary>
    /// <remarks>
    /// 攤平是邊走邊往 <paramref name="sources"/> 追加的，中途失敗會留下半份結果。
    /// 不收回的話，<see cref="ResolveAvailable"/> 會把那半份當成完整的欄位清單，
    /// 症狀是子查詢少列幾欄卻看不出是壞的。
    /// </remarks>
    private bool TryResolve(SqlTableReference reference, List<SqlColumnSource> sources)
    {
        var before = sources.Count;
        var resolved = TryResolveReference(
            reference,
            reference.EffectiveName,
            depth: 0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            sources);

        if (resolved && sources.Count > before)
        {
            return true;
        }

        sources.RemoveRange(before, sources.Count - before);
        return false;
    }

    /// <summary>
    /// 指令碼裡所有 CTE 的名稱。
    /// </summary>
    /// <remarks>
    /// 建議清單要在 <c>FROM</c>、<c>JOIN</c> 之後把它們列出來——那些名稱只存在於
    /// 這份指令碼裡，中繼資料查不到。與欄位解析共用同一份名冊，
    /// 呼叫端不必為了拿名稱再掃一次同一份文字。
    /// </remarks>
    public IEnumerable<string> CommonTableExpressionNames => CommonTableExpressions.Keys;

    /// <summary>
    /// 指令碼裡宣告的暫存資料表與資料表變數。
    /// </summary>
    /// <remarks>
    /// 建議清單要拿它替 <c>#tmp</c>、<c>@rows</c> 這些名稱掛上資料行清單，
    /// 提交之後才展得開 <c>INSERT</c> 骨架。與欄位解析共用同一份名冊，
    /// 呼叫端不必為了拿資料行再掃一次同一份文字。
    /// </remarks>
    public IReadOnlyDictionary<string, SqlScriptTable> ScriptTables =>
        _scriptTables ??= SqlScriptTableCollector.Collect(_tokens);

    /// <summary>
    /// 這個名稱在這份指令碼裡是一張什麼樣的資料表；不是的話回傳 null。
    /// </summary>
    /// <remarks>
    /// 「指令碼自己宣告的資料表長什麼樣」的<b>唯一出處</b>：建議清單掛在項目上的
    /// 那一份、滑鼠停留與預覽問的那一份、提交之後展開 <c>INSERT</c> 用的那一份，
    /// 全部問這裡。各自接一條的症狀是同一個名稱在三個表面顯示成三種東西。
    ///
    /// 兩個出處，順序固定：帶著型別的宣告（<c>CREATE TABLE #tmp (…)</c>、
    /// <c>DECLARE @tmp TABLE (…)</c>）說得比較多，落空才輪到
    /// <c>SELECT … INTO #tmp</c> 投影出來的那一份。後者的資料行是<b>延後</b>算的，
    /// 見 <see cref="SqlScriptTable"/>：<c>FROM</c> 之後的清單只要名稱。
    /// </remarks>
    public SqlScriptTable? FindScriptTable(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (ScriptTables.TryGetValue(name, out var declared))
        {
            return declared;
        }

        if (FindSelectIntoTable(name) is not { } selectInto)
        {
            return null;
        }

        _projectedTables ??= new Dictionary<string, SqlScriptTable>(StringComparer.OrdinalIgnoreCase);

        if (!_projectedTables.TryGetValue(name, out var projected))
        {
            projected = new SqlScriptTable(
                selectInto.Name,
                () => ProjectScriptColumns(selectInto),
                selectInto.Start,
                selectInto.End);

            _projectedTables.Add(name, projected);
        }

        return projected;
    }

    /// <summary>
    /// 指令碼裡由 <c>SELECT … INTO #tmp</c> 建立的暫存資料表；不是的話回傳 null。
    /// </summary>
    /// <remarks>
    /// 這種寫法沒有資料行定義，<see cref="ScriptTables"/> 那份名冊裡一個都不會有
    /// ——那一份收的是帶型別的宣告。但資料行仍然寫在使用者眼前：就在那句
    /// <c>SELECT</c> 的選取清單裡，與 CTE 是同一條推理，也走同一份遞迴。
    ///
    /// 呼叫順序因此固定：先問 <see cref="ScriptTables"/>，落空才問這一份。
    /// 同一個名稱兩種寫法都有時，帶型別的那一份說得比較多。
    /// </remarks>
    public SqlSelectIntoTable? FindSelectIntoTable(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return SelectIntoTables.TryGetValue(name, out var table) ? table : null;
    }

    /// <summary>
    /// 一個 <c>SELECT … INTO</c> 暫存資料表的資料行名稱；讀不出來時回傳空清單。
    /// </summary>
    /// <remarks>
    /// 讀不出來的情形與 CTE 一模一樣，因此兩者共用
    /// <see cref="ProjectColumnNames"/>：選取清單裡有 <c>*</c> 而它打在資料庫的
    /// 資料表上時，那份名單只有中繼資料知道，而問這個問題的滑鼠停留路徑不等查詢。
    /// </remarks>
    public IReadOnlyList<string> ResolveSelectIntoColumns(SqlSelectIntoTable table)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        return ProjectColumnNames(table.BodyStart, table.BodyEnd, table.Name);
    }

    /// <summary>
    /// 把投影出來的欄位名稱換成資料行。
    /// </summary>
    /// <remarks>
    /// 型別、NULL、預設值與主索引鍵一律讀不出來——那些要追到最內層的資料表，
    /// 而中間任何一段運算式都會讓答案不成立。空字串在這裡不是遺漏而是實話，
    /// 與計算資料行同一個做法：<c>INSERT</c> 骨架據此給 <c>NULL</c>，
    /// 那是唯一不會替使用者猜錯內容的預留值。
    ///
    /// 反過來說，讀不出型別<b>不代表</b>讀不出名稱。名稱寫在選取清單裡，
    /// 而 <c>INSERT INTO #tmp (…)</c> 要的正是那一份。
    /// </remarks>
    private IReadOnlyList<SqlScriptColumn> ProjectScriptColumns(SqlSelectIntoTable table)
    {
        var names = ResolveSelectIntoColumns(table);

        if (names.Count == 0)
        {
            return Array.Empty<SqlScriptColumn>();
        }

        var columns = new SqlScriptColumn[names.Count];

        for (var index = 0; index < names.Count; index++)
        {
            columns[index] = new SqlScriptColumn(
                names[index],
                string.Empty,
                isNullable: true,
                hasDefault: false,
                isIdentity: false,
                isComputed: false,
                isPrimaryKey: false);
        }

        return columns;
    }

    /// <summary>
    /// 指令碼裡的某一個 CTE；這個名稱不是 CTE 時回傳 null。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留提示與結構預覽的物件定位問的是這一份。它們原本只問中繼資料，
    /// 而 CTE 只存在於這份指令碼裡——症狀是使用者上一行才寫下的名稱，
    /// 停在上面卻什麼都不顯示。與欄位解析共用同一次掃描的結果。
    /// </remarks>
    public SqlCommonTableExpression? FindCommonTableExpression(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return CommonTableExpressions.TryGetValue(name, out var cte) ? cte : null;
    }

    /// <summary>
    /// 一個 CTE 的輸出欄位名稱；讀不出來時回傳空清單。
    /// </summary>
    /// <remarks>
    /// 讀不出來的只有一種情形：主體是 <c>SELECT *</c> 而它打在資料庫的資料表上，
    /// 那份名單只有中繼資料知道，而問這個問題的滑鼠停留路徑不等查詢。
    /// 空清單與「這個 CTE 真的一欄都沒有」在畫面上是同一件事——兩者都只顯示得出
    /// 名稱——因此不另外分一種回傳值；「不是 CTE」那一種由
    /// <see cref="FindCommonTableExpression"/> 用 null 回答。
    ///
    /// 攤平走的是與欄位建議、<c>SELECT *</c> 展開同一份遞迴，明確寫出的資料行清單
    /// 覆寫主體算出來的名稱這一條因此不必再寫一次。
    /// </remarks>
    public IReadOnlyList<string> ResolveCommonTableExpressionColumns(SqlCommonTableExpression commonTableExpression)
    {
        if (commonTableExpression is null)
        {
            throw new ArgumentNullException(nameof(commonTableExpression));
        }

        if (commonTableExpression.ColumnNames.Count > 0)
        {
            return commonTableExpression.ColumnNames;
        }

        return ProjectColumnNames(
            commonTableExpression.BodyStart,
            commonTableExpression.BodyEnd,
            commonTableExpression.Name);
    }

    private IReadOnlyDictionary<string, SqlCommonTableExpression> CommonTableExpressions =>
        _commonTableExpressions ??= CollectCommonTableExpressions(_tokens);

    private IReadOnlyDictionary<string, SqlSelectIntoTable> SelectIntoTables =>
        _selectIntoTables ??= CollectSelectIntoTables(_tokens);

    /// <summary>
    /// 把一段查詢投影成一串欄位名稱；讀不出來時回傳空清單。
    /// </summary>
    /// <param name="seedName">
    /// 這段查詢自己的名稱，先放進展開中的名冊擋住自我參照
    /// （遞迴 CTE，以及 <c>SELECT … INTO #t … FROM #t</c>）。
    /// </param>
    /// <remarks>
    /// CTE 與 <c>SELECT … INTO</c> 問的是同一件事：「這段查詢在外層看得到哪些欄位」。
    /// 各寫一份的症狀是同一種選取清單在兩處給出不同的答案，而且沒有任何徵兆。
    /// </remarks>
    private IReadOnlyList<string> ProjectColumnNames(int bodyStart, int bodyEnd, string seedName)
    {
        var sources = new List<SqlColumnSource>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seedName };

        if (!TryExpandQuery(bodyStart, bodyEnd, qualifier: null, depth: 1, visiting, sources))
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();

        foreach (var source in sources)
        {
            // 一個來源要問中繼資料就整份放棄：半份欄位清單看起來與完整的一模一樣，
            // 而使用者會照著它去找一個根本沒列出來的欄位。
            if (source.Kind != SqlColumnSourceKind.Names)
            {
                return Array.Empty<string>();
            }

            names.AddRange(source.Names);
        }

        return names;
    }

    /// <summary>
    /// 把一個資料來源攤平成欄位來源。
    /// </summary>
    /// <param name="qualifier">攤平後要補在欄位前面的名稱，一路由最外層帶下來。</param>
    private bool TryResolveReference(
        SqlTableReference reference,
        string? qualifier,
        int depth,
        HashSet<string> visiting,
        List<SqlColumnSource> sources)
    {
        if (depth > MaximumDepth)
        {
            return false;
        }

        if (reference.IsDerived)
        {
            var open = FindTokenAt(_tokens, reference.Start);

            // 衍生資料表的第一個詞法單元是左括號；資料表變數（@t）不是。
            // 它的欄位確實不在中繼資料裡——資料表變數根本不是 sys.objects 裡的物件
            // ——但 DECLARE @t TABLE (…) 就寫在指令碼裡，讀得出來。
            if (open < 0 || !_tokens[open].IsPunctuation("("))
            {
                return TryResolveScriptTable(reference.ObjectName, qualifier, depth, visiting, sources);
            }

            var close = SqlTokenNavigator.FindClosingParenthesis(_tokens, open, _tokens.Count);

            return close > open
                && TryExpandQuery(open + 1, close, qualifier, depth + 1, visiting, sources);
        }

        // CTE 名稱不帶結構描述；dbo.c 指的一定是資料庫裡的物件，不是 CTE。
        if (reference.SchemaName is null &&
            CommonTableExpressions.TryGetValue(reference.ObjectName, out var cte))
        {
            if (cte.ColumnNames.Count > 0)
            {
                sources.Add(SqlColumnSource.FromNames(cte.ColumnNames, qualifier));
                return true;
            }

            // 遞迴 CTE 會參照自己，沒有這道關就會一直展開下去。
            if (!visiting.Add(cte.Name))
            {
                return false;
            }

            try
            {
                return TryExpandQuery(cte.BodyStart, cte.BodyEnd, qualifier, depth + 1, visiting, sources);
            }
            finally
            {
                visiting.Remove(cte.Name);
            }
        }

        // 暫存資料表在 tempdb 裡，而中繼資料只看得到目前連線的那一個資料庫；
        // 交給下面那一行的話，查詢一定落空而使用者什麼欄位都看不到。
        // 帶結構描述的名稱不必問：#tmp 不會寫成 dbo.#tmp。
        if (reference.SchemaName is null &&
            TryResolveScriptTable(reference.ObjectName, qualifier, depth, visiting, sources))
        {
            return true;
        }

        sources.Add(SqlColumnSource.FromTable(reference, qualifier));
        return true;
    }

    /// <summary>
    /// 把指令碼自己宣告的資料表攤平成欄位來源。
    /// </summary>
    /// <remarks>
    /// 資料行有兩個出處，順序固定：先問帶著型別的宣告
    /// （<c>CREATE TABLE #tmp (…)</c>、<c>DECLARE @tmp TABLE (…)</c>），
    /// 落空才問 <c>SELECT … INTO #tmp</c> 那句 <c>SELECT</c> 的選取清單。
    /// 同一個名稱兩種寫法都有時前者說得比較多，而後者是<b>唯一</b>寫得出
    /// <c>SELECT * INTO #tmp FROM dbo.Loan</c> 那些欄位的地方——它整份攤平到
    /// 中繼資料那一層，與子查詢的 <c>*</c> 走同一條遞迴。
    ///
    /// 空的資料行清單當成解析不出來：回報空清單會讓呼叫端以為那張表真的一欄都沒有。
    /// </remarks>
    private bool TryResolveScriptTable(
        string name,
        string? qualifier,
        int depth,
        HashSet<string> visiting,
        List<SqlColumnSource> sources)
    {
        if (ScriptTables.TryGetValue(name, out var table) && table.ColumnNames.Count > 0)
        {
            sources.Add(SqlColumnSource.FromNames(table.ColumnNames, qualifier, table.Name));
            return true;
        }

        // SELECT … INTO #t … FROM #t 會參照自己，沒有這道關就會一直展開下去。
        if (!SelectIntoTables.TryGetValue(name, out var selectInto) || !visiting.Add(name))
        {
            return false;
        }

        var before = sources.Count;

        try
        {
            if (!TryExpandQuery(
                    selectInto.BodyStart,
                    selectInto.BodyEnd,
                    qualifier,
                    depth + 1,
                    visiting,
                    sources))
            {
                return false;
            }
        }
        finally
        {
            visiting.Remove(name);
        }

        // 這一份說得出出處，就把名字補上：在 UPDATE #Loan SET | 看到「查詢結果」
        // 會讓人以為認錯了東西。攤平到資料庫資料表的那幾筆不動——它們的出處由
        // 中繼資料自己說，而那才是使用者要找的那張表。
        for (var index = before; index < sources.Count; index++)
        {
            if (sources[index].Kind == SqlColumnSourceKind.Names)
            {
                sources[index] = SqlColumnSource.FromNames(
                    sources[index].Names,
                    sources[index].Qualifier,
                    selectInto.Name);
            }
        }

        return true;
    }

    /// <summary>讀出一段查詢的輸出欄位。</summary>
    private bool TryExpandQuery(
        int start,
        int end,
        string? qualifier,
        int depth,
        HashSet<string> visiting,
        List<SqlColumnSource> sources)
    {
        var index = start;

        // ((SELECT …)) 這種寫法的外層括號要看穿。
        while (index < end && _tokens[index].IsPunctuation("("))
        {
            index++;
        }

        if (index >= end || !_tokens[index].IsKeyword("SELECT"))
        {
            return false;
        }

        var selectIndex = index;
        index = SkipSelectListPrelude(index + 1, end);

        var names = new List<string>();
        IReadOnlyList<SqlTableReference>? innerSources = null;

        while (index < end)
        {
            var itemEnd = FindItemEnd(index, end);

            if (itemEnd == index)
            {
                break;
            }

            if (IsWildcardItem(index, itemEnd, out var itemQualifier))
            {
                // 名稱要先落地，否則 SELECT Id, * 攤平後 Id 會排到資料表欄位後面。
                Flush(names, qualifier, sources);

                innerSources ??= SqlScopeAnalyzer.ExtractSources(_tokens, selectIndex, end);

                if (!TryResolveInnerWildcard(innerSources, itemQualifier, qualifier, depth, visiting, sources))
                {
                    return false;
                }
            }
            else if (TryGetOutputName(index, itemEnd, out var name))
            {
                names.Add(name);
            }
            else
            {
                // 沒有名稱的運算式（SELECT a + b）在外層就是「(無資料行名稱)」，
                // 攤平成欄位清單時無從稱呼它。
                return false;
            }

            index = itemEnd;

            if (index < end && _tokens[index].IsPunctuation(","))
            {
                index++;
                continue;
            }

            break;
        }

        Flush(names, qualifier, sources);
        return true;
    }

    private bool TryResolveInnerWildcard(
        IReadOnlyList<SqlTableReference> innerSources,
        string? itemQualifier,
        string? qualifier,
        int depth,
        HashSet<string> visiting,
        List<SqlColumnSource> sources)
    {
        if (innerSources.Count == 0)
        {
            return false;
        }

        if (itemQualifier is null)
        {
            foreach (var inner in innerSources)
            {
                if (!TryResolveReference(inner, qualifier, depth, visiting, sources))
                {
                    return false;
                }
            }

            return true;
        }

        var scope = new SqlStatementScope(innerSources, 0, 0);

        return scope.TryResolve(itemQualifier, out var target)
            && TryResolveReference(target, qualifier, depth, visiting, sources);
    }

    private static void Flush(List<string> names, string? qualifier, List<SqlColumnSource> sources)
    {
        if (names.Count == 0)
        {
            return;
        }

        sources.Add(SqlColumnSource.FromNames(names.ToArray(), qualifier));
        names.Clear();
    }

    private int SkipSelectListPrelude(int index, int end)
    {
        while (index < end)
        {
            var token = _tokens[index];

            if (token.IsKeyword("TOP"))
            {
                index++;

                if (index < end && _tokens[index].IsPunctuation("("))
                {
                    var close = SqlTokenNavigator.FindClosingParenthesis(_tokens, index, _tokens.Count);
                    index = close > index ? close + 1 : end;
                    continue;
                }

                if (index < end && _tokens[index].Kind is SqlTokenKind.Number or SqlTokenKind.Variable)
                {
                    index++;
                }

                continue;
            }

            if (token.Kind == SqlTokenKind.Identifier && !token.IsQuoted && SelectListPrelude.Contains(token.Value))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    /// <summary>選取清單裡下一個逗號或子句關鍵字的位置。</summary>
    private int FindItemEnd(int index, int end)
    {
        var depth = 0;

        for (var i = index; i < end; i++)
        {
            var token = _tokens[i];

            if (token.IsPunctuation("("))
            {
                depth++;
                continue;
            }

            if (token.IsPunctuation(")"))
            {
                depth--;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (token.IsPunctuation(",") || token.IsPunctuation(";"))
            {
                return i;
            }

            if (token.Kind == SqlTokenKind.Identifier &&
                !token.IsQuoted &&
                SelectListEnd.Contains(token.Value))
            {
                return i;
            }
        }

        return end;
    }

    /// <summary>選取項是不是 <c>*</c> 或 <c>別名.*</c>。</summary>
    private bool IsWildcardItem(int start, int end, out string? qualifier)
    {
        qualifier = null;

        if (end - start == 1)
        {
            return _tokens[start].Kind == SqlTokenKind.Operator && _tokens[start].Value == "*";
        }

        if (end - start != 3 ||
            _tokens[start].Kind != SqlTokenKind.Identifier ||
            !_tokens[start + 1].IsPunctuation(".") ||
            _tokens[start + 2].Kind != SqlTokenKind.Operator ||
            _tokens[start + 2].Value != "*")
        {
            return false;
        }

        qualifier = _tokens[start].Value;
        return true;
    }

    /// <summary>
    /// 一個選取項在外層看到的欄位名稱。
    /// </summary>
    /// <remarks>
    /// T-SQL 有三種命名寫法，順序不能顛倒：<c>AS 名稱</c>、<c>名稱 = 運算式</c>、
    /// 直接把名稱接在運算式後面。都沒有時才退回「這一項本身就是欄位參照」，
    /// 取它的最後一段。
    /// </remarks>
    private bool TryGetOutputName(int start, int end, out string name)
    {
        name = string.Empty;

        if (end <= start)
        {
            return false;
        }

        var last = _tokens[end - 1];

        // expr AS 名稱
        if (end - start >= 3 && _tokens[end - 2].IsKeyword("AS") && last.Kind == SqlTokenKind.Identifier)
        {
            name = last.Value;
            return true;
        }

        // 名稱 = expr
        if (end - start >= 3 &&
            _tokens[start].Kind == SqlTokenKind.Identifier &&
            _tokens[start + 1].Kind == SqlTokenKind.Operator &&
            _tokens[start + 1].Value == "=")
        {
            name = _tokens[start].Value;
            return true;
        }

        // expr 名稱（省略 AS）。前一個詞法單元決定最後那個識別字是別名還是
        // 運算式的一部分：ISNULL(a, 0) x 是別名，a.b 的 b 不是。
        if (end - start >= 2 && last.Kind == SqlTokenKind.Identifier && IsAliasFollower(_tokens[end - 2]))
        {
            if (!last.IsQuoted && SelectListTerminators.Contains(last.Value))
            {
                return false;
            }

            name = last.Value;
            return true;
        }

        // 單純的欄位參照：Id、a.Id、dbo.t.Id
        if (last.Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        for (var i = start; i < end - 1; i++)
        {
            if (_tokens[i].Kind != SqlTokenKind.Identifier && !_tokens[i].IsPunctuation("."))
            {
                return false;
            }
        }

        name = last.Value;
        return true;
    }

    /// <summary>前面接著這種詞法單元的識別字，是省略了 <c>AS</c> 的別名。</summary>
    private static bool IsAliasFollower(SqlToken token)
    {
        return token.Kind is SqlTokenKind.Number or SqlTokenKind.String or SqlTokenKind.Variable
            || token.IsPunctuation(")")
            || (token.Kind == SqlTokenKind.Identifier && token.IsQuoted);
    }

    /// <summary>
    /// 收集指令碼裡所有的 CTE。
    /// </summary>
    /// <remarks>
    /// 不限定在游標所在的批次裡找：CTE 名稱在一份指令碼裡幾乎不會重複，
    /// 而要正確劃出批次邊界得再維護一套規則，代價高於它擋掉的問題。
    ///
    /// <c>WITH (NOLOCK)</c> 這類資料表提示自然被排除——它後面接的是左括號
    /// 而不是名稱。
    ///
    /// 一個 CTE 都沒有時共用同一份空名冊：這條路徑在每一次按鍵上，
    /// 而那正是絕大多數敘述的情形。
    /// </remarks>
    private static IReadOnlyDictionary<string, SqlCommonTableExpression> CollectCommonTableExpressions(
        IReadOnlyList<SqlToken> tokens)
    {
        Dictionary<string, SqlCommonTableExpression>? result = null;

        for (var index = 0; index < tokens.Count; index++)
        {
            if (!tokens[index].IsKeyword("WITH"))
            {
                continue;
            }

            var cursor = index + 1;

            while (cursor < tokens.Count)
            {
                var name = tokens[cursor];

                if (name.Kind != SqlTokenKind.Identifier ||
                    (!name.IsQuoted && SelectListTerminators.Contains(name.Value)))
                {
                    break;
                }

                cursor++;
                var columns = Array.Empty<string>() as IReadOnlyList<string>;

                if (cursor < tokens.Count && tokens[cursor].IsPunctuation("("))
                {
                    var listEnd = SqlTokenNavigator.FindClosingParenthesis(tokens, cursor, tokens.Count);

                    if (listEnd < 0)
                    {
                        break;
                    }

                    columns = ReadColumnList(tokens, cursor + 1, listEnd);
                    cursor = listEnd + 1;
                }

                if (cursor + 1 >= tokens.Count ||
                    !tokens[cursor].IsKeyword("AS") ||
                    !tokens[cursor + 1].IsPunctuation("("))
                {
                    break;
                }

                var bodyEnd = SqlTokenNavigator.FindClosingParenthesis(tokens, cursor + 1, tokens.Count);

                if (bodyEnd < 0)
                {
                    break;
                }

                result ??= new Dictionary<string, SqlCommonTableExpression>(StringComparer.OrdinalIgnoreCase);

                // 同名時保留先出現的那一個，與 T-SQL 不允許重複命名的前提一致。
                if (!result.ContainsKey(name.Value))
                {
                    result.Add(
                        name.Value,
                        new SqlCommonTableExpression(
                            name.Value,
                            columns,
                            cursor + 2,
                            bodyEnd,
                            name.Start,
                            tokens[bodyEnd].End));
                }

                cursor = bodyEnd + 1;

                if (cursor < tokens.Count && tokens[cursor].IsPunctuation(","))
                {
                    cursor++;
                    continue;
                }

                break;
            }

            index = Math.Max(index, cursor - 1);
        }

        return result ?? NoCommonTableExpressions;
    }

    /// <summary>
    /// 收集指令碼裡所有 <c>SELECT … INTO #tmp</c> 建立的暫存資料表。
    /// </summary>
    /// <remarks>
    /// 從 <c>SELECT</c> 往前認而不是從 <c>INTO</c> 往回認：<c>INSERT INTO #tmp</c>
    /// 的形狀與這裡一模一樣，往回認會把使用者剛寫的那句 INSERT 讀成一份宣告，
    /// 而那份「宣告」的資料行其實是插入目標的資料行清單。
    ///
    /// 認出一句就整句跳過。巢狀的 <c>SELECT</c>（衍生資料表、CTE 主體、
    /// <c>IN (SELECT …)</c>）寫不出 <c>SELECT … INTO</c>，掃進去只是白費。
    ///
    /// 一句都沒有時共用同一份空名冊，與另外兩份名冊同一條理由。
    /// </remarks>
    private static IReadOnlyDictionary<string, SqlSelectIntoTable> CollectSelectIntoTables(
        IReadOnlyList<SqlToken> tokens)
    {
        Dictionary<string, SqlSelectIntoTable>? result = null;

        for (var index = 0; index < tokens.Count; index++)
        {
            if (!tokens[index].IsKeyword("SELECT"))
            {
                continue;
            }

            var end = SqlScopeAnalyzer.FindStatementEnd(tokens, index);
            var target = FindSelectIntoTarget(tokens, index + 1, end);

            if (target > 0)
            {
                var name = tokens[target].Value;
                result ??= new Dictionary<string, SqlSelectIntoTable>(StringComparer.OrdinalIgnoreCase);

                // 同名時保留先出現的那一個，與另外兩份名冊同一條規則。
                if (!result.ContainsKey(name))
                {
                    result.Add(
                        name,
                        new SqlSelectIntoTable(
                            name,
                            index,
                            end,
                            tokens[index].Start,
                            tokens[end - 1].End));
                }
            }

            index = Math.Max(index, end - 1);
        }

        return result ?? NoSelectIntoTables;
    }

    /// <summary>
    /// 這句 <c>SELECT</c> 的 <c>INTO</c> 指到的暫存資料表詞法單元；沒有時回傳 -1。
    /// </summary>
    /// <remarks>
    /// 只認深度 0 的 <c>INTO</c>：巢狀查詢裡的那一個屬於它自己。井號開頭則是必要
    /// 條件——<c>SELECT … INTO dbo.NewTable</c> 建的是一張真的資料表，那是中繼資料
    /// 的事，拿這份還沒執行的投影去蓋掉它，等於用「正要變成什麼樣」回答
    /// 「現在長什麼樣」。
    /// </remarks>
    private static int FindSelectIntoTarget(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var depth = 0;

        for (var index = start; index < end; index++)
        {
            var token = tokens[index];

            if (token.IsPunctuation("("))
            {
                depth++;
                continue;
            }

            if (token.IsPunctuation(")"))
            {
                depth--;
                continue;
            }

            if (depth != 0 || !token.IsKeyword("INTO"))
            {
                continue;
            }

            return index + 1 < end &&
                tokens[index + 1].Kind == SqlTokenKind.Identifier &&
                tokens[index + 1].Value.Length > 1 &&
                tokens[index + 1].Value[0] == '#'
                ? index + 1
                : -1;
        }

        return -1;
    }

    private static IReadOnlyList<string> ReadColumnList(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var names = new List<string>();

        for (var index = start; index < end; index++)
        {
            if (tokens[index].Kind == SqlTokenKind.Identifier)
            {
                names.Add(tokens[index].Value);
            }
        }

        return names;
    }

    private static int FindTokenAt(IReadOnlyList<SqlToken> tokens, int position)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Start == position)
            {
                return index;
            }

            if (tokens[index].Start > position)
            {
                break;
            }
        }

        return -1;
    }
}
