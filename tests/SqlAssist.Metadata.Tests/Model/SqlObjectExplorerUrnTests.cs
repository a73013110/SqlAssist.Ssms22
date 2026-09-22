using System;
using System.Linq;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 物件總管節點的位址：型別對應、父物件底下那幾種，以及由精確到寬鬆的候選。
/// </summary>
/// <remarks>
/// 這一份錯的症狀在實機上全都一樣——導航回 false，畫面上只說「找不到」，
/// 而少掉的可能是結構描述、可能是節點型別，也可能是 DEFAULT 那一段資料行。
/// </remarks>
public sealed class SqlObjectExplorerUrnTests
{
    private const string Root = "Server[@Name='LIBSQL01']";

    private const string Table =
        Root + "/Database[@Name='Lib']/Table[@Name='Cat_BookCopy' and @Schema='dbo']";

    private static SqlObjectParent Parent(string childType, string? columnName = null) =>
        new(new SqlObjectInfo(1, "dbo", "Cat_BookCopy", SqlObjectKind.Table, "Lib"), childType, columnName);

    [Fact]
    public void 資料表接在資料庫底下並帶著結構描述()
    {
        Assert.Equal(
            new[] { Table },
            SqlObjectExplorerUrn
                .ForObject(Root, "Lib", "dbo", "Cat_BookCopy", SqlObjectKind.Table)
                .Select(node => node.Urn));
    }

    /// <remarks>物件總管沒有把純量與資料表值函式分成兩種節點。</remarks>
    [Theory]
    [InlineData(SqlObjectKind.ScalarFunction)]
    [InlineData(SqlObjectKind.InlineTableFunction)]
    [InlineData(SqlObjectKind.TableValuedFunction)]
    public void 三種函式共用同一種節點(SqlObjectKind kind)
    {
        Assert.Equal(
            Root + "/Database[@Name='Lib']/UserDefinedFunction[@Name='Loan' and @Schema='dbo']",
            SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Loan", kind).Single().Urn);
    }

    [Theory]
    [InlineData(SqlObjectKind.View, "View")]
    [InlineData(SqlObjectKind.Procedure, "StoredProcedure")]
    [InlineData(SqlObjectKind.Synonym, "Synonym")]
    [InlineData(SqlObjectKind.Sequence, "Sequence")]
    [InlineData(SqlObjectKind.TableType, "UserDefinedTableType")]
    public void 其餘種類各有自己的節點型別(SqlObjectKind kind, string node)
    {
        Assert.Equal(
            Root + "/Database[@Name='Lib']/" + node + "[@Name='Copy' and @Schema='dbo']",
            SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Copy", kind).Single().Urn);
    }

    /// <remarks>少了結構描述會停在第一個同名的節點上，而使用者看不出跳錯了。</remarks>
    [Fact]
    public void 缺結構描述就不組位址()
    {
        Assert.Empty(SqlObjectExplorerUrn.ForObject(Root, "Lib", "", "Loan", SqlObjectKind.Table));
    }

    /// <remarks>與 SMO 的 <c>Urn.EscapeString</c> 同一條規則。</remarks>
    [Fact]
    public void 名稱裡的單引號寫成兩個()
    {
        Assert.Equal(
            Root + "/Database[@Name='Lib''s']/Table[@Name='Lib_Rea''der' and @Schema='dbo']",
            SqlObjectExplorerUrn.ForObject(Root, "Lib's", "dbo", "Lib_Rea'der", SqlObjectKind.Table).Single().Urn);
    }

    /// <remarks>資料行命中要停在那一行上；指不到才退回那張表。</remarks>
    [Fact]
    public void 資料行先指那一行再退回物件()
    {
        var nodes = SqlObjectExplorerUrn.ForColumn(
            Root, "Lib", "dbo", "Cat_BookCopy", SqlObjectKind.Table, "CopyNo");

        Assert.Equal(new[] { Table + "/Column[@Name='CopyNo']", Table }, nodes.Select(node => node.Urn));
        Assert.Equal(
            new[] { SqlExplorerNodeKind.Column, SqlExplorerNodeKind.Object },
            nodes.Select(node => node.Kind));
        Assert.Equal(new[] { Table, "" }, nodes.Select(node => node.OwnerUrn));
    }

    /// <summary>
    /// 畫在別人底下的節點都帶著父物件的位址，物件本身與作業則是空的。
    /// </summary>
    /// <remarks>
    /// 接線層靠這個欄位分兩條路：有值的先到父物件再往下找一層資料夾，空的直接指。
    /// 漏掉一種的症狀是那一種在物件總管上永遠停在資料表，而它的兄弟節點好好的。
    /// </remarks>
    [Theory]
    [InlineData("C")]
    [InlineData("F")]
    [InlineData("PK")]
    [InlineData("UQ")]
    [InlineData("TR")]
    public void 掛在父物件底下的節點帶著父物件位址(string childType)
    {
        var nodes = SqlObjectExplorerUrn.ForChild(Root, Parent(childType), "CK_Cat_BookCopy");

        Assert.Equal(new[] { Table, "" }, nodes.Select(node => node.OwnerUrn));
    }

    /// <remarks>
    /// DEFAULT 的位址多一段資料行，畫它的卻是資料表的「條件約束」資料夾；
    /// 父物件寫成那個資料行的話，往下找的那一步會去一個沒有子節點的資料行底下翻。
    /// </remarks>
    [Fact]
    public void 預設值約束的父物件是資料表不是資料行()
    {
        var nodes = SqlObjectExplorerUrn.ForChild(
            Root, Parent("D", "DueDate"), "DF_Cat_BookCopy_DueDate");

        Assert.Equal(new[] { Table, Table, "" }, nodes.Select(node => node.OwnerUrn));
    }

    /// <remarks>自己就有位址的那幾種不必先繞父物件。</remarks>
    [Fact]
    public void 物件與作業沒有父物件位址()
    {
        Assert.Equal(
            "",
            SqlObjectExplorerUrn.ForObject(Root, "Lib", "dbo", "Cat_BookCopy", SqlObjectKind.Table)
                .Single().OwnerUrn);
        Assert.Equal("", SqlObjectExplorerUrn.ForJob(Root, "Loan 夜間回收").Single().OwnerUrn);
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

        Assert.Equal(
            new[] { Table + "/" + node + "[@Name='DF_Cat_BookCopy_DueDate']", Table },
            nodes.Select(item => item.Urn));
    }

    /// <remarks>
    /// DEFAULT 掛在資料行底下，而且<b>帶名稱鍵</b>——列舉器對所有具名物件一律組成
    /// <c>父位址/型別[@Name='…']</c>。少了那一段的症狀是每一次都退到第二個候選，
    /// 畫面上選到的是那個資料行。中間那一層是「指不到 Default 就停在那一行」。
    /// </remarks>
    [Fact]
    public void 預設值約束走資料行底下那一段()
    {
        var nodes = SqlObjectExplorerUrn.ForChild(
            Root, Parent("D", "DueDate"), "DF_Cat_BookCopy_DueDate");

        Assert.Equal(
            new[]
            {
                Table + "/Column[@Name='DueDate']/Default[@Name='DF_Cat_BookCopy_DueDate']",
                Table + "/Column[@Name='DueDate']",
                Table
            },
            nodes.Select(node => node.Urn));
        Assert.Equal(
            new[] { SqlExplorerNodeKind.Constraint, SqlExplorerNodeKind.Column, SqlExplorerNodeKind.Object },
            nodes.Select(node => node.Kind));
    }

    /// <remarks>問不到資料行時只剩父物件，不拿別的節點頂替。</remarks>
    [Fact]
    public void 預設值約束缺資料行就只退回父物件()
    {
        Assert.Equal(
            new[] { Table },
            SqlObjectExplorerUrn.ForChild(Root, Parent("D"), "DF_x").Select(node => node.Urn));
    }

    /// <remarks>沒見過的型別代碼不硬湊一個節點出來，退回父物件仍然是對的地方。</remarks>
    [Fact]
    public void 認不得的子物件退回父物件()
    {
        Assert.Equal(
            new[] { Table },
            SqlObjectExplorerUrn.ForChild(Root, Parent("ZZ"), "Whatever").Select(node => node.Urn));
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
}
