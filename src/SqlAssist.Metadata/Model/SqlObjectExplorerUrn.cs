using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Model;

/// <summary>物件總管上一個節點是哪一種東西；措辭由呈現層決定。</summary>
public enum SqlExplorerNodeKind
{
    /// <summary>資料庫底下的一個物件（資料表、檢視、模組…）。</summary>
    Object,

    Column,

    Trigger,

    Constraint,

    /// <summary>SQL Server Agent 的作業。</summary>
    Job
}

/// <summary>物件總管上的一個節點：指得到它的 URN，加上它是什麼。</summary>
/// <remarks>
/// 帶著 <see cref="Kind"/> 與 <see cref="Name"/> 而不是只交出字串，是為了讓降級說得出話：
/// 指不到條件約束而停在資料行上時，畫面上要寫得出「已改為選取資料行 DueDate」，
/// 而那句話的兩個零件都在這裡。
/// </remarks>
public readonly struct SqlExplorerNode
{
    /// <param name="ownerUrn">
    /// 這個節點畫在哪一個物件的節點底下；它自己就是那個物件（或作業）時留空。
    /// </param>
    public SqlExplorerNode(SqlExplorerNodeKind kind, string name, string urn, string ownerUrn = "")
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("節點名稱不可為空。", nameof(name));
        if (string.IsNullOrEmpty(urn)) throw new ArgumentException("節點 URN 不可為空。", nameof(urn));

        Kind = kind;
        Name = name;
        Urn = urn;
        OwnerUrn = ownerUrn ?? "";
    }

    public SqlExplorerNodeKind Kind { get; }

    /// <summary>給人看的名稱；物件是限定名稱，其餘是節點自己的名字。</summary>
    public string Name { get; }

    public string Urn { get; }

    /// <summary>這個節點畫在哪一個物件的節點底下；它自己就是那個物件時為空字串。</summary>
    /// <remarks>
    /// 資料行、條件約束與觸發程序畫在父物件底下的<b>資料夾</b>裡，而那些資料夾在樹上
    /// <b>沒有自己的位址</b>——它們的 URN 就是父物件的。「用一個 URN 從樹根指到節點」
    /// 這件事因此對它們不成立，只能先到父物件，再從它底下把資料夾走一遍。
    ///
    /// 帶著它，接線層就照這一個欄位分兩條路，不必自己維護一張「哪一種東西畫在哪一格」的
    /// 對應表——那張表已經在 <see cref="SqlObjectExplorerUrn"/> 裡了，抄第二份的症狀是
    /// 加一種節點時只改了一邊。
    /// </remarks>
    public string OwnerUrn { get; }

    public override string ToString() => Kind + ":" + Name;
}

/// <summary>
/// 物件總管節點的 URN；「在物件總管中選取」唯一組位址的地方。
/// </summary>
/// <remarks>
/// 純字串組裝，所以放在 Metadata 而不是接線層：節點路徑的對應表與跳脫規則只有一份，
/// 之後別的路徑要跳到樹上時接的是同一支。路徑照 SSMS 自己的
/// <c>sqlexplorerhier.xml</c>（節點的 <c>Xpath</c> 與 <c>UrnShell</c>）。
///
/// 每一支都交出一串<b>由精確到寬鬆</b>的候選，呼叫端依序試到第一個指得到的為止。
/// 這不是在猜寫法，而是承認樹上不一定有那個節點：使用者可能在物件總管上設了篩選器、
/// 那個版本可能沒有那一種節點。只交出最精確的那一個時，症狀是條件約束一律說「找不到」，
/// 而它所屬的那張表就在旁邊。
///
/// 伺服器那一段<b>由呼叫端給</b>，而且必須是物件總管節點自己的根 URN，不是自己拼的
/// <c>Server[@Name='…']</c>：具名執行個體與有別名的連線上，連線字串裡的名稱與樹上那一行
/// 不一樣，自己拼出來的位址對不上任何節點，而導航只回得出 false。
///
/// 跳脫與 SMO 的 <c>Urn.EscapeString</c> 同一條規則（單引號寫成兩個）。不參照 SMO 是因為
/// Metadata 只依賴 <c>System.Data</c>，而規則本身只有一行。
/// </remarks>
public static class SqlObjectExplorerUrn
{
    private static readonly SqlExplorerNode[] None = Array.Empty<SqlExplorerNode>();

    /// <summary>這一種物件在資料庫底下有沒有自己的節點。</summary>
    /// <remarks>
    /// 觸發程序與條件約束回 false：它們畫在父物件底下，位址要先問出父物件是誰
    /// （<see cref="RequiresParent"/>），組法在 <see cref="ForChild"/>。
    /// </remarks>
    public static bool Supports(SqlObjectKind kind) => NodeType(kind) is not null;

    /// <summary>這一種物件的位址要先問一趟父物件。</summary>
    public static bool RequiresParent(SqlObjectKind kind) =>
        kind is SqlObjectKind.Trigger or SqlObjectKind.Constraint;

    /// <summary>
    /// 資料庫底下一個物件的節點；這一種物件沒有自己的節點，或名稱不齊時回傳空的。
    /// </summary>
    /// <param name="rootUrn">物件總管上那一台伺服器的根節點 URN。</param>
    /// <remarks>
    /// 結構描述缺了就不組：同名不同結構描述的物件在同一個資料庫裡是常態，
    /// 少了它導航會停在第一個同名的節點上，而使用者看不出跳錯了。
    /// </remarks>
    public static IReadOnlyList<SqlExplorerNode> ForObject(
        string rootUrn, string databaseName, string schemaName, string name, SqlObjectKind kind)
    {
        RequireRoot(rootUrn);

        if (BuildObject(rootUrn, databaseName, schemaName, name, kind) is not { } urn) return None;

        return new[] { new SqlExplorerNode(SqlExplorerNodeKind.Object, Qualify(schemaName, name), urn) };
    }

    /// <summary>
    /// 一個物件底下的資料行，指不到時退回那個物件。
    /// </summary>
    /// <remarks>
    /// 資料行命中的導航目標是<b>那一行</b>，不是整個物件：使用者搜的是 <c>CopyNo</c>，
    /// 而樹上就有那一行。退回物件是因為資料行資料夾在某些種類上不一定畫得出來，
    /// 而停在那張表上仍然是他要去的地方。
    /// </remarks>
    public static IReadOnlyList<SqlExplorerNode> ForColumn(
        string rootUrn, string databaseName, string schemaName, string name, SqlObjectKind kind, string columnName)
    {
        RequireRoot(rootUrn);

        if (BuildObject(rootUrn, databaseName, schemaName, name, kind) is not { } owner) return None;
        if (string.IsNullOrEmpty(columnName)) return ForObject(rootUrn, databaseName, schemaName, name, kind);

        return new[]
        {
            new SqlExplorerNode(
                SqlExplorerNodeKind.Column, columnName, Child(owner, "Column", columnName), owner),
            new SqlExplorerNode(SqlExplorerNodeKind.Object, Qualify(schemaName, name), owner)
        };
    }

    /// <summary>
    /// 一個掛在父物件底下的東西（條件約束、觸發程序），指不到時一路退回父物件。
    /// </summary>
    /// <param name="parent">父物件與子物件的型別代碼，走 <c>SqlMetadataCatalog.GetParentAsync</c>。</param>
    /// <param name="childName">子物件自己的名稱。</param>
    /// <remarks>
    /// 四種條件約束分屬三種節點：<c>CHECK</c> 在「條件約束」底下，主索引鍵與唯一鍵在
    /// 「索引鍵」底下而且是<b>索引</b>，外來鍵自成一種。<c>DEFAULT</c> 最特別——它掛在
    /// <b>資料行</b>底下（<c>Column/Default</c>），所以位址要多一段，而那個資料行名稱
    /// 只有目錄答得出來。
    ///
    /// <c>DEFAULT</c> 那一段<b>帶名稱鍵</b>（<c>Default[@Name='…']</c>），儘管一個資料行只有
    /// 一個。位址是列舉器給的，而 <c>DefaultConstrain.xml</c> 接的是 <c>inc_constraint.xml</c>
    /// → <c>inc_urn.xml</c>，那一份對所有具名物件一律組成 <c>父位址/型別[@Name='…']</c>。
    /// 照「單一子物件不帶鍵」的直覺寫成 <c>…/Default</c> 的症狀很安靜：候選第一個永遠對不上，
    /// 於是每一次都退到第二個，畫面上選到的是那個<b>資料行</b>，而它看起來就像功能只做到一半。
    ///
    /// <c>DEFAULT</c> 的候選仍然有三層：節點本身、它掛的資料行、父物件。
    /// </remarks>
    public static IReadOnlyList<SqlExplorerNode> ForChild(
        string rootUrn, SqlObjectParent parent, string childName)
    {
        RequireRoot(rootUrn);
        if (parent is null) throw new ArgumentNullException(nameof(parent));

        if (string.IsNullOrEmpty(childName)) return None;

        var owner = parent.Object;

        if (BuildObject(rootUrn, owner.DatabaseName ?? "", owner.SchemaName, owner.Name, owner.Kind) is not { } ownerUrn)
        {
            return None;
        }

        var fallback = new SqlExplorerNode(
            SqlExplorerNodeKind.Object, Qualify(owner.SchemaName, owner.Name), ownerUrn);

        // DEFAULT 掛在資料行底下，所以它是唯一要先接一段資料行的；資料行問不到時
        // 只剩父物件，而不是拿別的節點頂替。
        if (IsDefaultConstraint(parent.ChildType))
        {
            if (parent.ColumnName is not { Length: > 0 } defaultColumn) return new[] { fallback };

            var column = Child(ownerUrn, "Column", defaultColumn);

            // 兩個都掛在父物件底下，不是掛在資料行底下：DEFAULT 的位址多一段資料行，
            // 但畫它的是資料表的「條件約束」資料夾。寫成 column 的話，往下找的那一步會去
            // 一個沒有子節點的資料行底下翻。
            return new[]
            {
                new SqlExplorerNode(
                    SqlExplorerNodeKind.Constraint, childName, Child(column, "Default", childName), ownerUrn),
                new SqlExplorerNode(SqlExplorerNodeKind.Column, defaultColumn, column, ownerUrn),
                fallback
            };
        }

        var (node, kind) = ChildNode(parent.ChildType);

        if (node is null) return new[] { fallback };

        return new[]
        {
            new SqlExplorerNode(kind, childName, Child(ownerUrn, node, childName), ownerUrn),
            fallback
        };
    }

    /// <summary>一個 SQL Server Agent 作業的節點。</summary>
    /// <remarks>
    /// 作業掛在 <c>JobServer</c> 底下而不是某個資料庫底下，所以不共用上面那幾支。
    /// 步驟沒有自己的節點，呼叫端要先降到它所屬的作業。
    /// </remarks>
    public static IReadOnlyList<SqlExplorerNode> ForJob(string rootUrn, string jobName)
    {
        RequireRoot(rootUrn);

        if (string.IsNullOrEmpty(jobName)) return None;

        return new[]
        {
            new SqlExplorerNode(
                SqlExplorerNodeKind.Job, jobName, rootUrn + "/JobServer/Job[@Name='" + Escape(jobName) + "']")
        };
    }

    /// <remarks>
    /// 三種函式共用 <c>UserDefinedFunction</c>：物件總管沒有把純量與資料表值函式分成兩種
    /// 節點，分開寫的那一版對內嵌資料表值函式組出一個不存在的型別，導航安靜地回 false。
    /// </remarks>
    private static string? NodeType(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Table => "Table",
        SqlObjectKind.View => "View",
        SqlObjectKind.Procedure => "StoredProcedure",
        SqlObjectKind.ScalarFunction or
        SqlObjectKind.InlineTableFunction or
        SqlObjectKind.TableValuedFunction => "UserDefinedFunction",
        SqlObjectKind.Synonym => "Synonym",
        SqlObjectKind.Sequence => "Sequence",
        SqlObjectKind.TableType => "UserDefinedTableType",
        _ => null
    };

    /// <remarks>
    /// 代碼照 <c>sys.objects.type</c>。主索引鍵與唯一鍵在樹上是<b>索引</b>而不是條件約束，
    /// 寫成別的名字的話，樹上一個節點都對不上。
    /// </remarks>
    private static (string? Node, SqlExplorerNodeKind Kind) ChildNode(string childType) =>
        (childType ?? "").Trim().ToUpperInvariant() switch
        {
            "TR" or "TA" => ("Trigger", SqlExplorerNodeKind.Trigger),
            "C" => ("Check", SqlExplorerNodeKind.Constraint),
            "F" => ("ForeignKey", SqlExplorerNodeKind.Constraint),
            "PK" or "UQ" => ("Index", SqlExplorerNodeKind.Constraint),
            _ => (null, SqlExplorerNodeKind.Object)
        };

    private static bool IsDefaultConstraint(string childType) =>
        string.Equals((childType ?? "").Trim(), "D", StringComparison.OrdinalIgnoreCase);

    private static string? BuildObject(
        string rootUrn, string databaseName, string schemaName, string name, SqlObjectKind kind)
    {
        if (NodeType(kind) is not { } node) return null;
        if (string.IsNullOrEmpty(databaseName) || string.IsNullOrEmpty(schemaName) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return rootUrn +
            "/Database[@Name='" + Escape(databaseName) + "']" +
            "/" + node + "[@Name='" + Escape(name) + "' and @Schema='" + Escape(schemaName) + "']";
    }

    private static string Child(string ownerUrn, string node, string name) =>
        ownerUrn + "/" + node + "[@Name='" + Escape(name) + "']";

    private static string Qualify(string schemaName, string name) => schemaName + "." + name;

    private static void RequireRoot(string rootUrn)
    {
        if (string.IsNullOrEmpty(rootUrn)) throw new ArgumentException("根 URN 不可為空。", nameof(rootUrn));
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
