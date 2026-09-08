using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>可重用的物件識別字與敘述範圍；不保存任何中繼資料解析結果。</summary>
/// <remarks>
/// 同一份 SQL 的詞法分析只做一次，但每次停留都比對當下的快照及明細，
/// 避免清快取、背景載入或切換連線後繼續沿用舊物件或「查無物件」。
///
/// 答案有兩個出處，順序固定：先問<b>這份指令碼自己宣告了什麼</b>，再問中繼資料。
/// 暫存資料表在 tempdb 裡、資料表變數不是 <c>sys.objects</c> 裡的物件、CTE 只存在於
/// 這份指令碼裡——三者在資料庫快照裡一列都查不到，而只問快照的症狀是使用者上一行
/// 才寫下的名稱，滑鼠停上去沒有提示，Ctrl+F12 也回報「不是可辨識的資料庫物件」。
/// 指令碼那一份還不必等連線：沒有連線、快取還沒載入時它照樣答得出來。
/// </remarks>
public sealed class SqlObjectLookup
{
    private readonly string _text;
    private readonly IReadOnlyList<SqlToken> _tokens;
    private readonly SqlStatementScope _scope;

    /// <summary>指令碼宣告的名冊，第一次真的要用到才建立。</summary>
    /// <remarks>
    /// 與建議清單的預覽共用同一份，「這個名稱宣告了哪些資料行」因此只有一個答案。
    /// </remarks>
    private SqlScriptDeclarations? _declarations;

    /// <summary>指令碼那一支的答案；文字與識別字都固定，算一次就不會變。</summary>
    private Candidate? _scriptCandidate;

    private bool _scriptResolved;

    private SqlObjectLookup(
        string text,
        IReadOnlyList<SqlToken> tokens,
        SqlIdentifierReference reference,
        SqlStatementScope scope)
    {
        _text = text;
        _tokens = tokens;
        Reference = reference;
        _scope = scope;
    }

    public SqlIdentifierReference Reference { get; }

    public static SqlObjectLookup? Create(string text, int position)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var reference = SqlIdentifierScanner.FindAt(text, position);

        if (reference is null)
        {
            return null;
        }

        // 詞法串流讓範圍分析與指令碼名冊共用同一次掃描；各自來一次等於在滑鼠移動的
        // 軌跡上把整份文字多掃一遍。
        var tokens = SqlTokenizer.Tokenize(text);
        return new SqlObjectLookup(text, tokens, reference, SqlScopeAnalyzer.Analyze(tokens, position));
    }

    public sealed class Candidate
    {
        internal Candidate(SqlObjectInfo objectInfo, bool needsColumn, SqlObjectDetail? scriptDetail = null)
        {
            Object = objectInfo;
            NeedsColumn = needsColumn;
            ScriptDetail = scriptDetail;
        }

        public SqlObjectInfo Object { get; }

        public bool NeedsColumn { get; }

        /// <summary>指令碼宣告的物件已經讀好的明細；資料庫物件為 null。</summary>
        /// <remarks>
        /// 帶著走而不是讓呼叫端回頭問中繼資料：這些名稱的 <c>object_id</c> 一律是 0，
        /// 而第二、三層快取就是照編號存的——問過去不是白跑一次查詢，
        /// 就是拿到另一個同樣沒有編號的東西。
        /// </remarks>
        public SqlObjectDetail? ScriptDetail { get; }
    }

    /// <param name="peekDetail">
    /// 拿資料來源換已經在手上的欄位明細，未限定的欄位要靠它才判斷得出來；
    /// 傳 null 等於「一份明細都沒有」，不能把尚未確認的欄位退回同名物件。
    /// 滑鼠停留傳的是只讀快取的那一份，使用者主動按下的路徑傳的是已經載好的那一份。
    /// </param>
    /// <param name="peekSnapshot">
    /// 拿跨資料庫或跨伺服器的來源換<b>它自己那個目錄</b>的快照；傳 null 等於
    /// 「只有目前這條連線」，那時跨庫來源一律解析不出來（絕不拿本機同名的表頂替）。
    /// </param>
    public Candidate? FindCandidate(
        SqlDatabaseSnapshot? snapshot,
        Func<SqlObjectInfo, SqlObjectDetail?>? peekDetail = null,
        Func<SqlObjectPath, SqlDatabaseSnapshot?>? peekSnapshot = null)
    {
        // 指令碼自己宣告的東西不必等連線，也不受快取影響：答案就在使用者眼前的文字裡。
        if (FindScriptCandidate() is { } script)
        {
            return script;
        }

        // 未限定的欄位排在物件之前：SELECT／WHERE 裡寫得出來的單段名稱，語意上就是
        // 某個來源的欄位。先問快照的症狀是 SELECT Branch FROM dbo.Loan 停在 Branch 上
        // 時畫出 dbo.Branch 那張表的結構——看起來正常，答的卻是另一個問題。
        var column = FindColumnCandidate(snapshot, peekDetail, peekSnapshot, out var unresolvedColumn);
        if (column is not null || unresolvedColumn)
        {
            return column;
        }

        if (TryResolveColumnOwner(snapshot, peekSnapshot, out var owner))
        {
            return owner is null ? null : new Candidate(owner, needsColumn: true);
        }

        if (snapshot is null || snapshot.IsEmpty)
        {
            return null;
        }

        var matches = ResolveObject(snapshot, _scope, Reference);
        return matches.Count == 0 ? null : new Candidate(matches[0], needsColumn: false);
    }

    public SqlObjectLocation? Locate(Candidate candidate, SqlObjectDetail? detail = null)
    {
        if (candidate is null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }

        return candidate.NeedsColumn
            ? BuildColumnLocation(
                Reference,
                candidate.Object,
                candidate.ScriptDetail ?? detail,
                candidate.ScriptDetail)
            : new SqlObjectLocation(Reference, candidate.Object, column: null, detail: candidate.ScriptDetail);
    }

    /// <remarks>詞法單元傳進去，指令碼名冊與範圍分析共用同一次掃描。</remarks>
    private SqlScriptDeclarations Declarations =>
        _declarations ??= SqlScriptDeclarations.Create(_text, _tokens);

    /// <summary>把識別字解析成這份指令碼自己宣告的物件；不是的話回傳 null。</summary>
    private Candidate? FindScriptCandidate()
    {
        if (_scriptResolved)
        {
            return _scriptCandidate;
        }

        _scriptResolved = true;
        _scriptCandidate = ResolveScriptCandidate();
        return _scriptCandidate;
    }

    private Candidate? ResolveScriptCandidate()
    {
        // 有限定字時它是資料來源，游標底下這一段是欄位：#Loan.CopyNo、t.CopyNo。
        if (Reference.Qualifier is not null)
        {
            if (Reference.Path is not { IsLocal: true } ||
                !_scope.TryResolve(Reference.Qualifier, out var owner) ||
                !owner.IsLocal || owner.SchemaName is not null)
            {
                return null;
            }

            return Declarations.Find(owner.ObjectName) is { } ownerDetail
                ? new Candidate(ownerDetail.Object, needsColumn: true, ownerDetail)
                : null;
        }

        // 別名優先於同名的宣告，與資料庫物件同一條規則：<c>FROM dbo.Loan c</c> 之後的
        // c 是 Loan，即使這份指令碼別的地方剛好有一個叫 c 的 CTE。
        if (_scope.TryResolve(Reference.Name, out var aliased))
        {
            return aliased.IsLocal && aliased.SchemaName is null && Declarations.Find(aliased.ObjectName) is { } aliasedDetail
                ? new Candidate(aliasedDetail.Object, needsColumn: false, aliasedDetail)
                : null;
        }

        return Declarations.Find(Reference.Name) is { } detail
            ? new Candidate(detail.Object, needsColumn: false, detail)
            : null;
    }

    /// <summary>
    /// 把沒有限定字的識別字解析成這條敘述某個資料來源的欄位。
    /// </summary>
    /// <remarks>
    /// 少了這一條，<c>SELECT CopyNo FROM dbo.Cat_BookCopy</c> 停在 <c>CopyNo</c> 上
    /// 什麼都沒有，同一個欄位加上別名寫成 <c>c.CopyNo</c> 卻答得出來——差別只在
    /// 使用者有沒有多打兩個字，而那不是他問的問題。
    ///
    /// 兩個來源都有同名欄位時 T-SQL 自己也判不出來，這裡就不猜：挑第一個等於
    /// 指著另一張表的欄位說這是你要的那一個。
    /// </remarks>
    private Candidate? FindColumnCandidate(
        SqlDatabaseSnapshot? snapshot,
        Func<SqlObjectInfo, SqlObjectDetail?>? peekDetail,
        Func<SqlObjectPath, SqlDatabaseSnapshot?>? peekSnapshot,
        out bool unresolved)
    {
        unresolved = false;
        if (!IsColumnPosition())
        {
            return null;
        }

        Candidate? found = null;

        foreach (var table in _scope.Tables)
        {
            var (_, detail, isScript) = ResolveSource(table, snapshot, peekSnapshot, peekDetail);

            if (detail is null)
            {
                // 未載入不等於沒有同名欄位；仍走完來源，讓 Hover 的預載委派補齊快取。
                unresolved = true;
                continue;
            }

            if (!HasColumn(detail, Reference.Name))
            {
                continue;
            }

            if (found is not null)
            {
                unresolved = true;
                return null;
            }

            found = new Candidate(detail.Object, needsColumn: true, isScript ? detail : null);
        }

        // 沒有欄位證據時仍可提示明確的別名；但已找到的欄位及歧義不能被別名搶走。
        if (found is null && _scope.TryResolve(Reference.Name, out var aliased) &&
            !string.IsNullOrEmpty(aliased.Alias))
        {
            unresolved = false;
        }

        return unresolved ? null : found;
    }

    /// <summary>
    /// 未限定的欄位可能屬於哪些資料庫來源。
    /// </summary>
    /// <remarks>
    /// 等得起查詢的呼叫端用它決定要把哪幾份明細載齊，判斷本身仍然在
    /// <see cref="FindCandidate"/> 裡——兩邊各判一次的症狀是載了一堆用不到的明細，
    /// 或是載得不夠而答不出來。指令碼自己宣告的來源不在此列：它們的欄位就寫在
    /// 眼前的括號裡，不必載。
    /// </remarks>
    public IReadOnlyList<SqlObjectInfo> FindColumnSources(
        SqlDatabaseSnapshot? snapshot,
        Func<SqlObjectPath, SqlDatabaseSnapshot?>? peekSnapshot = null)
    {
        if (!IsColumnPosition())
        {
            return Array.Empty<SqlObjectInfo>();
        }

        var sources = new List<SqlObjectInfo>();
        var seen = new HashSet<SqlObjectInfo>();

        foreach (var table in _scope.Tables)
        {
            // 與欄位判斷共用來源解析，避免 CTE 與同名資料庫物件各走一套規則。
            var (owner, _, isScript) = ResolveSource(table, snapshot, peekSnapshot, peekDetail: null);
            if (!isScript && owner is not null && seen.Add(owner))
            {
                sources.Add(owner);
            }
        }

        return sources;
    }

    /// <summary>
    /// 敘述裡指向別的資料庫或伺服器的資料來源。
    /// </summary>
    /// <remarks>
    /// 等得起查詢的呼叫端先把這些目錄的第一層載齊，再走一次判斷；滑鼠停留那條
    /// 路徑不用它——它只讀快取，載入交給 <c>PeekSnapshot</c> 自己排的背景預載。
    /// 回傳的是<b>路徑</b>而不是物件：那些目錄還沒載入時根本換不出物件，
    /// 而這份清單問的正是「要先去載哪幾個目錄」。
    /// </remarks>
    public IReadOnlyList<SqlObjectPath> FindExternalSources()
    {
        List<SqlObjectPath>? paths = null;

        foreach (var table in _scope.Tables)
        {
            if (table.IsDerived || table.IsLocal || table.Path is not { } path)
            {
                continue;
            }

            paths ??= new List<SqlObjectPath>();

            if (!ContainsCatalog(paths, path))
            {
                paths.Add(path);
            }
        }

        return (IReadOnlyList<SqlObjectPath>?)paths ?? Array.Empty<SqlObjectPath>();
    }

    /// <summary>同一台伺服器的同一個資料庫只要載一次，不必為每一張表各排一輪。</summary>
    private static bool ContainsCatalog(List<SqlObjectPath> paths, SqlObjectPath path)
    {
        foreach (var existing in paths)
        {
            if (string.Equals(existing.ServerName, path.ServerName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.DatabaseName, path.DatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把敘述裡的一個資料來源換成物件與已經在手上的明細。</summary>
    /// <returns>換不出物件或明細還沒有時 <c>Detail</c> 為 null。</returns>
    private (SqlObjectInfo? Owner, SqlObjectDetail? Detail, bool IsScript) ResolveSource(
        SqlTableReference table,
        SqlDatabaseSnapshot? snapshot,
        Func<SqlObjectPath, SqlDatabaseSnapshot?>? peekSnapshot,
        Func<SqlObjectInfo, SqlObjectDetail?>? peekDetail)
    {
        // 順序與整支一樣：先問這份指令碼宣告了什麼，再問快照。
        if (table.IsLocal && table.SchemaName is null && Declarations.Find(table.ObjectName) is { } declared)
        {
            return (declared.Object, declared, true);
        }

        var sourceSnapshot = FindSourceSnapshot(table, snapshot, peekSnapshot);

        if (sourceSnapshot is null)
        {
            return (null, null, false);
        }

        var matches = sourceSnapshot.Find(table.ObjectName, table.SchemaName);

        return matches.Count == 0
            ? (null, null, false)
            : (matches[0], peekDetail?.Invoke(matches[0]), false);
    }

    /// <summary>
    /// 這個資料來源要拿哪一份快照去比對。
    /// </summary>
    /// <remarks>
    /// 衍生資料表沒有中繼資料可查。跨庫、跨伺服器的來源要換到<b>它自己那個目錄</b>：
    /// 拿目前連線裡同名的表回答，看到的是一份看起來正常、實際上屬於另一張表的欄位。
    /// 換得到就照常回答——建議清單早就是這樣做的（每個來源各自 <c>ScopeTo</c>），
    /// 只有這一支從前一律放棄，症狀是 <c>FROM LibArchive.dbo.Loan l</c> 之後
    /// <c>l.CopyNo</c> 停上去什麼都沒有，而同一份欄位在建議清單裡列得出來。
    ///
    /// 換不到（呼叫端沒有跨庫目錄，或那個目錄還沒載入）時回 null 而不是退回本機快照。
    /// </remarks>
    private static SqlDatabaseSnapshot? FindSourceSnapshot(
        SqlTableReference table,
        SqlDatabaseSnapshot? snapshot,
        Func<SqlObjectPath, SqlDatabaseSnapshot?>? peekSnapshot)
    {
        if (table.IsDerived)
        {
            return null;
        }

        var source = table.IsLocal ? snapshot : peekSnapshot?.Invoke(table.Path!);

        return source is null || source.IsEmpty ? null : source;
    }

    /// <summary>游標底下這個名稱有沒有可能是這條敘述某個來源的欄位。</summary>
    private bool IsColumnPosition()
    {
        if (Reference.Qualifier is not null || _scope.Tables.Count == 0 || !IsColumnShapedName())
        {
            return false;
        }

        foreach (var table in _scope.Tables)
        {
            // 游標停在 FROM／JOIN／UPDATE／INSERT INTO 的那一段名稱上時，它是資料來源
            // 本身，不是誰的欄位——那一段留給後面的物件解析。
            if (Reference.Start >= table.Start && Reference.Start <= table.End)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 這個名稱寫成裸字時可能是欄位嗎。
    /// </summary>
    /// <remarks>
    /// 保留字裸寫一定不是欄位參考（<c>SELECT Order FROM t</c> 是語法錯誤），
    /// 而滑鼠會停在 <c>SELECT</c>、<c>FROM</c>、<c>WHERE</c> 上的次數遠多於停在欄位上。
    /// 少了這一道，每一次停在關鍵字上都會把整條敘述的來源明細掃過一輪。
    /// 加了方括號就是識別字，<c>[Order]</c> 照樣要能回答。
    /// </remarks>
    private bool IsColumnShapedName()
    {
        if (!SqlKeywordCatalog.IsReservedIdentifier(Reference.Name))
        {
            return true;
        }

        var start = Reference.Start;
        return start < _text.Length && (_text[start] == '[' || _text[start] == '"');
    }

    private static bool HasColumn(SqlObjectDetail detail, string name)
    {
        foreach (var column in detail.Columns)
        {
            if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>認出欄位限定詞；來源無法由這份快照解析時，仍回傳 true 以禁止物件猜測。</summary>
    private bool TryResolveColumnOwner(
        SqlDatabaseSnapshot? snapshot,
        Func<SqlObjectPath, SqlDatabaseSnapshot?>? peekSnapshot,
        out SqlObjectInfo? owner)
    {
        owner = null;

        // 多段的限定字不可能是別名：別名只有一段。多段時拿最右邊那一段去比對別名，
        // 剛好取名叫 dbo 的別名會讓 F12 跳到它指的那張表。
        if (Reference.Qualifier is null ||
            Reference.Path is not { IsLocal: true } ||
            !_scope.TryResolve(Reference.Qualifier, out var table))
        {
            return false;
        }

        // 認出限定詞就不再猜物件；換不到目錄的來源也不能借用本機同名資料。
        if (FindSourceSnapshot(table, snapshot, peekSnapshot) is { } sourceSnapshot)
        {
            var matches = sourceSnapshot.Find(table.ObjectName, table.SchemaName);
            owner = matches.Count == 0 ? null : matches[0];
        }
        return true;
    }

    /// <param name="scriptDetail">指令碼宣告的物件才有；資料庫物件為 null。</param>
    private static SqlObjectLocation? BuildColumnLocation(
        SqlIdentifierReference reference,
        SqlObjectInfo owner,
        SqlObjectDetail? detail,
        SqlObjectDetail? scriptDetail)
    {
        if (detail is null)
        {
            // 明細還沒回來時仍回報物件，呼叫端可以顯示載入中的內容。
            return new SqlObjectLocation(reference, owner, column: null, detail: scriptDetail);
        }

        foreach (var column in detail.Columns)
        {
            if (string.Equals(column.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new SqlObjectLocation(reference, owner, column, scriptDetail);
            }
        }

        // 限定詞確實是資料來源，只是沒有這個欄位；不要退回去猜同名的資料庫物件。
        return null;
    }

    /// <summary>
    /// 把識別字解析成資料庫物件。
    /// </summary>
    /// <remarks>
    /// 沒有限定詞的識別字可能是敘述裡的別名，這時要換成別名指向的資料表。
    /// 別名優先於同名物件：<c>FROM Loan AS PUBLISHER</c> 之後的 <c>PUBLISHER</c> 是 Loan。
    /// </remarks>
    private static IReadOnlyList<SqlObjectInfo> ResolveObject(
        SqlDatabaseSnapshot snapshot,
        SqlStatementScope scope,
        SqlIdentifierReference reference)
    {
        if (reference.Qualifier is null &&
            scope.TryResolve(reference.Name, out var aliased) &&
            !aliased.IsDerived)
        {
            // 別名可能指向另一個資料庫的表，那時這一份快照回答不了它。
            if (aliased.Path is { IsLocal: false })
            {
                return Array.Empty<SqlObjectInfo>();
            }

            var byAlias = snapshot.Find(aliased.ObjectName, aliased.SchemaName);

            if (byAlias.Count > 0)
            {
                return byAlias;
            }
        }

        var matches = snapshot.Find(reference.Name, reference.Qualifier);

        // 限定詞找不到時退回只用名稱比對——但只限這個名稱本來就指著這份快照的時候。
        // 跨資料庫或伺服器時猜測同名物件，會讓畫面看似正常卻指向錯誤的結構。
        return matches.Count == 0 && reference.Qualifier is not null && reference.IsLocal
            ? snapshot.Find(reference.Name)
            : matches;
    }
}
