using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;

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
/// 剖析失敗時只認得到剖析出來的那一部分；認不出來的名稱在稽核裡歸成不明，不算漏。
/// </remarks>
public sealed class AuditDefinitions
{
    private readonly HashSet<int> _starts = new();
    private readonly HashSet<int> _nameReferences = new();
    private readonly HashSet<int> _notNames = new();
    private readonly Dictionary<string, List<Visibility>> _scoped = new(StringComparer.Ordinal);
    private readonly List<(TSqlFragment Name, Scope Scope, bool Qualifiable)> _pending = new();
    private readonly List<(int Start, int End)> _queries = new();
    private readonly List<(int Start, int End)> _statements = new();
    private readonly List<(int Start, int End)> _orderBys = new();
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

    /// <summary>剖析出錯（整段或其中一句）。</summary>
    public bool HasErrors { get; private set; }

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
        _scoped.TryGetValue(AuditText.Normalize(name), out var definitions) &&
        definitions.Any(definition =>
            definition.DefinedAt < start &&
            definition.From <= start &&
            start < definition.To &&
            (!qualified || definition.Qualifiable));

    public static AuditDefinitions Collect(string batch)
    {
        var definitions = new AuditDefinitions { _batchEnd = batch.Length + 1 };
        var fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(new StringReader(batch), out var errors);
        definitions.HasErrors = errors.Count > 0;
        fragment?.Accept(new Collector(definitions));
        definitions._nameReferences.ExceptWith(definitions._notNames);
        definitions.Resolve();
        return definitions;
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
                var statementEnd = Innermost(_statements, start) ?? _batchEnd;

                foreach (var (from, to) in _orderBys.Where(range => range.Start > start && range.End <= statementEnd))
                {
                    list.Add(new Visibility(start, from, to, qualifiable));
                }

                continue;
            }

            var end = scope switch
            {
                Scope.Query => Innermost(_queries, start) ?? Innermost(_statements, start) ?? _batchEnd,
                Scope.Statement => Innermost(_statements, start) ?? _batchEnd,
                _ => _batchEnd,
            };

            list.Add(new Visibility(start, 0, end, qualifiable));
        }
    }

    private static int? Innermost(List<(int Start, int End)> ranges, int offset)
    {
        int? end = null;
        var length = int.MaxValue;

        foreach (var (start, stop) in ranges)
        {
            if (start <= offset && offset < stop && stop - start < length)
            {
                end = stop;
                length = stop - start;
            }
        }

        return end;
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        private readonly AuditDefinitions _owner;

        public Collector(AuditDefinitions owner)
        {
            _owner = owner;
        }

        public override void Visit(QuerySpecification node) =>
            _owner._queries.Add((node.StartOffset, node.StartOffset + node.FragmentLength));

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

        public override void Visit(TableReferenceWithAlias node) => _owner.Add(node.Alias, Scope.Query);

        public override void Visit(TableReferenceWithAliasAndColumns node) => _owner.Add(node.Columns, Scope.Query, qualifiable: true);

        public override void Visit(SelectScalarExpression node) => _owner.Add(node.ColumnName?.Identifier, Scope.OrderBy);

        public override void Visit(CommonTableExpression node)
        {
            _owner.Add(node.ExpressionName, Scope.Statement);
            _owner.Add(node.Columns, Scope.Statement, qualifiable: true);
        }

        public override void Visit(DeclareVariableElement node) => _owner.Add(node.VariableName, Scope.Batch);

        public override void Visit(DeclareTableVariableBody node) => _owner.Add(node.VariableName, Scope.Batch);

        public override void Visit(ColumnDefinition node) => _owner.Add(node.ColumnIdentifier, Scope.None);

        public override void Visit(ConstraintDefinition node) => _owner.Add(node.ConstraintIdentifier, Scope.None);

        public override void Visit(IndexDefinition node) => _owner.Add(node.Name, Scope.None);

        public override void Visit(DeclareCursorStatement node) => _owner.Add(node.Name, Scope.Batch);

        public override void Visit(SelectStatement node) =>
            _owner.Add(node.Into?.BaseIdentifier, IsTemporary(node.Into?.BaseIdentifier) ? Scope.Batch : Scope.None);

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
