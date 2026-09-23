using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 物件總管節點的位址：型別對應、從哪裡開始自己走、父物件底下那幾種，以及由精確到寬鬆的候選。
/// </summary>
/// <remarks>
/// 這一份錯的症狀在實機上全都一樣——導航回 false，畫面上只說「找不到」，
/// 而少掉的可能是結構描述、可能是節點型別、可能是錨點，也可能是 DEFAULT 那一段資料行。
/// </remarks>
public sealed class SqlObjectExplorerUrnTests
{
    private const string Root = "Server[@Name='LIBSQL01']";

    private const string Database = Root + "/Database[@Name='Lib']";

    private const string Table = Database + "/Table[@Name='Cat_BookCopy' and @Schema='dbo']";

    private const string TableType = Database + "/UserDefinedTableType[@Name='Lib_TagList' and @Schema='dbo']";

    private static readonly string[] DatabaseFolders = { "Databases", "SystemDatabases", "DatabaseSnapshots" };

    private static readonly string[] TableFolders = { "UserTables", "GraphTables", "FileTables" };

    private static readonly string[] TableTypeFolders = { "UserProgrammability", "Types", "UserDefinedTableTypes" };

    private static SqlObjectParent Parent(string childType, string? columnName = null) =>
        new(new SqlObjectInfo(1, "dbo", "Cat_BookCopy", SqlObjectKind.Table, "Lib"), childType, columnName);

    private static SqlObjectParent TableTypeParent(string childType, string? columnName = null) =>
        new(new SqlObjectInfo(7, "dbo", "Lib_TagList", SqlObjectKind.TableType, "Lib"), childType, columnName);

    /// <summary>
    /// 從資料庫走的那一條與從伺服器根走的那一條：後者多「資料庫」與它的兩個子資料夾、多三層。
    /// </summary>
    private static void AssertWalks(
        IReadOnlyList<SqlExplorerNode> nodes, int at, string urn, IEnumerable<string> folders, int depth)
    {
        Assert.Equal(urn, nodes[at].Urn);
        Assert.Equal(Database, nodes[at].AnchorUrn);
        Assert.Equal(folders, nodes[at].Folders);
        Assert.Equal(depth, nodes[at].Depth);

        Assert.Equal(urn, nodes[at + 1].Urn);
        Assert.Equal(Root, nodes[at + 1].AnchorUrn);
        Assert.Equal(DatabaseFolders.Concat(folders), nodes[at + 1].Folders);
        Assert.Equal(depth + 3, nodes[at + 1].Depth);
    }

    /// <remarks>
    /// 同一個位址三條路，由近到遠：導覽服務直接指；從資料庫往下走（FileTable、圖形資料表
    /// 在子資料夾裡）；從伺服器根往下走（系統資料庫與快照集在「資料庫」的子資料夾裡）。
    /// </remarks>
    [Fact]
    public void 資料表由近到遠三條路()
    {
        var nodes = SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Cat_BookCopy", SqlObjectKind.Table);

        Assert.Equal(3, nodes.Count);
        Assert.Equal(Table, nodes[0].Urn);
        Assert.Equal("", nodes[0].AnchorUrn);
        Assert.Equal(0, nodes[0].Depth);
        AssertWalks(nodes, 1, Table, TableFolders, 3);
    }

    /// <remarks>
    /// 導覽服務認得檢視與預存程序，但它到不了系統資料庫；後面兩條就是給 master、msdb 用的。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.View, "View", 2, new[] { "Views" })]
    [InlineData(SqlObjectKind.Procedure, "StoredProcedure", 3, new[] { "UserProgrammability", "StoredProcedures" })]
    public void 檢視與預存程序也能從資料庫與伺服器根走(
        SqlObjectKind kind, string node, int depth, string[] folders)
    {
        var nodes = SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Copy", kind);
        var urn = Database + "/" + node + "[@Name='Copy' and @Schema='dbo']";

        Assert.Equal(3, nodes.Count);
        Assert.Equal(urn, nodes[0].Urn);
        Assert.Equal("", nodes[0].AnchorUrn);
        AssertWalks(nodes, 1, urn, folders, depth);
    }

    /// <remarks>
    /// 物件總管沒有把純量與資料表值函式分成兩種節點，但畫在不同的資料夾裡；
    /// 內嵌與多陳述式都在「資料表值函式」底下。導覽服務不認得函式，沒有直接指的那一條。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.ScalarFunction, "Scalar-valuedFunctions")]
    [InlineData(SqlObjectKind.InlineTableFunction, "Table-valuedFunctions")]
    [InlineData(SqlObjectKind.TableValuedFunction, "Table-valuedFunctions")]
    public void 函式從資料庫往下走四層(SqlObjectKind kind, string folder)
    {
        var nodes = SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Loan", kind);

        Assert.Equal(2, nodes.Count);
        AssertWalks(
            nodes, 0, Database + "/UserDefinedFunction[@Name='Loan' and @Schema='dbo']",
            new[] { "UserProgrammability", "UsrDbFunctions", folder }, 4);
    }

    /// <remarks>「其他」那一桶的三種：導覽服務一個都不認得。</remarks>
    [Theory]
    [InlineData(SqlObjectKind.Synonym, "Synonym", 2, new[] { "Synonyms" })]
    [InlineData(SqlObjectKind.Sequence, "Sequence", 3, new[] { "UserProgrammability", "Sequences" })]
    [InlineData(
        SqlObjectKind.TableType, "UserDefinedTableType", 4,
        new[] { "UserProgrammability", "Types", "UserDefinedTableTypes" })]
    public void 其他種類從資料庫往下走(SqlObjectKind kind, string node, int depth, string[] folders)
    {
        var nodes = SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Copy", kind);

        Assert.Equal(2, nodes.Count);
        AssertWalks(nodes, 0, Database + "/" + node + "[@Name='Copy' and @Schema='dbo']", folders, depth);
    }

    /// <remarks>
    /// msdb 在「系統資料庫」底下，導覽服務指不到它。位址本身與使用者資料庫同一個樣子，
    /// 從伺服器根走的那一條才是到得了的那一條。
    /// </remarks>
    [Fact]
    public void 系統資料庫的物件從伺服器根走得到()
    {
        var nodes = SqlObjectExplorerUrn.ForObject(Root, "msdb", "dbo", "Lib_TagList", SqlObjectKind.TableType);
        var viaRoot = nodes.Single(node => node.AnchorUrn == Root);

        Assert.Equal(
            Root + "/Database[@Name='msdb']/UserDefinedTableType[@Name='Lib_TagList' and @Schema='dbo']",
            viaRoot.Urn);
        Assert.Equal(new[] { "Databases", "SystemDatabases" }, viaRoot.Folders.Take(2));
    }

    /// <remarks>少了結構描述會停在第一個同名的節點上，而使用者看不出跳錯了。</remarks>
    [Fact]
    public void 缺結構描述就不組位址()
    {
        Assert.Empty(SqlObjectExplorerUrn.ForObject(Root, "Lib", "", "Loan", SqlObjectKind.Table));
    }

    /// <remarks>與 SMO 的 <c>Urn.EscapeString</c> 同一條規則；錨點的資料庫那一段也要跳脫。</remarks>
    [Fact]
    public void 名稱裡的單引號寫成兩個()
    {
        var nodes = SqlObjectExplorerUrn.ForObject(Root, "Lib's", "dbo", "Lib_Rea'der", SqlObjectKind.Table);

        Assert.Equal(
            Root + "/Database[@Name='Lib''s']/Table[@Name='Lib_Rea''der' and @Schema='dbo']",
            nodes[0].Urn);
        Assert.Equal(Root + "/Database[@Name='Lib''s']", nodes[1].AnchorUrn);
    }

    /// <remarks>資料行命中要停在那一行上；指不到才退回那張表。每個目標都有由近到遠三條路。</remarks>
    [Fact]
    public void 資料行先指那一行再退回物件()
    {
        var nodes = SqlObjectExplorerUrn.ForColumn(
            Root, "Lib", "dbo", "Cat_BookCopy", SqlObjectKind.Table, "CopyNo");
        var column = Table + "/Column[@Name='CopyNo']";

        Assert.Equal(new[] { column, column, column, Table, Table, Table }, nodes.Select(node => node.Urn));
        Assert.Equal(
            new[] { SqlExplorerNodeKind.Column, SqlExplorerNodeKind.Object },
            nodes.Select(node => node.Kind).Distinct());
        Assert.Equal(Table, nodes[0].AnchorUrn);
        Assert.Equal(new[] { "Columns" }, nodes[0].Folders);
        Assert.Equal(2, nodes[0].Depth);
        AssertWalks(nodes, 1, column, TableFolders.Append("Columns"), 5);
        Assert.Equal("", nodes[3].AnchorUrn);
    }

    /// <remarks>
    /// 資料表型別不是導覽服務指得到的那幾種，所以它的資料行沒有錨在型別上的那一條，
    /// 沿路是型別自己那一串、型別本身、「資料行」資料夾、那一行，一共六層。
    /// </remarks>
    [Fact]
    public void 資料表型別的資料行從資料庫往下走()
    {
        var nodes = SqlObjectExplorerUrn.ForColumn(
            Root, "Lib", "dbo", "Lib_TagList", SqlObjectKind.TableType, "Tag");

        Assert.Equal(4, nodes.Count);
        AssertWalks(nodes, 0, TableType + "/Column[@Name='Tag']", TableTypeFolders.Append("Columns"), 6);
        AssertWalks(nodes, 2, TableType, TableTypeFolders, 4);
    }

    /// <remarks>函式在樹上只有「參數」資料夾，沒有資料行；資料行命中直接停在函式上。</remarks>
    [Theory]
    [InlineData(SqlObjectKind.InlineTableFunction)]
    [InlineData(SqlObjectKind.TableValuedFunction)]
    public void 函式的資料行命中停在函式上(SqlObjectKind kind)
    {
        var nodes = SqlObjectExplorerUrn.ForColumn(Root, "Lib", "dbo", "Loan", kind, "CopyNo");

        Assert.All(nodes, node =>
        {
            Assert.Equal(Database + "/UserDefinedFunction[@Name='Loan' and @Schema='dbo']", node.Urn);
            Assert.Equal(SqlExplorerNodeKind.Object, node.Kind);
        });
    }

    /// <remarks>
    /// 資料表型別上的主索引鍵畫在型別節點底下，而型別本身導覽服務指不到，所以不錨在型別上：
    /// 錨在型別上的話，第一步導航就失敗，連退回型別都退不到。父物件名稱必須是型別名，
    /// 不是 <c>sys.objects</c> 上那個 <c>TT_…</c> 內部名稱（那一段由目錄查詢負責）。
    /// </remarks>
    [Fact]
    public void 資料表型別上的條件約束從資料庫往下走()
    {
        var nodes = SqlObjectExplorerUrn.ForChild(Root, TableTypeParent("PK"), "PK_Lib_TagList");

        Assert.Equal(4, nodes.Count);
        AssertWalks(nodes, 0, TableType + "/Index[@Name='PK_Lib_TagList']", TableTypeFolders.Append("Keys"), 6);
        AssertWalks(nodes, 2, TableType, TableTypeFolders, 4);
    }

    /// <remarks>DEFAULT 的位址多一段資料行，畫它的仍然是型別的「條件約束」資料夾，層數與資料行相同。</remarks>
    [Fact]
    public void 資料表型別上的預設值約束與資料行同一層()
    {
        var nodes = SqlObjectExplorerUrn.ForChild(Root, TableTypeParent("D", "Tag"), "DF_Lib_TagList_Tag");
        var column = TableType + "/Column[@Name='Tag']";

        Assert.Equal(6, nodes.Count);
        AssertWalks(
            nodes, 0, column + "/Default[@Name='DF_Lib_TagList_Tag']", TableTypeFolders.Append("Constraints"), 6);
        AssertWalks(nodes, 2, column, TableTypeFolders.Append("Columns"), 6);
        AssertWalks(nodes, 4, TableType, TableTypeFolders, 4);
    }

    /// <summary>
    /// 畫在資料表底下的節點最近的錨點是那張表；退回的三條路與物件本身相同。
    /// </summary>
    /// <remarks>
    /// 接線層靠這個欄位分兩條路：有值的先到錨點再往下找，空的直接指。
    /// 漏掉一種的症狀是那一種在物件總管上永遠停在資料表，而它的兄弟節點好好的。
    /// </remarks>
    [Theory]
    [InlineData("C")]
    [InlineData("F")]
    [InlineData("PK")]
    [InlineData("UQ")]
    [InlineData("TR")]
    public void 掛在資料表底下的節點錨在資料表上(string childType)
    {
        var nodes = SqlObjectExplorerUrn.ForChild(Root, Parent(childType), "CK_Cat_BookCopy");

        Assert.Equal(
            new[] { Table, Database, Root, "", Database, Root },
            nodes.Select(node => node.AnchorUrn));
        Assert.Equal(2, nodes[0].Depth);
        Assert.Equal(5, nodes[1].Depth);
    }

    /// <remarks>
    /// DEFAULT 的位址多一段資料行，畫它的卻是資料表的「條件約束」資料夾；
    /// 錨點寫成那個資料行的話，往下找的那一步會去一個沒有子節點的資料行底下翻。
    /// </remarks>
    [Fact]
    public void 預設值約束的錨點是資料表不是資料行()
    {
        var nodes = SqlObjectExplorerUrn.ForChild(
            Root, Parent("D", "DueDate"), "DF_Cat_BookCopy_DueDate");

        Assert.Equal(
            new[] { Table, Database, Root, Table, Database, Root, "", Database, Root },
            nodes.Select(node => node.AnchorUrn));
    }

    /// <remarks>
    /// 資料夾名稱只決定先翻哪一個；寫錯的症狀不是找不到，而是每一次都照樹的順序
    /// 多翻幾個沒建過的資料夾，每一個都是一趟伺服器查詢。
    /// </remarks>
    [Theory]
    [InlineData("C", "Constraints")]
    [InlineData("F", "Keys")]
    [InlineData("PK", "Keys")]
    [InlineData("UQ", "Keys")]
    [InlineData("TR", "Triggers")]
    public void 掛在資料表底下的節點帶著畫它的資料夾(string childType, string folder)
    {
        var nodes = SqlObjectExplorerUrn.ForChild(Root, Parent(childType), "CK_Cat_BookCopy");

        Assert.Equal(new[] { folder }, nodes[0].Folders);
        Assert.Equal(TableFolders.Append(folder), nodes[1].Folders);
        Assert.Empty(nodes[3].Folders);
    }

    /// <remarks>DEFAULT 的位址掛在資料行底下，畫它的卻是「條件約束」資料夾。</remarks>
    [Fact]
    public void 預設值約束與資料行各自帶著自己的資料夾()
    {
        var nodes = SqlObjectExplorerUrn.ForChild(Root, Parent("D", "DueDate"), "DF_Cat_BookCopy_DueDate");

        Assert.Equal(new[] { "Constraints" }, nodes[0].Folders);
        Assert.Equal(new[] { "Columns" }, nodes[3].Folders);
    }

    /// <remarks>作業掛在導覽服務認得的 <c>JobServer</c> 底下，不必自己走。</remarks>
    [Fact]
    public void 作業沒有錨點()
    {
        var only = SqlObjectExplorerUrn.ForJob(Root, "Loan 夜間回收").Single();

        Assert.Equal("", only.AnchorUrn);
        Assert.Equal(0, only.Depth);
    }

    /// <remarks>
    /// 主索引鍵與唯一鍵在樹上是<b>索引</b>，不在「條件約束」資料夾裡；
    /// 觸發程序與外來鍵各自一種。
    /// </remarks>
    [Theory]
    [InlineData("C", "Check")]
    [InlineData("F", "ForeignKey")]
    [InlineData("PK", "Index")]
    [InlineData("UQ", "Index")]
    [InlineData("TR", "Trigger")]
    [InlineData("TA", "Trigger")]
    public void 父物件底下的節點照子物件自己的型別代碼(string childType, string node)
    {
        var nodes = SqlObjectExplorerUrn.ForChild(Root, Parent(childType), "DF_Cat_BookCopy_DueDate");
        var child = Table + "/" + node + "[@Name='DF_Cat_BookCopy_DueDate']";

        Assert.Equal(new[] { child, child, child, Table, Table, Table }, nodes.Select(item => item.Urn));
    }

    /// <remarks>
    /// DEFAULT 掛在資料行底下，而且<b>帶名稱鍵</b>——列舉器對所有具名物件一律組成
    /// <c>父位址/型別[@Name='…']</c>。少了那一段的症狀是每一次都退到資料行那個候選，
    /// 畫面上選到的是那個資料行。中間那一層是「指不到 Default 就停在那一行」。
    /// </remarks>
    [Fact]
    public void 預設值約束走資料行底下那一段()
    {
        var nodes = SqlObjectExplorerUrn.ForChild(
            Root, Parent("D", "DueDate"), "DF_Cat_BookCopy_DueDate");
        var column = Table + "/Column[@Name='DueDate']";
        var constraint = column + "/Default[@Name='DF_Cat_BookCopy_DueDate']";

        Assert.Equal(
            new[] { constraint, constraint, constraint, column, column, column, Table, Table, Table },
            nodes.Select(node => node.Urn));
        Assert.Equal(
            new[] { SqlExplorerNodeKind.Constraint, SqlExplorerNodeKind.Column, SqlExplorerNodeKind.Object },
            nodes.Select(node => node.Kind).Distinct());
    }

    /// <remarks>問不到資料行時只剩父物件，不拿別的節點頂替。</remarks>
    [Fact]
    public void 預設值約束缺資料行就只退回父物件()
    {
        Assert.All(SqlObjectExplorerUrn.ForChild(Root, Parent("D"), "DF_x"), node => Assert.Equal(Table, node.Urn));
    }

    /// <remarks>沒見過的型別代碼不硬湊一個節點出來，退回父物件仍然是對的地方。</remarks>
    [Fact]
    public void 認不得的子物件退回父物件()
    {
        Assert.All(
            SqlObjectExplorerUrn.ForChild(Root, Parent("ZZ"), "Whatever"), node => Assert.Equal(Table, node.Urn));
    }

    [Theory]
    [InlineData(SqlObjectKind.Trigger)]
    [InlineData(SqlObjectKind.Constraint)]
    public void 掛在父物件底下的種類要先問父物件(SqlObjectKind kind)
    {
        Assert.True(SqlObjectExplorerUrn.RequiresParent(kind));
        Assert.False(SqlObjectExplorerUrn.Supports(kind));
    }

    [Theory]
    [InlineData(SqlObjectKind.TemporaryTable)]
    [InlineData(SqlObjectKind.TableVariable)]
    [InlineData(SqlObjectKind.Unknown)]
    public void 指令碼自己宣告的東西不在樹上(SqlObjectKind kind)
    {
        Assert.False(SqlObjectExplorerUrn.Supports(kind));
        Assert.False(SqlObjectExplorerUrn.RequiresParent(kind));
        Assert.Empty(SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Loan", kind));
    }

    /// <remarks>作業掛在 JobServer 底下，不在任何一個資料庫裡。</remarks>
    [Fact]
    public void 作業接在JobServer底下()
    {
        Assert.Equal(
            Root + "/JobServer/Job[@Name='Loan 夜間回收']",
            SqlObjectExplorerUrn.ForJob(Root, "Loan 夜間回收").Single().Urn);
    }

    /// <remarks>根 URN 要由呼叫端從樹上取得；空的是程式錯誤，不是一種找不到。</remarks>
    [Fact]
    public void 沒有根URN就擲例外()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlObjectExplorerUrn.ForObject("", "Lib", "dbo", "Loan", SqlObjectKind.Table));
        Assert.Throws<ArgumentException>(() => SqlObjectExplorerUrn.ForJob("", "Loan"));
        Assert.Throws<ArgumentException>(() => SqlObjectExplorerUrn.ForChild("", Parent("C"), "CK_x"));
    }

    /// <remarks>有錨點卻不往下翻就永遠找不到，沒有錨點卻要翻則不知道從哪裡翻。</remarks>
    [Fact]
    public void 錨點與層數要一起給()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SqlExplorerNode(SqlExplorerNodeKind.Object, "dbo.Loan", Table, Database));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SqlExplorerNode(SqlExplorerNodeKind.Object, "dbo.Loan", Table, depth: 2));
    }
}
