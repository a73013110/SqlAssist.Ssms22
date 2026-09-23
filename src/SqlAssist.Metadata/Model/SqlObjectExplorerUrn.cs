using System;
using System.Collections.Generic;
using System.Linq;

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

/// <summary>物件總管上的一個節點：指得到它的 URN，加上它是什麼、從哪裡走得到。</summary>
/// <remarks>
/// 帶著 <see cref="Kind"/> 與 <see cref="Name"/> 而不是只交出字串，是為了讓降級說得出話：
/// 指不到條件約束而停在資料行上時，畫面上要寫得出「已改為選取資料行 DueDate」，
/// 而那句話的兩個零件都在這裡。
/// </remarks>
public readonly struct SqlExplorerNode
{
    private static readonly string[] NoFolders = Array.Empty<string>();

    /// <param name="anchorUrn">
    /// 導覽服務指得到、而且這個節點在它底下的最近一個節點；這個節點自己就指得到時留空。
    /// </param>
    /// <param name="folders">從錨點往下沿路資料夾的不變名稱；不知道時留空。</param>
    /// <param name="depth">從錨點往下要翻幾層才到這個節點；沒有錨點時為 0。</param>
    public SqlExplorerNode(
        SqlExplorerNodeKind kind, string name, string urn,
        string anchorUrn = "", IReadOnlyList<string>? folders = null, int depth = 0)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("節點名稱不可為空。", nameof(name));
        if (string.IsNullOrEmpty(urn)) throw new ArgumentException("節點 URN 不可為空。", nameof(urn));

        anchorUrn ??= "";

        // 有錨點卻不往下翻就永遠找不到，沒有錨點卻要翻則不知道從哪裡翻；兩種都是組錯了。
        if ((anchorUrn.Length > 0) != (depth > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "有錨點時層數必須大於 0，沒有時必須是 0。");
        }

        Kind = kind;
        Name = name;
        Urn = urn;
        AnchorUrn = anchorUrn;
        Folders = folders ?? NoFolders;
        Depth = depth;
    }

    public SqlExplorerNodeKind Kind { get; }

    /// <summary>給人看的名稱；物件是限定名稱，其餘是節點自己的名字。</summary>
    public string Name { get; }

    public string Urn { get; }

    /// <summary>導覽服務指得到、而且這個節點在它底下的最近一個節點；自己就指得到時為空字串。</summary>
    /// <remarks>
    /// SSMS 22 的導覽服務只認得資料表、檢視與預存程序三種物件的資料夾路徑
    /// （<c>SchemaObjectFolderPaths</c> 寫死 <c>UserTables</c>、<c>Views</c>、
    /// <c>UserProgrammability/StoredProcedures</c>），再往下一段只比對直接子節點。
    /// 函式、同義字、序列與資料表型別因此從樹根<b>一個都指不到</b>——它們在
    /// 「程式設計」底下隔著兩三層資料夾，而資料夾在樹上<b>沒有自己的位址</b>
    /// （URN 就是父節點的）。畫在資料表底下的資料行、條件約束與觸發程序是同一件事的
    /// 另一個樣子。
    ///
    /// 所以位址之外還要一個「從哪裡開始自己走」：錨點是資料表，或者是資料庫。
    /// 由這一份決定而不是讓接線層自己試，是因為「哪一種畫在哪一格」的對應表只有一份，
    /// 抄第二份的症狀是加一種節點時只改了一邊。
    /// </remarks>
    public string AnchorUrn { get; }

    /// <summary>
    /// 從錨點往下沿路資料夾在樹上的不變名稱（<c>UserProgrammability</c>、<c>Columns</c>…），依序。
    /// </summary>
    /// <remarks>
    /// 取自 <c>sqlexplorerhier.xml</c> 資料夾的 <c>UniqueName</c>（沒有時是物件名稱），樹上以
    /// <c>INodeInformation.InvariantName</c> 交出來，SSMS 自己的導覽服務也拿它比對
    /// <c>UserTables</c> 那幾層。顯示文字是在地化的，<b>禁止</b>拿來比。
    ///
    /// <b>只決定先翻哪一個資料夾，不決定翻不翻。</b>每翻開一個沒建過的資料夾就是向伺服器查一次，
    /// 照樹的順序找函式要先付資料表、檢視與外部資源好幾趟。名稱對不上（新版本改了、
    /// 別種節點的資料夾）時只是退回樹的順序，拿它過濾的話就變成找不到。
    /// </remarks>
    public IReadOnlyList<string> Folders { get; }

    /// <summary>從錨點往下要翻幾層才到這個節點（資料夾與中間的物件都算一層）。</summary>
    /// <remarks>
    /// 往下找是一段會向伺服器查詢的遞迴，深度一定要有上限；而上限只有知道樹長什麼樣子的
    /// 這一份給得出來。寫死一個數字的話，資料表底下的兩層不夠資料表型別的六層，
    /// 反過來又讓資料表底下多翻一大片。
    /// </remarks>
    public int Depth { get; }

    public override string ToString() => Kind + ":" + Name;
}

/// <summary>
/// 物件總管節點的 URN；「在物件總管中選取」唯一組位址的地方。
/// </summary>
/// <remarks>
/// 純字串組裝，所以放在 Metadata 而不是接線層：節點路徑的對應表與跳脫規則只有一份，
/// 之後別的路徑要跳到樹上時接的是同一支。路徑照 SSMS 自己的
/// <c>sqlexplorerhier.xml</c>（節點的 <c>Xpath</c> 與 <c>UrnShell</c>、資料夾的 <c>UniqueName</c>）。
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

    // 資料夾的 UniqueName：資料表、檢視、資料表型別與各版本的變體共用這幾個，
    // 見 SqlExplorerNode.Folders。
    private const string ColumnsFolder = "Columns";
    private const string KeysFolder = "Keys";
    private const string ConstraintsFolder = "Constraints";
    private const string TriggersFolder = "Triggers";

    /// <summary>從伺服器根到資料庫沿路可能經過的資料夾。</summary>
    /// <remarks>
    /// 使用者資料庫直接在「資料庫」底下；系統資料庫（master、msdb…，連同散發資料庫）在
    /// 「系統資料庫」、快照集在「資料庫快照集」這兩個子資料夾裡。導覽服務只比對「資料庫」的
    /// 直接子節點，所以後兩種從樹根一個都指不到，連帶資料庫底下的每一種物件都指不到。
    /// </remarks>
    private static readonly string[] DatabaseFolders = { "Databases", "SystemDatabases", "DatabaseSnapshots" };

    /// <summary>從伺服器根往下到資料庫節點最多幾層：「資料庫」、子資料夾、資料庫本身。</summary>
    private const int DatabaseDepth = 3;

    /// <remarks>
    /// 「程式設計」與「函式」兩個資料夾沒有 <c>UniqueName</c>，不變名稱退回 XML 上的物件名稱；
    /// 導覽服務自己比對 <c>UserProgrammability</c> 靠的就是這一條。
    /// </remarks>
    private const string ProgrammabilityFolder = "UserProgrammability";
    private const string FunctionsFolder = "UsrDbFunctions";

    /// <summary>資料庫底下每一種物件的節點型別與它在樹上的位置。</summary>
    /// <remarks>
    /// <c>Navigable</c> 只有導覽服務寫死的那三種為 true；每一種都另有從資料庫、從伺服器根
    /// 往下自己走的路（<see cref="Walks"/>）。
    ///
    /// 三種函式共用 <c>UserDefinedFunction</c>：物件總管沒有把純量與資料表值函式分成兩種
    /// 節點，分開寫的那一版對內嵌資料表值函式組出一個不存在的型別，導航安靜地回 false。
    /// 內嵌與多陳述式都畫在「資料表值函式」底下（<c>FunctionType</c> 3 與 2）。
    ///
    /// 資料表的層數算到子資料夾那一層：FileTable 與圖形資料表畫在「資料表」底下的
    /// <b>子資料夾</b>裡，導覽服務只比對「資料表」的直接子節點，所以它們與函式同一個症狀。
    /// </remarks>
    private static Placement? PlacementOf(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Table => new Placement("Table", true, 3, "UserTables", "GraphTables", "FileTables"),
        SqlObjectKind.View => new Placement("View", true, 2, "Views"),
        SqlObjectKind.Procedure => new Placement("StoredProcedure", true, 3, ProgrammabilityFolder, "StoredProcedures"),
        SqlObjectKind.ScalarFunction => new Placement(
            "UserDefinedFunction", false, 4, ProgrammabilityFolder, FunctionsFolder, "Scalar-valuedFunctions"),
        SqlObjectKind.InlineTableFunction or
        SqlObjectKind.TableValuedFunction => new Placement(
            "UserDefinedFunction", false, 4, ProgrammabilityFolder, FunctionsFolder, "Table-valuedFunctions"),
        SqlObjectKind.Synonym => new Placement("Synonym", false, 2, "Synonyms"),
        SqlObjectKind.Sequence => new Placement("Sequence", false, 3, ProgrammabilityFolder, "Sequences"),
        SqlObjectKind.TableType => new Placement(
            "UserDefinedTableType", false, 4, ProgrammabilityFolder, "Types", "UserDefinedTableTypes"),
        _ => null
    };

    /// <summary>這一種物件在資料庫底下有沒有自己的節點。</summary>
    /// <remarks>
    /// 觸發程序與條件約束回 false：它們畫在父物件底下，位址要先問出父物件是誰
    /// （<see cref="RequiresParent"/>），組法在 <see cref="ForChild"/>。
    /// </remarks>
    public static bool Supports(SqlObjectKind kind) => PlacementOf(kind) is not null;

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
    ///
    /// 同一個位址會有<b>好幾條路</b>，由近到遠：導覽服務直接指、從資料庫往下走、從伺服器根
    /// 往下走。呼叫端比的是位址，所以後面幾條成功時不算降級。
    /// </remarks>
    public static IReadOnlyList<SqlExplorerNode> ForObject(
        string rootUrn, string databaseName, string schemaName, string name, SqlObjectKind kind)
    {
        RequireRoot(rootUrn);

        return Locate(rootUrn, databaseName, schemaName, name, kind) is { } target
            ? ObjectCandidates(target)
            : None;
    }

    /// <summary>
    /// 一個物件底下的資料行，指不到時退回那個物件。
    /// </summary>
    /// <remarks>
    /// 資料行命中的導航目標是<b>那一行</b>，不是整個物件：使用者搜的是 <c>CopyNo</c>，
    /// 而樹上就有那一行。退回物件是因為資料行資料夾在某些種類上不一定畫得出來，
    /// 而停在那張表上仍然是他要去的地方。
    ///
    /// 函式在樹上<b>沒有</b>資料行資料夾（資料表值函式底下只有「參數」），所以它的資料行命中
    /// 直接交出函式本身：多翻一個參數資料夾換來的只是一句永遠成立的「找不到」。
    /// </remarks>
    public static IReadOnlyList<SqlExplorerNode> ForColumn(
        string rootUrn, string databaseName, string schemaName, string name, SqlObjectKind kind, string columnName)
    {
        RequireRoot(rootUrn);

        if (Locate(rootUrn, databaseName, schemaName, name, kind) is not { } owner) return None;
        if (string.IsNullOrEmpty(columnName) || !HasColumnFolder(kind)) return ObjectCandidates(owner);

        return Prepend(
            ChildOf(owner, SqlExplorerNodeKind.Column, columnName, Child(owner.Urn, "Column", columnName), ColumnsFolder),
            ObjectCandidates(owner));
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

        var ownerInfo = parent.Object;

        if (Locate(rootUrn, ownerInfo.DatabaseName ?? "", ownerInfo.SchemaName, ownerInfo.Name, ownerInfo.Kind)
            is not { } owner)
        {
            return None;
        }

        var fallback = ObjectCandidates(owner);

        // DEFAULT 掛在資料行底下，所以它是唯一要先接一段資料行的；資料行問不到時
        // 只剩父物件，而不是拿別的節點頂替。
        if (IsDefaultConstraint(parent.ChildType))
        {
            if (parent.ColumnName is not { Length: > 0 } defaultColumn) return fallback;

            var column = Child(owner.Urn, "Column", defaultColumn);

            // 兩個都畫在父物件底下，不是畫在資料行底下：DEFAULT 的位址多一段資料行，
            // 但畫它的是父物件的「條件約束」資料夾。當成畫在資料行底下的話，往下找的那一步
            // 會去一個沒有子節點的資料行底下翻。
            return Prepend(
                Prepend(
                    ChildOf(owner, SqlExplorerNodeKind.Constraint, childName,
                        Child(column, "Default", childName), ConstraintsFolder),
                    ChildOf(owner, SqlExplorerNodeKind.Column, defaultColumn, column, ColumnsFolder)),
                fallback);
        }

        var (node, kind, folder) = ChildNode(parent.ChildType);

        if (node is null) return fallback;

        return Prepend(ChildOf(owner, kind, childName, Child(owner.Urn, node, childName), folder), fallback);
    }

    /// <summary>一個 SQL Server Agent 作業的節點。</summary>
    /// <remarks>
    /// 作業掛在 <c>JobServer</c> 底下而不是某個資料庫底下，所以不共用上面那幾支；
    /// 導覽服務認得 <c>JobServer</c>，不必自己走。步驟沒有自己的節點，呼叫端要先降到它所屬的作業。
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

    /// <summary>樹上有「資料行」資料夾的種類。</summary>
    private static bool HasColumnFolder(SqlObjectKind kind) =>
        kind is SqlObjectKind.Table or SqlObjectKind.View or SqlObjectKind.TableType;

    /// <remarks>
    /// 代碼照 <c>sys.objects.type</c>。主索引鍵與唯一鍵在樹上是<b>索引</b>而不是條件約束，
    /// 寫成別的名字的話，樹上一個節點都對不上。它們在「索引鍵」與「索引」兩個資料夾裡
    /// 各畫一次，同一個位址；前者在樹上排得比較前面，也是使用者認它的地方。
    /// </remarks>
    private static (string? Node, SqlExplorerNodeKind Kind, string Folder) ChildNode(string childType) =>
        (childType ?? "").Trim().ToUpperInvariant() switch
        {
            "TR" or "TA" => ("Trigger", SqlExplorerNodeKind.Trigger, TriggersFolder),
            "C" => ("Check", SqlExplorerNodeKind.Constraint, ConstraintsFolder),
            "F" => ("ForeignKey", SqlExplorerNodeKind.Constraint, KeysFolder),
            "PK" or "UQ" => ("Index", SqlExplorerNodeKind.Constraint, KeysFolder),
            _ => (null, SqlExplorerNodeKind.Object, "")
        };

    private static bool IsDefaultConstraint(string childType) =>
        string.Equals((childType ?? "").Trim(), "D", StringComparison.OrdinalIgnoreCase);

    /// <summary>一個物件自己的節點，由近到遠：導覽服務直接指、從資料庫走、從伺服器根走。</summary>
    private static IReadOnlyList<SqlExplorerNode> ObjectCandidates(Located target)
    {
        var placement = target.Placement;
        var walks = Walks(
            target, SqlExplorerNodeKind.Object, target.Name, target.Urn, placement.Folders, placement.Depth);

        return placement.Navigable
            ? Prepend(new SqlExplorerNode(SqlExplorerNodeKind.Object, target.Name, target.Urn), walks)
            : walks;
    }

    /// <summary>畫在 <paramref name="owner"/> 底下某個資料夾裡的節點，由近到遠。</summary>
    /// <remarks>
    /// 父物件是導覽服務指得到的那三種時，最近的錨點就是它，往下兩層（資料夾、節點）。
    /// 其餘兩條從資料庫或伺服器根走：沿路是父物件自己的那一串資料夾、父物件、資料夾、節點。
    /// </remarks>
    private static IReadOnlyList<SqlExplorerNode> ChildOf(
        Located owner, SqlExplorerNodeKind kind, string name, string urn, string folder)
    {
        var folders = new List<string>(owner.Placement.Folders) { folder };
        var walks = Walks(owner, kind, name, urn, folders, owner.Placement.Depth + 2);

        return owner.Placement.Navigable
            ? Prepend(new SqlExplorerNode(kind, name, urn, owner.Urn, new[] { folder }, 2), walks)
            : walks;
    }

    /// <summary>從資料庫、從伺服器根各走一次到 <paramref name="urn"/> 的兩個候選。</summary>
    /// <param name="depth">從資料庫往下的層數。</param>
    /// <remarks>
    /// 兩條都要：導覽服務指得到使用者資料庫，從那裡走最近；系統資料庫與快照集它指不到，
    /// 只能從伺服器根經過「資料庫」與它的子資料夾下去。<b>不</b>先判斷是不是系統資料庫：
    /// 樹上那一格看的是 SMO 的 <c>IsSystemObject</c>，散發資料庫也算，名字看不出來。
    /// 呼叫端從近的錨點到得了之後就不會再從遠的找一次，所以使用者資料庫上多一條不多花錢。
    /// </remarks>
    private static IReadOnlyList<SqlExplorerNode> Walks(
        Located database, SqlExplorerNodeKind kind, string name, string urn,
        IReadOnlyList<string> folders, int depth) =>
        new[]
        {
            new SqlExplorerNode(kind, name, urn, database.DatabaseUrn, folders, depth),
            new SqlExplorerNode(
                kind, name, urn, database.RootUrn, DatabaseFolders.Concat(folders).ToArray(), DatabaseDepth + depth)
        };

    private static IReadOnlyList<SqlExplorerNode> Prepend(
        SqlExplorerNode first, IReadOnlyList<SqlExplorerNode> rest) =>
        Prepend(new[] { first }, rest);

    private static IReadOnlyList<SqlExplorerNode> Prepend(
        IReadOnlyList<SqlExplorerNode> first, IReadOnlyList<SqlExplorerNode> rest) =>
        first.Concat(rest).ToArray();

    private static Located? Locate(
        string rootUrn, string databaseName, string schemaName, string name, SqlObjectKind kind)
    {
        if (PlacementOf(kind) is not { } placement) return null;
        if (string.IsNullOrEmpty(databaseName) || string.IsNullOrEmpty(schemaName) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var databaseUrn = rootUrn + "/Database[@Name='" + Escape(databaseName) + "']";
        var urn = databaseUrn + "/" + placement.Node + "[@Name='" + Escape(name) + "' and @Schema='" +
            Escape(schemaName) + "']";

        return new Located(Qualify(schemaName, name), urn, rootUrn, databaseUrn, placement);
    }

    private static string Child(string ownerUrn, string node, string name) =>
        ownerUrn + "/" + node + "[@Name='" + Escape(name) + "']";

    private static string Qualify(string schemaName, string name) => schemaName + "." + name;

    private static void RequireRoot(string rootUrn)
    {
        if (string.IsNullOrEmpty(rootUrn)) throw new ArgumentException("根 URN 不可為空。", nameof(rootUrn));
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private sealed class Placement
    {
        /// <param name="navigable">導覽服務從樹根指不指得到。</param>
        /// <param name="depth">從資料庫往下翻幾層到得了它，資料夾與它自己都算。</param>
        /// <param name="folders">沿路資料夾的不變名稱，只用來排先後。</param>
        public Placement(string node, bool navigable, int depth, params string[] folders)
        {
            Node = node;
            Navigable = navigable;
            Depth = depth;
            Folders = folders;
        }

        public string Node { get; }

        public bool Navigable { get; }

        public int Depth { get; }

        public IReadOnlyList<string> Folders { get; }
    }

    private sealed class Located
    {
        public Located(string name, string urn, string rootUrn, string databaseUrn, Placement placement)
        {
            Name = name;
            Urn = urn;
            RootUrn = rootUrn;
            DatabaseUrn = databaseUrn;
            Placement = placement;
        }

        public string Name { get; }

        public string Urn { get; }

        public string RootUrn { get; }

        public string DatabaseUrn { get; }

        public Placement Placement { get; }
    }
}
