using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.CompletionAudit;

/// <summary>
/// 一個批次的語法事實：哪些名稱是新取的、哪些取過的名稱在哪個範圍內引用得到、哪些詞元站在名稱的位置上。
/// </summary>
/// <remarks>
/// 新取的名稱（別名、CREATE 的目標、宣告的變數與參數、CTE、資料行與條件約束）本來就不該有清單，要排除，
/// 而且不能用產品自己的判斷排除——產品把一個該列的位置誤判成新名字，正是稽核要抓的漏。所以用 ScriptDom 的
/// 語法樹另外認一次。
///
/// 「之前取過」要看範圍，否則同名就算：資料表別名只在它那一個查詢（含子查詢）裡，選取清單的別名只在
/// 同一句的 ORDER BY 裡，CTE 只在它那一句，變數、資料表變數、暫存資料表與資料指標到批次結束。UNION 的下一個
/// 分支還沒寫 FROM 時，上一個分支的同名別名不算數。衍生資料表與 CTE 的資料行清單是那張表的欄位：沒寫限定字時
/// 只在那張表是這個欄位可能屬於的表之一時引用得到（與 <see cref="ColumnOwners"/> 同一條規則），點號之後要限定字指它——
/// <c>e.OrganizationNode</c> 是 e 的欄位，不是同一句裡叫 OrganizationNode 的選取別名；CTE 自己的查詢裡、
/// <c>UPDATE t SET</c> 的 t 不是它時，同名的詞是別張表的欄位。資料行、條件約束與索引的定義只記「這裡是新取的」：引用它們要先知道是
/// 哪一張表，與資料庫的欄位同一類（<see cref="IAuditNameIndex"/>），只看名字相同會認錯——
/// <c>CREATE TABLE Loan (CopyNo int REFERENCES Copy (CopyNo))</c> 的第二個 CopyNo 是 Copy 的欄位。
///
/// <c>inserted</c>／<c>deleted</c> 不是誰取的名字，卻在兩種範圍裡引用得到：DML 觸發程序的整句（指父資料表）
/// 與 OUTPUT 子句（指那句 DML 的目標）。範圍外的同名詞照一般名稱判斷。
///
/// 欄位要知道屬於哪一張表（<see cref="ColumnOwners"/>）：資料行清單（INSERT／MERGE 的資料行、SET 的左邊、索引、條件約束與
/// 統計資料的欄位）屬於它指定的那張表，查詢裡沒寫限定字的欄位屬於範圍內的資料來源。名稱索引只比名字，
/// 那些表全都查不到時，碰巧同名的欄位不算列得出來。資料表定義（<c>CREATE TABLE</c>、資料表變數、資料表型別）
/// 自己的條件約束與索引清單例外：資料行就定義在同一份括號裡，在那幾份清單裡引用得到。
///
/// 剖析不過的那一句（<see cref="IsUnparsed"/>）挖成空白再剖析，其餘的句子照常認；那一句裡的名稱認不出來，
/// 在稽核裡歸成不明，不算漏，詞元在語法樹上也沒有角色（<see cref="HasRole"/>）。
/// </remarks>
public sealed class AuditDefinitions
{
    private readonly HashSet<int> _starts = new();
    private readonly HashSet<int> _nameReferences = new();
    private readonly HashSet<int> _notNames = new();
    private readonly HashSet<int> _unparsed = new();
    private readonly Dictionary<string, List<Visibility>> _scoped = new(StringComparer.Ordinal);
    private readonly List<(TSqlFragment Name, Scope Scope, string? Table)> _pending = new();
    private readonly List<(int Start, int End)> _queries = new();
    private readonly List<(int Start, int End)> _statements = new();
    private readonly List<(int Start, int End)> _orderBys = new();
    private readonly Dictionary<int, int> _fromStarts = new();
    private readonly List<(int Start, int From)> _aliasedTargets = new();
    private readonly Dictionary<int, (string Name, string? Database)> _aliasSources = new();
    private readonly Dictionary<string, (string Name, string? Database)> _starSources = new(StringComparer.Ordinal);
    private readonly Dictionary<int, IReadOnlyList<IReadOnlyList<(string Name, string? Database)>>> _columnOwners = new();
    private readonly Dictionary<int, string> _listOwners = new();
    private readonly List<(int Start, int End, List<(string Name, string? Database)?> Sources, HashSet<string> Names)> _sourceScopes = new();
    private readonly List<(int Start, int End, int Scope)> _barriers = new();
    private readonly List<Identifier> _columnReferences = new();
    private readonly List<(int Start, int End, SchemaObjectName Name, IList<ColumnDefinition> Columns)> _createdTables = new();
    private readonly HashSet<int> _tableNames = new();
    private readonly List<(int Start, int End, SchemaObjectName? Owner)> _changeTables = new();
    private int _batchEnd;

    private AuditDefinitions()
    {
    }

    /// <summary>名稱引用得到多遠。</summary>
    private enum Scope
    {
        /// <summary>只記「這裡是新取的」，之後引用不算。</summary>
        None,

        /// <summary>它那一個查詢（最內層的 QuerySpecification；不在查詢裡就是那一句）。</summary>
        Query,

        /// <summary>它那一句。</summary>
        Statement,

        /// <summary>到批次結束。</summary>
        Batch,

        /// <summary>同一句裡、它之後的查詢 ORDER BY（視窗的不算）。</summary>
        OrderBy,
    }

    /// <summary>一個名稱從哪裡取、在哪一段文字裡引用得到。</summary>
    private readonly struct Visibility
    {
        public Visibility(int definedAt, int from, int to, string? table)
        {
            DefinedAt = definedAt;
            From = from;
            To = to;
            Table = table;
        }

        public int DefinedAt { get; }

        public int From { get; }

        public int To { get; }

        /// <summary>衍生資料表與 CTE 的資料行清單：這個名稱是那張表（別名或 CTE 名稱）的欄位。</summary>
        public string? Table { get; }
    }

    /// <summary>剖析不過、從錯的那個詞起不稽核的句數。</summary>
    public int UnparsedStatements { get; private set; }

    /// <summary>
    /// <paramref name="start"/> 起頭的詞元在剖析不過的那一句裡、錯的那個詞或它之後：作者寫的不是 T-SQL，
    /// 下一個詞不是這一格的答案。錯之前的詞照常稽核——剖析器停在第一個接不下去的詞，那之前的每一格都還是
    /// 合法語句的開頭，寫到一半的指令碼也照樣稽核。
    /// </summary>
    public bool IsUnparsed(int start) => _unparsed.Contains(start);

    /// <summary>
    /// <paramref name="start"/> 起頭的詞元在語法樹的某一句裡，<see cref="IsNameReference"/> 說得出它是不是名稱；
    /// 剖析不過的那一句（錯之前也是）不在樹上。
    /// </summary>
    public bool HasRole(int start) => InnermostRange(_statements, start) is not null;

    /// <summary><paramref name="start"/> 起頭的那個名稱是新取的。</summary>
    public bool IsDefinition(int start) => _starts.Contains(start);

    /// <summary>
    /// <paramref name="start"/> 起頭的詞元站在名稱的位置上：語法樹上的識別字，型別名稱與函式名稱除外
    /// （內建型別與函式是字，由字的名單判斷）。
    /// </summary>
    public bool IsNameReference(int start) => _nameReferences.Contains(start);

    /// <summary><paramref name="name"/> 在 <paramref name="start"/> 之前取過，而且這裡還在它的範圍內。</summary>
    /// <param name="qualifier">這個名稱接在點號之後時，點號前的那一段。</param>
    public bool IsDefinedBefore(string name, int start, string? qualifier = null) =>
        Nearest(name, start, qualifier) is not null;

    /// <summary><paramref name="name"/> 是之前取過、這裡引用得到的資料行名稱（CTE 或衍生資料表的資料行清單）。</summary>
    public bool IsColumnDefinedBefore(string name, int start) => Nearest(name, start, qualifier: null) is { Table: not null };

    /// <summary>
    /// <paramref name="name"/> 在 <paramref name="start"/> 指的是之後才取的那一次：截斷的地方還沒寫到，
    /// 產品與 SSMS 都不可能認得（<c>SELECT r.ReaderId FROM Lib_Reader r</c> 的 r）。
    /// </summary>
    /// <remarks>
    /// 指的是範圍最內層的那一次，不是之前最近的那一次：<c>FROM #Loan a JOIN (SELECT a.CopyNo FROM Copy a)</c>
    /// 的 <c>a.</c> 是子查詢之後才取的 Copy，外層的 #Loan 被它遮住。只看「之前取過」的話，截斷處認得的外層
    /// 那一個會讓 Copy 的欄位看起來該列。
    /// </remarks>
    /// <param name="qualifier">這個名稱接在點號之後時，點號前的那一段。</param>
    public bool IsDefinedLater(string name, int start, string? qualifier = null)
    {
        if (!_scoped.TryGetValue(AuditText.Normalize(name), out var definitions))
        {
            return false;
        }

        Visibility? innermost = null;

        foreach (var definition in definitions)
        {
            if (definition.From > start ||
                start >= definition.To ||
                !Reaches(definition, start, qualifier))
            {
                continue;
            }

            // 同一層取兩次寫不出來；真的遇到時以之前那一次為準，維持「取過」的原判。
            var length = definition.To - definition.From;

            if (innermost is not { } current ||
                length < current.To - current.From ||
                length == current.To - current.From && definition.DefinedAt < current.DefinedAt)
            {
                innermost = definition;
            }
        }

        return innermost is { } found && found.DefinedAt > start;
    }

    /// <summary>
    /// 別名 <paramref name="alias"/> 在 <paramref name="start"/> 指的資料表名稱（最後一段）與寫出來的資料庫；
    /// 不是資料表的別名（衍生資料表、函式、資料表變數）或不在範圍內時是 null。
    /// </summary>
    /// <remarks>
    /// 資料庫也要帶出來：名稱索引只比名字，<c>Other.dbo.Loan</c> 在連線的伺服器上沒有那個資料庫時，
    /// 碰巧同名的 Loan 會讓欄位看起來列得出來。
    ///
    /// 暫存資料表是 <c>SELECT * INTO #Loan FROM Other.dbo.Loan</c> 時看的是那張表：欄位只能從它攤平，
    /// 名字是指令碼取的不代表欄位認得出來。
    /// </remarks>
    public (string Name, string? Database)? SourceOf(string alias, int start)
    {
        if (IsChangeTable(alias, start))
        {
            return ChangeTableOwner(start);
        }

        if (Nearest(alias, start, qualifier: null) is not { } definition ||
            !_aliasSources.TryGetValue(definition.DefinedAt, out var source))
        {
            return null;
        }

        // FROM inserted i 的 i 指的是觸發程序的父資料表。
        if (source.Database is null && IsChangeTable(source.Name, definition.DefinedAt))
        {
            return ChangeTableOwner(definition.DefinedAt);
        }

        return Project(source);
    }

    /// <summary>
    /// <paramref name="name"/> 是 <paramref name="start"/> 這裡引用得到的 <c>inserted</c>／<c>deleted</c>：
    /// 在 DML 觸發程序那一句裡，或在 OUTPUT 子句裡。
    /// </summary>
    public bool IsChangeTable(string name, int start) =>
        AuditText.Normalize(name) is "INSERTED" or "DELETED" && InnermostChangeRange(start) is not null;

    /// <summary>
    /// <paramref name="start"/> 起頭的欄位可能屬於的資料表（最後一段）與寫出來的資料庫，由內往外一層一份；說不出來時是 null。
    /// </summary>
    /// <remarks>
    /// <c>INSERT INTO Other.dbo.Loan (CopyNo)</c>、<c>CREATE INDEX … ON Copy (CopyNo)</c> 的 CopyNo 只屬於那一張表；
    /// <c>SELECT … FROM Other.dbo.Loan GROUP BY CopyNo</c> 的 CopyNo 屬於這個查詢與外層查詢的來源——衍生資料表
    /// 看不到它所在那一層的來源（APPLY 右邊除外）。來源裡有一個不是具名資料表（衍生資料表、函式、資料表變數、
    /// <c>inserted</c>）就說不出來；指令碼取過的名稱（別名、CTE 的資料行）不是資料庫的欄位，也不在此列。
    /// 暫存資料表與 <see cref="SourceOf"/> 一樣看 <c>SELECT * INTO</c> 的那張表。
    /// 分層是因為 T-SQL 由內往外找：內層查不到的表可能就有這個欄位，外層的表就說不上是它的擁有者。
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<(string Name, string? Database)>>? ColumnOwners(int start) =>
        _columnOwners.TryGetValue(start, out var levels)
            ? levels.Select(level => (IReadOnlyList<(string Name, string? Database)>)level.Select(Project).ToArray()).ToArray()
            : null;

    /// <summary>暫存資料表是 <c>SELECT * INTO</c> 一張表時換成那張表。</summary>
    private (string Name, string? Database) Project((string Name, string? Database) table) =>
        _starSources.TryGetValue(AuditText.Normalize(table.Name), out var projected) ? projected : table;

    /// <summary>
    /// <paramref name="start"/> 這裡的 <c>inserted</c>／<c>deleted</c> 指的資料表；目標不是具名資料表時是 null。
    /// </summary>
    /// <remarks>
    /// OUTPUT 在觸發程序裡時看最內層：那句 DML 的 OUTPUT 指它自己的目標。目標寫成別名
    /// （<c>UPDATE l SET … OUTPUT … FROM Loan l</c>）時看同一句裡取那個別名的資料表。
    /// </remarks>
    private (string Name, string? Database)? ChangeTableOwner(int start)
    {
        if (InnermostChangeRange(start) is not { Owner: { BaseIdentifier.Value: { } name } owner })
        {
            return null;
        }

        return TargetAlias(start) is { } definedAt
            ? _aliasSources[definedAt]
            : (name, owner.DatabaseIdentifier?.Value);
    }

    /// <summary>
    /// <paramref name="start"/> 這裡的 <c>inserted</c>／<c>deleted</c> 指 DML 的目標，而目標是同一句 FROM 才取的別名
    /// （<c>UPDATE l SET … OUTPUT inserted.⎵ FROM Loan l</c>）：截斷處只有一個叫 <c>l</c> 的名字，說不出是哪一張表。
    /// </summary>
    public bool NamesLaterTarget(string name, int start) =>
        IsChangeTable(name, start) && TargetAlias(start) is { } definedAt && definedAt > start;

    /// <summary>DML 的目標寫成別名時，同一句裡取那個別名的位置。</summary>
    private int? TargetAlias(int start)
    {
        if (InnermostChangeRange(start) is { Owner: { BaseIdentifier.Value: { } name, SchemaIdentifier: null, DatabaseIdentifier: null } } &&
            _scoped.TryGetValue(AuditText.Normalize(name), out var definitions) &&
            InnermostRange(_statements, start) is { } statement)
        {
            foreach (var definition in definitions)
            {
                if (definition.DefinedAt >= statement.Start &&
                    definition.DefinedAt < statement.End &&
                    _aliasSources.ContainsKey(definition.DefinedAt))
                {
                    return definition.DefinedAt;
                }
            }
        }

        return null;
    }

    private (int Start, int End, SchemaObjectName? Owner)? InnermostChangeRange(int offset)
    {
        (int Start, int End, SchemaObjectName? Owner)? innermost = null;

        foreach (var range in _changeTables)
        {
            if (range.Start <= offset &&
                offset < range.End &&
                (innermost is not { } current || range.End - range.Start < current.End - current.Start))
            {
                innermost = range;
            }
        }

        return innermost;
    }

    /// <summary>
    /// <paramref name="qualifier"/> 在 <paramref name="start"/> 指的是 CTE 的名稱：當限定字要這個查詢的 FROM 寫出它，
    /// 與別名不同——別名取在哪一層就在哪一層看得到，CTE 名稱在整句裡都「取過」。
    /// </summary>
    public bool NamesTable(string qualifier, int start) =>
        Nearest(qualifier, start, qualifier: null) is { } definition && _tableNames.Contains(definition.DefinedAt);

    /// <summary>
    /// <paramref name="start"/> 在一個查詢的選取清單裡，而那個查詢的 FROM 寫在它後面：截斷之後
    /// 資料來源還沒出現，欄位誰都列不出來。
    /// </summary>
    /// <remarks>
    /// UPDATE 的目標是 FROM 才取的別名（<c>UPDATE l SET Fee = 1, CopyNo = 2 FROM Loan l</c>）時同理：截斷處只有
    /// 一個叫 <c>l</c> 的名字，SET 到 FROM 之間的欄位不知道屬於哪一張表。
    /// </remarks>
    public bool NeedsLaterFrom(int start)
    {
        (int Start, int End)? query = InnermostRange(_queries, start);

        if (query is { } range)
        {
            return _fromStarts.TryGetValue(range.Start, out var from) && start < from;
        }

        return _aliasedTargets.Exists(target => target.Start <= start && start < target.From);
    }

    /// <summary>範圍內、<paramref name="start"/> 之前最近的那一次取名。</summary>
    private Visibility? Nearest(string name, int start, string? qualifier)
    {
        if (!_scoped.TryGetValue(AuditText.Normalize(name), out var definitions))
        {
            return null;
        }

        Visibility? nearest = null;

        foreach (var definition in definitions)
        {
            if (definition.DefinedAt < start &&
                definition.From <= start &&
                start < definition.To &&
                Reaches(definition, start, qualifier) &&
                (nearest is null || definition.DefinedAt > nearest.Value.DefinedAt))
            {
                nearest = definition;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 範圍內的取名在 <paramref name="start"/> 引用得到：點號之後只有資料行清單算，而且限定字要指那張表
    /// （CTE 名稱、衍生資料表的別名，或別名指的 CTE）；沒寫限定字的資料行清單名稱，要那張表是這個欄位可能屬於的表之一。
    /// </summary>
    private bool Reaches(Visibility definition, int start, string? qualifier)
    {
        if (definition.Table is not { } table)
        {
            return qualifier is null;
        }

        if (qualifier is not null)
        {
            return Same(qualifier, table) || SourceOf(qualifier, start) is { } source && Same(source.Name, table);
        }

        if (_listOwners.TryGetValue(start, out var owner))
        {
            return Same(owner, table);
        }

        return EnclosingSourceScopes(start).Any(scope => _sourceScopes[scope].Names.Contains(AuditText.Normalize(table)));
    }

    private static bool Same(string left, string right) =>
        string.Equals(AuditText.Normalize(left), AuditText.Normalize(right), StringComparison.Ordinal);

    public static AuditDefinitions Collect(string batch)
    {
        var tokens = SqlTokenizer.Tokenize(batch);
        return Collect(batch, tokens, SqlStatementHeads.Find(batch, tokens));
    }

    /// <param name="tokens"><paramref name="batch"/> 的詞元。</param>
    /// <param name="heads">每一句第一個詞元的索引（<see cref="SqlStatementHeads.Find"/>）。</param>
    public static AuditDefinitions Collect(string batch, IReadOnlyList<SqlToken> tokens, IReadOnlyList<int> heads)
    {
        var definitions = new AuditDefinitions { _batchEnd = batch.Length + 1 };
        var parsed = batch;
        TSqlFragment? fragment;

        // ScriptDom 停在第一個錯、不回報之後的；挖掉那一句再剖析，才找得到下一句的錯，也才認得到之後的名稱。
        while (true)
        {
            fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(new StringReader(parsed), out var errors);

            if (errors.Count == 0 || !definitions.SkipStatement(ref parsed, errors.Min(error => error.Offset), tokens, heads))
            {
                break;
            }
        }

        fragment?.Accept(new Collector(definitions));
        definitions._nameReferences.ExceptWith(definitions._notNames);
        definitions.Resolve();
        return definitions;
    }

    /// <summary>
    /// 記下 <paramref name="offset"/> 那個錯起到那一句結束的詞元，並把那一句整句挖成空白（位置不變）；
    /// 已經挖掉的位置又報錯時回傳 false。
    /// </summary>
    /// <remarks>
    /// 截斷的指令碼，剖析器常在最後一個詞就報錯、不等到結尾（<c>IN (</c> 報在 <c>(</c>）；那是還沒寫完，
    /// 不是寫錯，不記剖析失敗、照常稽核。錯的後面還有詞才算剖析不過。寫到一半的那一句一樣挖掉：
    /// 剖析器有錯就不給語法樹，不挖的話之前寫完的句子也沒有角色（<see cref="HasRole"/>）。
    /// </remarks>
    private bool SkipStatement(ref string parsed, int offset, IReadOnlyList<SqlToken> tokens, IReadOnlyList<int> heads)
    {
        var position = 0;

        while (position < tokens.Count && tokens[position].End <= offset)
        {
            position++;
        }

        var last = Math.Min(position, tokens.Count - 1);

        // 已經挖掉的位置不會再報錯；真的遇到就停，不重複剖析同一段。
        if (last < 0 || char.IsWhiteSpace(parsed[tokens[last].Start]))
        {
            return false;
        }

        var head = heads.LastOrDefault(index => index <= last);
        var next = heads.Where(index => index > last).DefaultIfEmpty(tokens.Count).First();

        // 錯的後面只剩挖掉的句子（IF … BEGIN 裡寫到一半的那一句）也是寫到一半。
        var text = parsed;

        if (tokens.Skip(position + 1).Any(token => !char.IsWhiteSpace(text[token.Start])))
        {
            for (var index = position; index < next; index++)
            {
                _unparsed.Add(tokens[index].Start);
            }

            UnparsedStatements++;
        }

        var end = next < tokens.Count ? tokens[next].Start : parsed.Length;
        var blanked = parsed.ToCharArray();

        for (var index = tokens[head].Start; index < end; index++)
        {
            if (blanked[index] is not ('\r' or '\n'))
            {
                blanked[index] = ' ';
            }
        }

        parsed = new string(blanked);
        return true;
    }

    private void Add(TSqlFragment? name, Scope scope, string? table = null)
    {
        if (name is null || name.StartOffset < 0)
        {
            return;
        }

        _starts.Add(name.StartOffset);

        if (scope != Scope.None)
        {
            _pending.Add((name, scope, table));
        }
    }

    /// <summary>別名指的是資料庫裡的資料表時記下名稱：資料表查不到，別名之後的欄位就無從判斷。</summary>
    private void AddSource(Identifier? alias, TableReference? source)
    {
        if (alias is { StartOffset: >= 0 } && source is NamedTableReference { SchemaObject: { BaseIdentifier.Value: { } name } path })
        {
            _aliasSources[alias.StartOffset] = (name, path.DatabaseIdentifier?.Value);
        }
    }

    /// <summary>
    /// 記下資料行清單裡每一個欄位屬於 <paramref name="owner"/>；擁有者不是具名資料表時不記。
    /// 清單在定義那張表的 <c>CREATE TABLE</c> 裡時（指回自己的外部索引鍵），改成讓那份定義的資料行在清單裡引用得到。
    /// </summary>
    private void AddColumns(IEnumerable<TSqlFragment?>? columns, SchemaObjectName? owner)
    {
        var identifiers = ColumnIdentifiers(columns);

        if (owner is not { BaseIdentifier.Value: { } name } || identifiers.Count == 0)
        {
            return;
        }

        if (DefinitionOf(owner, identifiers[0].StartOffset) is { } definition)
        {
            AddDefinedColumns(identifiers, definition);
            return;
        }

        var owners = new[] { new[] { (name, owner.DatabaseIdentifier?.Value) } };

        foreach (var identifier in identifiers)
        {
            _columnOwners[identifier.StartOffset] = owners;
            _listOwners[identifier.StartOffset] = name;
        }
    }

    /// <summary>讓 <paramref name="definition"/> 的資料行在這份清單裡引用得到。</summary>
    private void AddDefinedColumns(IReadOnlyList<Identifier> identifiers, IList<ColumnDefinition> definition)
    {
        if (identifiers.Count == 0)
        {
            return;
        }

        var last = identifiers[identifiers.Count - 1];

        foreach (var column in definition)
        {
            if (column.ColumnIdentifier is { StartOffset: >= 0 } defined)
            {
                AddVisibility(defined, identifiers[0].StartOffset, last.StartOffset + last.FragmentLength);
            }
        }
    }

    private static IReadOnlyList<Identifier> ColumnIdentifiers(IEnumerable<TSqlFragment?>? columns) =>
        (columns ?? Array.Empty<TSqlFragment?>())
            .Select(column => column switch
            {
                Identifier identifier => identifier,
                ColumnReferenceExpression reference => reference.MultiPartIdentifier?.Identifiers.LastOrDefault(),
                ColumnWithSortOrder sorted => sorted.Column?.MultiPartIdentifier?.Identifiers.LastOrDefault(),
                _ => null
            })
            .OfType<Identifier>()
            .Where(identifier => identifier.StartOffset >= 0)
            .ToList();

    /// <summary><paramref name="offset"/> 在定義 <paramref name="table"/> 的 <c>CREATE TABLE</c> 裡時，那份定義的資料行。</summary>
    private IList<ColumnDefinition>? DefinitionOf(SchemaObjectName table, int offset)
    {
        foreach (var created in _createdTables)
        {
            if (created.Start <= offset &&
                offset < created.End &&
                AuditText.Normalize(created.Name.BaseIdentifier.Value) == AuditText.Normalize(table.BaseIdentifier.Value) &&
                (created.Name.SchemaIdentifier is null ||
                    table.SchemaIdentifier is null ||
                    AuditText.Normalize(created.Name.SchemaIdentifier.Value) == AuditText.Normalize(table.SchemaIdentifier.Value)))
            {
                return created.Columns;
            }
        }

        return null;
    }

    private void AddVisibility(Identifier name, int from, int to)
    {
        var key = AuditText.Normalize(name.Value);

        if (!_scoped.TryGetValue(key, out var list))
        {
            _scoped[key] = list = new List<Visibility>();
        }

        list.Add(new Visibility(name.StartOffset, from, to, table: null));
    }

    /// <summary>
    /// 一個查詢（或 UPDATE、DELETE、MERGE）引用得到的資料來源；衍生資料表在它那一層畫一道牆，裡面看不到這一層的來源。
    /// </summary>
    private void AddSourceScope(TSqlFragment node, IEnumerable<TableReference?> references)
    {
        if (node.StartOffset < 0)
        {
            return;
        }

        var sources = new List<(string Name, string? Database)?>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var scope = _sourceScopes.Count;
        _sourceScopes.Add((node.StartOffset, node.StartOffset + node.FragmentLength, sources, names));

        foreach (var reference in references)
        {
            AddSources(reference, sources, names, scope, wall: true);
        }
    }

    /// <param name="names">
    /// 資料行清單的表在這一層的名字：具名來源寫的名稱（CTE 寫成 <c>Parts AS p</c> 也是 Parts），其餘來源的別名。
    /// </param>
    private void AddSources(
        TableReference? reference,
        List<(string Name, string? Database)?> sources,
        HashSet<string> names,
        int scope,
        bool wall)
    {
        switch (reference)
        {
            case null:
                return;
            case JoinParenthesisTableReference group:
                AddSources(group.Join, sources, names, scope, wall);
                return;
            case QualifiedJoin join:
                AddSources(join.FirstTableReference, sources, names, scope, wall);
                AddSources(join.SecondTableReference, sources, names, scope, wall);
                return;
            case UnqualifiedJoin join:
                AddSources(join.FirstTableReference, sources, names, scope, wall);

                // APPLY 右邊看得到左邊的來源。
                AddSources(
                    join.SecondTableReference,
                    sources,
                    names,
                    scope,
                    wall && join.UnqualifiedJoinType is not (UnqualifiedJoinType.CrossApply or UnqualifiedJoinType.OuterApply));
                return;
            case NamedTableReference { SchemaObject: { BaseIdentifier.Value: { } name } path }
                when AuditText.Normalize(name) is not ("INSERTED" or "DELETED"):
                sources.Add((name, path.DatabaseIdentifier?.Value));
                names.Add(AuditText.Normalize(name));
                return;
            default:
                sources.Add(null);

                if (reference is TableReferenceWithAlias { Alias.Value: { } alias })
                {
                    names.Add(AuditText.Normalize(alias));
                }

                if (wall && reference is QueryDerivedTable { StartOffset: >= 0 } derived)
                {
                    _barriers.Add((derived.StartOffset, derived.StartOffset + derived.FragmentLength, scope));
                }

                return;
        }
    }

    /// <summary>
    /// <paramref name="offset"/> 這裡沒寫限定字的欄位可能屬於的資料表：最內層查詢往外每一層的來源，一層一份，
    /// 被衍生資料表擋住的那一層不算；有一個說不出是哪張表、或一個來源都沒有時是 null。
    /// </summary>
    private IReadOnlyList<IReadOnlyList<(string Name, string? Database)>>? VisibleSources(int offset)
    {
        var levels = new List<IReadOnlyList<(string Name, string? Database)>>();

        foreach (var scope in EnclosingSourceScopes(offset))
        {
            var owners = new List<(string Name, string? Database)>();

            foreach (var source in _sourceScopes[scope].Sources)
            {
                if (source is not { } named)
                {
                    return null;
                }

                owners.Add(named);
            }

            if (owners.Count > 0)
            {
                levels.Add(owners);
            }
        }

        return levels.Count == 0 ? null : levels;
    }

    /// <summary><paramref name="offset"/> 看得到來源的每一層，由內往外；被衍生資料表擋住的那一層不算。</summary>
    private IEnumerable<int> EnclosingSourceScopes(int offset)
    {
        var enclosing = Enumerable.Range(0, _sourceScopes.Count)
            .Where(scope => _sourceScopes[scope].Start <= offset && offset < _sourceScopes[scope].End)
            .OrderBy(scope => _sourceScopes[scope].End - _sourceScopes[scope].Start)
            .ToList();

        for (var level = 0; level < enclosing.Count; level++)
        {
            var scope = enclosing[level];

            if (level == 0 || !_barriers.Any(wall => wall.Scope == scope && wall.Start <= offset && offset < wall.End))
            {
                yield return scope;
            }
        }
    }

    private void Add(IEnumerable<Identifier>? names, Scope scope, string? table = null)
    {
        foreach (var name in names ?? Array.Empty<Identifier>())
        {
            Add(name, scope, table);
        }
    }

    /// <summary>範圍要等整棵樹走完才知道查詢與語句的邊界。</summary>
    private void Resolve()
    {
        foreach (var (name, scope, table) in _pending)
        {
            var text = name switch
            {
                Identifier identifier => identifier.Value,
                VariableReference variable => variable.Name,
                _ => null
            };

            if (text is null)
            {
                continue;
            }

            var start = name.StartOffset;
            var key = AuditText.Normalize(text);

            if (!_scoped.TryGetValue(key, out var list))
            {
                _scoped[key] = list = new List<Visibility>();
            }

            if (scope == Scope.OrderBy)
            {
                var statementEnd = InnermostRange(_statements, start)?.End ?? _batchEnd;

                foreach (var (from, to) in _orderBys.Where(range => range.Start > start && range.End <= statementEnd))
                {
                    list.Add(new Visibility(start, from, to, table));
                }

                continue;
            }

            var (scopeStart, end) = scope switch
            {
                Scope.Query => InnermostRange(_queries, start) ?? InnermostRange(_statements, start) ?? (0, _batchEnd),
                Scope.Statement => InnermostRange(_statements, start) ?? (0, _batchEnd),
                _ => (0, _batchEnd),
            };

            list.Add(new Visibility(start, scopeStart, end, table));
        }

        // 範圍要先建好：指令碼取過的名稱（選取清單的別名、CTE 的資料行）不是資料庫的欄位。
        foreach (var reference in _columnReferences)
        {
            var start = reference.StartOffset;

            if (!_columnOwners.ContainsKey(start) &&
                Nearest(reference.Value, start, qualifier: null) is null &&
                VisibleSources(start) is { } owners)
            {
                _columnOwners[start] = owners;
            }
        }
    }

    private static (int Start, int End)? InnermostRange(List<(int Start, int End)> ranges, int offset)
    {
        (int Start, int End)? innermost = null;
        var length = int.MaxValue;

        foreach (var (start, stop) in ranges)
        {
            if (start <= offset && offset < stop && stop - start < length)
            {
                innermost = (start, stop);
                length = stop - start;
            }
        }

        return innermost;
    }

    /// <summary>一段語法樹裡取過的資料來源別名。</summary>
    private sealed class AliasCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(TableReferenceWithAlias node)
        {
            if (node.Alias?.Value is { } alias)
            {
                Names.Add(alias);
            }
        }
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        private readonly AuditDefinitions _owner;

        public Collector(AuditDefinitions owner)
        {
            _owner = owner;
        }

        public override void Visit(QuerySpecification node)
        {
            _owner._queries.Add((node.StartOffset, node.StartOffset + node.FragmentLength));
            _owner.AddSourceScope(node, node.FromClause?.TableReferences ?? (IList<TableReference>)Array.Empty<TableReference>());

            if (node.FromClause is { StartOffset: >= 0 } from)
            {
                _owner._fromStarts[node.StartOffset] = from.StartOffset;
            }
        }

        /// <summary>
        /// 查詢自己的 ORDER BY；視窗與 WITHIN GROUP 的不算，選取清單的別名在那裡看不到：
        /// <c>ROUND(Fee, 2) AS Fee, ROW_NUMBER() OVER (ORDER BY Fee)</c> 的 Fee 是來源的欄位。
        /// </summary>
        public override void Visit(QueryExpression node)
        {
            if (node.OrderByClause is { StartOffset: >= 0 } order)
            {
                _owner._orderBys.Add((order.StartOffset, order.StartOffset + order.FragmentLength));
            }
        }

        public override void Visit(Identifier node) => _owner._nameReferences.Add(node.StartOffset);

        public override void Visit(FunctionCall node)
        {
            if (node.FunctionName is { } name)
            {
                _owner._notNames.Add(name.StartOffset);
            }
        }

        /// <summary>型別名稱在語法樹上也是多段名稱；內建型別是字，交給字的名單判斷。</summary>
        public override void Visit(DataTypeReference node)
        {
            foreach (var part in node.Name?.Identifiers ?? (IList<Identifier>)Array.Empty<Identifier>())
            {
                _owner._notNames.Add(part.StartOffset);
            }
        }

        public override void Visit(TableReferenceWithAlias node)
        {
            _owner.Add(node.Alias, Scope.Query);
            _owner.AddSource(node.Alias, node);
        }

        public override void Visit(TableReferenceWithAliasAndColumns node) => _owner.Add(node.Columns, Scope.Query, node.Alias?.Value);

        /// <summary>MERGE 目標的別名不在 TableReferenceWithAlias 上，引用得到整句（ON、動作子句、OUTPUT）。</summary>
        public override void Visit(MergeSpecification node)
        {
            _owner.Add(node.TableAlias, Scope.Statement);
            _owner.AddSource(node.TableAlias, node.Target);
            _owner.AddSourceScope(node, new[] { node.Target, node.TableReference });

            var target = (node.Target as NamedTableReference)?.SchemaObject;

            foreach (var clause in node.ActionClauses)
            {
                switch (clause.Action)
                {
                    case InsertMergeAction insert:
                        _owner.AddColumns(insert.Columns, target);
                        break;
                    case UpdateMergeAction update:
                        _owner.AddColumns(SetColumns(update.SetClauses), target);
                        break;
                }
            }
        }

        /// <summary>SET 的左邊屬於目標；目標是 FROM 才取的別名時說不出是哪一張，留給範圍內的來源。</summary>
        public override void Visit(UpdateSpecification node)
        {
            _owner.AddSourceScope(node, WithTarget(node.Target, node.FromClause));

            if (node.FromClause is { StartOffset: >= 0 } from && NamesAlias(node.Target, from))
            {
                _owner._aliasedTargets.Add((node.StartOffset, from.StartOffset));
            }
            else
            {
                _owner.AddColumns(SetColumns(node.SetClauses), (node.Target as NamedTableReference)?.SchemaObject);
            }
        }

        private static IEnumerable<TSqlFragment?> SetColumns(IEnumerable<SetClause> clauses) =>
            clauses.OfType<AssignmentSetClause>().Select(clause => clause.Column);

        public override void Visit(DeleteSpecification node) => _owner.AddSourceScope(node, WithTarget(node.Target, node.FromClause));

        /// <summary>沒寫限定字的一段名稱：之後在 <see cref="Resolve"/> 依範圍內的來源判斷屬於哪些表。</summary>
        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType == ColumnType.Regular && node.MultiPartIdentifier is { Count: 1 } path && path.Identifiers[0].StartOffset >= 0)
            {
                _owner._columnReferences.Add(path.Identifiers[0]);
            }
        }

        public override void Visit(InsertSpecification node) =>
            _owner.AddColumns(node.Columns, (node.Target as NamedTableReference)?.SchemaObject);

        public override void Visit(CreateIndexStatement node)
        {
            _owner.AddColumns(node.Columns, node.OnName);
            _owner.AddColumns(node.IncludeColumns, node.OnName);
        }

        public override void Visit(CreateColumnStoreIndexStatement node)
        {
            _owner.AddColumns(node.Columns, node.OnName);
            _owner.AddColumns(node.OrderedColumns, node.OnName);
        }

        public override void Visit(CreateStatisticsStatement node) => _owner.AddColumns(node.Columns, node.OnName);

        /// <summary>記下正在定義的表：指回自己的外部索引鍵引用的是這一份的資料行。</summary>
        public override void Visit(CreateTableStatement node)
        {
            if (node.SchemaObjectName is { BaseIdentifier: not null } name && node.Definition is { } definition)
            {
                _owner._createdTables.Add((node.StartOffset, node.StartOffset + node.FragmentLength, name, definition.ColumnDefinitions));
            }
        }

        /// <summary>
        /// 資料表定義（<c>CREATE TABLE</c>、<c>DECLARE @t TABLE</c>、<c>CREATE TYPE … AS TABLE</c>）自己的條件約束與
        /// 索引清單引用得到它的資料行，資料表或資料行層級都算。
        /// </summary>
        public override void Visit(TableDefinition node)
        {
            foreach (var list in ElementLists(node))
            {
                _owner.AddDefinedColumns(ColumnIdentifiers(list), node.ColumnDefinitions);
            }
        }

        /// <summary><c>ALTER TABLE t ADD</c> 的條件約束清單屬於 <c>t</c>。</summary>
        public override void Visit(AlterTableAddTableElementStatement node)
        {
            foreach (var list in ElementLists(node.Definition))
            {
                _owner.AddColumns(list, node.SchemaObjectName);
            }
        }

        /// <summary>參考的那一邊屬於被參考的資料表；自己這一邊由所在的定義記。</summary>
        public override void Visit(ForeignKeyConstraintDefinition node) =>
            _owner.AddColumns(node.ReferencedTableColumns, node.ReferenceTableName);

        /// <summary>DML 的目標是一段沒有別名的名稱，而 FROM 有一個來源以它當別名。</summary>
        private static bool NamesAlias(TableReference? target, FromClause from)
        {
            if (target is not NamedTableReference { Alias: null, SchemaObject: { Count: 1, BaseIdentifier.Value: { } name } })
            {
                return false;
            }

            var aliases = new AliasCollector();
            from.Accept(aliases);
            return aliases.Names.Contains(name);
        }

        private static IEnumerable<TableReference?> WithTarget(TableReference? target, FromClause? from) =>
            new[] { target }.Concat(from?.TableReferences ?? (IList<TableReference>)Array.Empty<TableReference>());

        /// <summary>定義裡寫資料行名稱的清單：條件約束、索引與 <c>PERIOD FOR SYSTEM_TIME</c>，資料表與資料行層級都算。</summary>
        private static IEnumerable<IEnumerable<TSqlFragment?>> ElementLists(TableDefinition? definition)
        {
            if (definition is null)
            {
                yield break;
            }

            foreach (var constraint in definition.TableConstraints.Concat(definition.ColumnDefinitions.SelectMany(column => column.Constraints)))
            {
                switch (constraint)
                {
                    case UniqueConstraintDefinition unique:
                        yield return unique.Columns;
                        break;
                    case ForeignKeyConstraintDefinition foreignKey:
                        yield return foreignKey.Columns;
                        break;
                    case DefaultConstraintDefinition { Column: { } column }:
                        yield return new[] { column };
                        break;
                }
            }

            foreach (var index in definition.Indexes.Concat(definition.ColumnDefinitions.Select(column => column.Index).OfType<IndexDefinition>()))
            {
                yield return index.Columns;
                yield return index.IncludeColumns;
            }

            if (definition.SystemTimePeriod is { } period)
            {
                yield return new TSqlFragment?[] { period.StartTimeColumn, period.EndTimeColumn };
            }
        }

        /// <summary>DML 觸發程序：整句裡 <c>inserted</c>／<c>deleted</c> 指父資料表。DDL 與登入觸發程序沒有它們。</summary>
        public override void Visit(TriggerStatementBody node)
        {
            if (node.TriggerObject is { TriggerScope: TriggerScope.Normal } trigger)
            {
                _owner._changeTables.Add((node.StartOffset, node.StartOffset + node.FragmentLength, trigger.Name));
            }
        }

        /// <summary>OUTPUT 子句裡 <c>inserted</c>／<c>deleted</c> 指那句 DML 的目標。</summary>
        public override void Visit(DataModificationSpecification node)
        {
            var target = (node.Target as NamedTableReference)?.SchemaObject;

            foreach (var clause in new TSqlFragment?[] { node.OutputClause, node.OutputIntoClause })
            {
                if (clause is { StartOffset: >= 0 })
                {
                    _owner._changeTables.Add((clause.StartOffset, clause.StartOffset + clause.FragmentLength, target));
                }
            }
        }

        public override void Visit(SelectScalarExpression node) => _owner.Add(node.ColumnName?.Identifier, Scope.OrderBy);

        public override void Visit(CommonTableExpression node)
        {
            _owner.Add(node.ExpressionName, Scope.Statement);

            if (node.ExpressionName is { StartOffset: >= 0 } name)
            {
                _owner._tableNames.Add(name.StartOffset);
            }

            _owner.Add(node.Columns, Scope.Statement, node.ExpressionName?.Value);
        }

        public override void Visit(DeclareVariableElement node) => _owner.Add(node.VariableName, Scope.Batch);

        public override void Visit(DeclareTableVariableBody node) => _owner.Add(node.VariableName, Scope.Batch);

        public override void Visit(ColumnDefinition node) => _owner.Add(node.ColumnIdentifier, Scope.None);

        public override void Visit(ConstraintDefinition node) => _owner.Add(node.ConstraintIdentifier, Scope.None);

        public override void Visit(IndexDefinition node) => _owner.Add(node.Name, Scope.None);

        public override void Visit(DeclareCursorStatement node) => _owner.Add(node.Name, Scope.Batch);

        public override void Visit(SelectStatement node)
        {
            var into = node.Into?.BaseIdentifier;
            _owner.Add(into, IsTemporary(into) ? Scope.Batch : Scope.None);

            // 選取清單只有 * 而來源是一張表：暫存資料表的欄位全從那張表來。
            if (IsTemporary(into) &&
                node.QueryExpression is QuerySpecification { FromClause.TableReferences: { Count: 1 } references } query &&
                query.SelectElements.All(element => element is SelectStarExpression) &&
                references[0] is NamedTableReference { SchemaObject: { BaseIdentifier.Value: { } name } path })
            {
                _owner._starSources[AuditText.Normalize(into!.Value)] = (name, path.DatabaseIdentifier?.Value);
            }
        }

        public override void Visit(BeginTransactionStatement node) => _owner.Add(node.Name?.Identifier, Scope.None);

        public override void Visit(SaveTransactionStatement node) => _owner.Add(node.Name?.Identifier, Scope.None);

        /// <summary>具名視窗在它那個查詢裡引用得到：<c>OVER w</c>，以及 WINDOW 子句裡另一個視窗的基底。</summary>
        public override void Visit(WindowDefinition node) => _owner.Add(node.WindowName, Scope.Query);

        /// <summary>
        /// 每一句的範圍；CREATE 的目標：最後一段是新取的，前面的結構描述與資料庫是既有的。
        /// </summary>
        /// <remarks>
        /// 八十幾種 CREATE 語句的名稱屬性叫法不一（<c>Name</c>、<c>SchemaObjectName</c>、
        /// <c>ProcedureReference</c>、<c>DatabaseName</c>），逐一覆寫會漏掉新版才有的那幾種，
        /// 所以照名稱讀屬性；ALTER 與 DROP 的目標是既有的，不在這裡。只有暫存資料表之後引用得回來：
        /// 其餘的目標在資料庫裡，由名稱索引回答。
        /// </remarks>
        public override void Visit(TSqlStatement node)
        {
            _owner._statements.Add((node.StartOffset, node.StartOffset + node.FragmentLength));

            if (!node.GetType().Name.StartsWith("Create", StringComparison.Ordinal))
            {
                return;
            }

            foreach (var property in new[] { "Name", "SchemaObjectName", "ProcedureReference", "DatabaseName" })
            {
                var name = LastPart(node.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(node));
                _owner.Add(name, IsTemporary(name) ? Scope.Batch : Scope.None);
            }
        }

        private static bool IsTemporary(TSqlFragment? name) =>
            name is Identifier { Value: { } value } && value.StartsWith("#", StringComparison.Ordinal);

        private static TSqlFragment? LastPart(object? value) => value switch
        {
            Identifier identifier => identifier,
            SchemaObjectName name => name.BaseIdentifier,
            ProcedureReference procedure => procedure.Name?.BaseIdentifier,
            IdentifierOrValueExpression either => either.Identifier,
            _ => null
        };
    }
}
