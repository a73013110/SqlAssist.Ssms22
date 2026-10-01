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
/// 分支還沒寫 FROM 時，上一個分支的同名別名不算數。點號之後只有衍生資料表與 CTE 的資料行清單算——
/// <c>e.OrganizationNode</c> 是 e 的欄位，不是同一句裡叫 OrganizationNode 的選取別名。資料行、條件約束與索引的定義只記「這裡是新取的」：引用它們要先知道是
/// 哪一張表，與資料庫的欄位同一類（<see cref="IAuditNameIndex"/>），只看名字相同會認錯——
/// <c>CREATE TABLE Loan (CopyNo int REFERENCES Copy (CopyNo))</c> 的第二個 CopyNo 是 Copy 的欄位。
///
/// <c>inserted</c>／<c>deleted</c> 不是誰取的名字，卻在兩種範圍裡引用得到：DML 觸發程序的整句（指父資料表）
/// 與 OUTPUT 子句（指那句 DML 的目標）。範圍外的同名詞照一般名稱判斷。
///
/// 剖析不過的那一句（<see cref="IsUnparsed"/>）挖成空白再剖析，其餘的句子照常認；那一句裡的名稱認不出來，
/// 在稽核裡歸成不明，不算漏。
/// </remarks>
public sealed class AuditDefinitions
{
    private readonly HashSet<int> _starts = new();
    private readonly HashSet<int> _nameReferences = new();
    private readonly HashSet<int> _notNames = new();
    private readonly HashSet<int> _unparsed = new();
    private readonly Dictionary<string, List<Visibility>> _scoped = new(StringComparer.Ordinal);
    private readonly List<(TSqlFragment Name, Scope Scope, bool Qualifiable)> _pending = new();
    private readonly List<(int Start, int End)> _queries = new();
    private readonly List<(int Start, int End)> _statements = new();
    private readonly List<(int Start, int End)> _orderBys = new();
    private readonly Dictionary<int, int> _fromStarts = new();
    private readonly Dictionary<int, (string Name, string? Database)> _aliasSources = new();
    private readonly Dictionary<string, (string Name, string? Database)> _starSources = new(StringComparer.Ordinal);
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

        /// <summary>同一句裡、它之後的 ORDER BY。</summary>
        OrderBy,
    }

    /// <summary>一個名稱從哪裡取、在哪一段文字裡引用得到。</summary>
    private readonly struct Visibility
    {
        public Visibility(int definedAt, int from, int to, bool qualifiable)
        {
            DefinedAt = definedAt;
            From = from;
            To = to;
            Qualifiable = qualifiable;
        }

        public int DefinedAt { get; }

        public int From { get; }

        public int To { get; }

        /// <summary>點號之後也算（衍生資料表與 CTE 的資料行清單）。</summary>
        public bool Qualifiable { get; }
    }

    /// <summary>剖析不過、從錯的那個詞起不稽核的句數。</summary>
    public int UnparsedStatements { get; private set; }

    /// <summary>
    /// <paramref name="start"/> 起頭的詞元在剖析不過的那一句裡、錯的那個詞或它之後：作者寫的不是 T-SQL，
    /// 下一個詞不是這一格的答案。錯之前的詞照常稽核——剖析器停在第一個接不下去的詞，那之前的每一格都還是
    /// 合法語句的開頭，寫到一半的指令碼也照樣稽核。
    /// </summary>
    public bool IsUnparsed(int start) => _unparsed.Contains(start);

    /// <summary><paramref name="start"/> 起頭的那個名稱是新取的。</summary>
    public bool IsDefinition(int start) => _starts.Contains(start);

    /// <summary>
    /// <paramref name="start"/> 起頭的詞元站在名稱的位置上：語法樹上的識別字，型別名稱與函式名稱除外
    /// （內建型別與函式是字，由字的名單判斷）。
    /// </summary>
    public bool IsNameReference(int start) => _nameReferences.Contains(start);

    /// <summary><paramref name="name"/> 在 <paramref name="start"/> 之前取過，而且這裡還在它的範圍內。</summary>
    /// <param name="qualified">這個名稱接在點號之後。</param>
    public bool IsDefinedBefore(string name, int start, bool qualified = false) =>
        Nearest(name, start, qualified) is not null;

    /// <summary><paramref name="name"/> 是之前取過的資料行名稱（CTE 或衍生資料表的資料行清單）。</summary>
    public bool IsColumnDefinedBefore(string name, int start) => Nearest(name, start, qualified: true) is not null;

    /// <summary>
    /// <paramref name="name"/> 在 <paramref name="start"/> 指的是之後才取的那一次：截斷的地方還沒寫到，
    /// 產品與 SSMS 都不可能認得（<c>SELECT r.ReaderId FROM Lib_Reader r</c> 的 r）。
    /// </summary>
    /// <remarks>
    /// 指的是範圍最內層的那一次，不是之前最近的那一次：<c>FROM #Loan a JOIN (SELECT a.CopyNo FROM Copy a)</c>
    /// 的 <c>a.</c> 是子查詢之後才取的 Copy，外層的 #Loan 被它遮住。只看「之前取過」的話，截斷處認得的外層
    /// 那一個會讓 Copy 的欄位看起來該列。
    /// </remarks>
    /// <param name="qualified">這個名稱接在點號之後。</param>
    public bool IsDefinedLater(string name, int start, bool qualified = false)
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
                qualified && !definition.Qualifiable)
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

        if (Nearest(alias, start, qualified: false) is not { } definition ||
            !_aliasSources.TryGetValue(definition.DefinedAt, out var source))
        {
            return null;
        }

        // FROM inserted i 的 i 指的是觸發程序的父資料表。
        if (source.Database is null && IsChangeTable(source.Name, definition.DefinedAt))
        {
            return ChangeTableOwner(definition.DefinedAt);
        }

        return _starSources.TryGetValue(AuditText.Normalize(source.Name), out var projected) ? projected : source;
    }

    /// <summary>
    /// <paramref name="name"/> 是 <paramref name="start"/> 這裡引用得到的 <c>inserted</c>／<c>deleted</c>：
    /// 在 DML 觸發程序那一句裡，或在 OUTPUT 子句裡。
    /// </summary>
    public bool IsChangeTable(string name, int start) =>
        AuditText.Normalize(name) is "INSERTED" or "DELETED" && InnermostChangeRange(start) is not null;

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

        if (owner.SchemaIdentifier is null &&
            owner.DatabaseIdentifier is null &&
            _scoped.TryGetValue(AuditText.Normalize(name), out var definitions) &&
            InnermostRange(_statements, start) is { } statement)
        {
            foreach (var definition in definitions)
            {
                if (definition.DefinedAt >= statement.Start &&
                    definition.DefinedAt < statement.End &&
                    _aliasSources.TryGetValue(definition.DefinedAt, out var aliased))
                {
                    return aliased;
                }
            }
        }

        return (name, owner.DatabaseIdentifier?.Value);
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
        Nearest(qualifier, start, qualified: false) is { } definition && _tableNames.Contains(definition.DefinedAt);

    /// <summary>
    /// <paramref name="start"/> 在一個查詢的選取清單裡，而那個查詢的 FROM 寫在它後面：截斷之後
    /// 資料來源還沒出現，欄位誰都列不出來。
    /// </summary>
    public bool NeedsLaterFrom(int start)
    {
        (int Start, int End)? query = InnermostRange(_queries, start);
        return query is { } range && _fromStarts.TryGetValue(range.Start, out var from) && start < from;
    }

    /// <summary>範圍內、<paramref name="start"/> 之前最近的那一次取名。</summary>
    private Visibility? Nearest(string name, int start, bool qualified)
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
                (!qualified || definition.Qualifiable) &&
                (nearest is null || definition.DefinedAt > nearest.Value.DefinedAt))
            {
                nearest = definition;
            }
        }

        return nearest;
    }

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
    /// 錯在最後一個詞或結尾（寫到一半）時沒有要記的，回傳 false。
    /// </summary>
    /// <remarks>
    /// 截斷的指令碼，剖析器常在最後一個詞就報錯、不等到結尾（<c>IN (</c> 報在 <c>(</c>）；那是還沒寫完，
    /// 不是寫錯。錯的後面還有詞才算剖析不過。
    /// </remarks>
    private bool SkipStatement(ref string parsed, int offset, IReadOnlyList<SqlToken> tokens, IReadOnlyList<int> heads)
    {
        var position = 0;

        while (position < tokens.Count && tokens[position].End <= offset)
        {
            position++;
        }

        // 已經挖掉的位置不會再報錯；真的遇到就停，不重複剖析同一段。
        if (position >= tokens.Count - 1 || char.IsWhiteSpace(parsed[tokens[position].Start]))
        {
            return false;
        }

        var head = heads.LastOrDefault(index => index <= position);
        var next = heads.Where(index => index > position).DefaultIfEmpty(tokens.Count).First();

        for (var index = position; index < next; index++)
        {
            _unparsed.Add(tokens[index].Start);
        }

        UnparsedStatements++;
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

    private void Add(TSqlFragment? name, Scope scope, bool qualifiable = false)
    {
        if (name is null || name.StartOffset < 0)
        {
            return;
        }

        _starts.Add(name.StartOffset);

        if (scope != Scope.None)
        {
            _pending.Add((name, scope, qualifiable));
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

    private void Add(IEnumerable<Identifier>? names, Scope scope, bool qualifiable = false)
    {
        foreach (var name in names ?? Array.Empty<Identifier>())
        {
            Add(name, scope, qualifiable);
        }
    }

    /// <summary>範圍要等整棵樹走完才知道查詢與語句的邊界。</summary>
    private void Resolve()
    {
        foreach (var (name, scope, qualifiable) in _pending)
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
                    list.Add(new Visibility(start, from, to, qualifiable));
                }

                continue;
            }

            var (scopeStart, end) = scope switch
            {
                Scope.Query => InnermostRange(_queries, start) ?? InnermostRange(_statements, start) ?? (0, _batchEnd),
                Scope.Statement => InnermostRange(_statements, start) ?? (0, _batchEnd),
                _ => (0, _batchEnd),
            };

            list.Add(new Visibility(start, scopeStart, end, qualifiable));
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

            if (node.FromClause is { StartOffset: >= 0 } from)
            {
                _owner._fromStarts[node.StartOffset] = from.StartOffset;
            }
        }

        public override void Visit(OrderByClause node) =>
            _owner._orderBys.Add((node.StartOffset, node.StartOffset + node.FragmentLength));

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

        public override void Visit(TableReferenceWithAliasAndColumns node) => _owner.Add(node.Columns, Scope.Query, qualifiable: true);

        /// <summary>MERGE 目標的別名不在 TableReferenceWithAlias 上，引用得到整句（ON、動作子句、OUTPUT）。</summary>
        public override void Visit(MergeSpecification node)
        {
            _owner.Add(node.TableAlias, Scope.Statement);
            _owner.AddSource(node.TableAlias, node.Target);
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

            _owner.Add(node.Columns, Scope.Statement, qualifiable: true);
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

        public override void Visit(WindowDefinition node) => _owner.Add(node.WindowName, Scope.None);

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
