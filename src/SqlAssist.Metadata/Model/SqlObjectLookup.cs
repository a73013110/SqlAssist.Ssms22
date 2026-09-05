using System;
using System.Collections.Generic;
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
    /// 與欄位建議共用同一個解析器，「這個名稱宣告了哪些資料行」因此只有一份。
    /// </remarks>
    private SqlColumnSourceResolver? _resolver;

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

    public Candidate? FindCandidate(SqlDatabaseSnapshot? snapshot)
    {
        // 指令碼自己宣告的東西不必等連線，也不受快取影響：答案就在使用者眼前的文字裡。
        if (FindScriptCandidate() is { } script)
        {
            return script;
        }

        if (snapshot is null || snapshot.IsEmpty)
        {
            return null;
        }

        if (TryResolveColumnOwner(snapshot, _scope, Reference, out var owner))
        {
            return new Candidate(owner, needsColumn: true);
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

    private SqlColumnSourceResolver Resolver => _resolver ??= new SqlColumnSourceResolver(_tokens);

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
                owner.SchemaName is not null)
            {
                return null;
            }

            return FindDeclared(owner.ObjectName) is { } ownerDetail
                ? new Candidate(ownerDetail.Object, needsColumn: true, ownerDetail)
                : null;
        }

        // 別名優先於同名的宣告，與資料庫物件同一條規則：<c>FROM dbo.Loan c</c> 之後的
        // c 是 Loan，即使這份指令碼別的地方剛好有一個叫 c 的 CTE。
        if (_scope.TryResolve(Reference.Name, out var aliased))
        {
            return aliased.SchemaName is null && FindDeclared(aliased.ObjectName) is { } aliasedDetail
                ? new Candidate(aliasedDetail.Object, needsColumn: false, aliasedDetail)
                : null;
        }

        return FindDeclared(Reference.Name) is { } detail
            ? new Candidate(detail.Object, needsColumn: false, detail)
            : null;
    }

    /// <summary>這個名稱是不是這份指令碼宣告的；是的話連明細一起讀出來。</summary>
    /// <remarks>
    /// 井號與小老鼠開頭是暫存資料表與資料表變數的必要條件，而那是一個字元的判斷：
    /// 絕大多數的停留都落在一般名稱上，那時連資料表名冊都不必建。
    /// </remarks>
    private SqlObjectDetail? FindDeclared(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (SqlIdentifier.IsScriptScoped(name))
        {
            // 資料行讀不出來的宣告（SELECT … INTO #Loan）名冊裡根本沒有，
            // 那時退回去查快照——名稱與資料行是兩件事。
            return Resolver.ScriptTables.TryGetValue(name, out var table)
                ? SqlScriptTableDetail.Create(table, _text)
                : null;
        }

        return Resolver.FindCommonTableExpression(name) is { } commonTableExpression
            ? SqlScriptTableDetail.Create(
                commonTableExpression,
                Resolver.ResolveCommonTableExpressionColumns(commonTableExpression),
                _text)
            : null;
    }

    /// <summary>判斷這個參考是不是「敘述中某個資料來源的欄位」，是的話取出該資料來源。</summary>
    private static bool TryResolveColumnOwner(
        SqlDatabaseSnapshot snapshot,
        SqlStatementScope scope,
        SqlIdentifierReference reference,
        out SqlObjectInfo owner)
    {
        owner = null!;

        // 多段的限定字不可能是別名：別名只有一段。多段時拿最右邊那一段去比對別名，
        // 剛好取名叫 dbo 的別名會讓 F12 跳到它指的那張表。
        if (reference.Qualifier is null ||
            reference.Path is not { IsLocal: true } ||
            !scope.TryResolve(reference.Qualifier, out var table) ||
            table.IsDerived)
        {
            return false;
        }

        var matches = snapshot.Find(table.ObjectName, table.SchemaName);

        if (matches.Count == 0)
        {
            return false;
        }

        owner = matches[0];
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
