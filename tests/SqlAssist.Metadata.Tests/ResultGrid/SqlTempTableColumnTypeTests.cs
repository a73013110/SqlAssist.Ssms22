using System.Linq;
using SqlAssist.Metadata.ResultGrid;
using Xunit;

namespace SqlAssist.Metadata.Tests.ResultGrid;

/// <summary>
/// <c>CREATE TABLE #temp</c> 裡每一欄的長度與精確度。
/// </summary>
/// <remarks>
/// 這一組全部是「產得出來、跑得動、資料卻不一樣」的案例，所以每一個都直接斷言
/// 寫出來的那一行。實測的病灶是結果格線只回報型別名稱：<c>varchar</c> 在
/// <c>CREATE TABLE</c> 裡是 <c>varchar(1)</c>，一執行就是「字串或二進位資料會被截斷」；
/// <c>decimal</c> 是 <c>decimal(18,0)</c>，連錯誤訊息都沒有。
/// </remarks>
public sealed class SqlTempTableColumnTypeTests
{
    private static string Build(ResultGridColumn column, params object?[]? values) =>
        SqlTempTableScript.Build(new ResultGridTable(
            new[] { column },
            (values ?? new object?[] { null }).Select(value => new object?[] { value }).ToArray(),
            isWholeResult: true));

    /// <remarks>
    /// 結構描述問得到長度時照抄。這是正常路徑，也是唯一一條長度與伺服器一致的。
    /// </remarks>
    [Fact]
    public void ReportedLengthIsUsed()
    {
        var script = Build(new ResultGridColumn("CopyNo", "varchar", maxLength: 20), "A01");

        Assert.Contains("    CopyNo varchar(20) NULL", script);
    }

    /// <remarks>
    /// 長度問不出來時退到 <c>(max)</c>，<b>不是</b>不寫長度——不寫就是
    /// <c>varchar(1)</c>，而那正是回報的那一句「字串或二進位資料會被截斷」。
    /// 也不是照觀察到的最長那一列開長度：使用者改資料重跑時會被截斷。
    /// </remarks>
    [Theory]
    [InlineData("varchar", "varchar(max)")]
    [InlineData("nvarchar", "nvarchar(max)")]
    [InlineData("varbinary", "varbinary(max)")]
    public void MissingLengthWidensToMax(string serverType, string expected)
    {
        var script = Build(new ResultGridColumn("Detail", serverType), null);

        Assert.Contains("    Detail " + expected + " NULL", script);
    }

    /// <remarks>
    /// 定長型別沒有 <c>char(max)</c> 可以退，所以一併換成變長的。
    /// 在暫存資料表上兩者唯一的差別是尾端補空白。
    /// </remarks>
    [Theory]
    [InlineData("char", "varchar(max)")]
    [InlineData("nchar", "nvarchar(max)")]
    [InlineData("binary", "varbinary(max)")]
    public void FixedLengthTypesWidenToVariableLength(string serverType, string expected)
    {
        var script = Build(new ResultGridColumn("Detail", serverType), null);

        Assert.Contains("    Detail " + expected + " NULL", script);
    }

    /// <remarks>
    /// <c>nvarchar(max)</c> 的結構描述長度是 1073741823，遠超過 <c>nvarchar</c> 的
    /// 4000 上限。照抄的話 <c>CREATE TABLE</c> 直接是錯誤，所以超過上限一律寫成
    /// <c>(max)</c>——那本來就是它的意思。
    /// </remarks>
    [Fact]
    public void LengthBeyondTheTypeLimitBecomesMax()
    {
        var script = Build(new ResultGridColumn("Detail", "nvarchar", maxLength: 1073741823), "x");

        Assert.Contains("    Detail nvarchar(max) NULL", script);
    }

    /// <remarks>
    /// 精確度問得到就照抄。少了它是 <c>decimal(18,0)</c>，
    /// 小數點後面整段被四捨五入掉而沒有任何訊息。
    /// </remarks>
    [Fact]
    public void ReportedPrecisionIsUsed()
    {
        var script = Build(
            new ResultGridColumn("Fine", "decimal", precision: 18, scale: 4),
            12.5m);

        Assert.Contains("    Fine decimal(18, 4) NULL", script);
    }

    /// <remarks>
    /// <c>decimal</c> 沒有 <c>(max)</c> 可以退，唯一還能放寬的方向是總位數取滿 38、
    /// 小數位數取實際出現過的最多那一個。同一欄的值來自同一個 <c>decimal(p, s)</c>，
    /// 所以這樣一定裝得下。
    /// </remarks>
    [Fact]
    public void MissingPrecisionFitsScaleToTheValues()
    {
        var script = Build(new ResultGridColumn("Fine", "decimal"), 1.5m, 12.345m, null);

        Assert.Contains("    Fine decimal(38, 3) NULL", script);
    }

    /// <remarks>
    /// 整欄都沒有小數時是 <c>decimal(38, 0)</c>，不是不寫精確度。
    /// </remarks>
    [Fact]
    public void MissingPrecisionWithWholeNumbersKeepsScaleZero()
    {
        var script = Build(new ResultGridColumn("Fine", "numeric"), 123m, null);

        Assert.Contains("    Fine numeric(38, 0) NULL", script);
    }

    /// <remarks>
    /// 省略時的預設值就是最大值的型別不加括號：<c>datetime2</c> 與 <c>time</c>
    /// 是 7、<c>float</c> 是 53。補上去只是雜訊，漏掉也不會截斷。
    /// </remarks>
    [Theory]
    [InlineData("int")]
    [InlineData("datetime")]
    [InlineData("datetime2")]
    [InlineData("uniqueidentifier")]
    public void TypesWithoutATruncatingDefaultAreCopiedAsIs(string serverType)
    {
        var script = Build(new ResultGridColumn("PerformedAt", serverType), null);

        Assert.Contains("    PerformedAt " + serverType + " NULL", script);
    }

    /// <remarks>
    /// 哪一天 SSMS 開始連長度一起報，照抄那一份，不要再從結構描述組一次。
    /// </remarks>
    [Fact]
    public void ServerTypeThatAlreadyCarriesItsLengthIsCopiedAsIs()
    {
        var script = Build(new ResultGridColumn("CopyNo", "nvarchar(20)", maxLength: 4000), "A01");

        Assert.Contains("    CopyNo nvarchar(20) NULL", script);
    }
}
