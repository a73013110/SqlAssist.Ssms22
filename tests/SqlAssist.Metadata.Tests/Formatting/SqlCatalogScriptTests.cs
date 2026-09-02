using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

/// <summary>
/// 同義字與序列的定義：<c>OBJECT_DEFINITION</c> 對這兩種一律回傳 NULL，
/// 它們的定義就是目錄檢視上的那幾個欄位。
/// </summary>
public sealed class SqlCatalogScriptTests
{
    private static SqlObjectInfo Synonym() => new(1, "dbo", "syn_Loan", SqlObjectKind.Synonym);

    private static SqlObjectInfo SequenceObject() =>
        new(2, "dbo", "seq_LoanNo", SqlObjectKind.Sequence);

    private static SqlSequenceInfo Sequence(
        string dataType = "int",
        bool isCycling = false,
        bool isCached = true,
        int? cacheSize = 50)
    {
        return new SqlSequenceInfo(
            dataType,
            startValue: "1",
            increment: "1",
            minimumValue: "1",
            maximumValue: "2147483647",
            isCycling,
            isCached,
            cacheSize);
    }

    /// <remarks>
    /// <c>base_object_name</c> 存的已經是加好方括號的多段式名稱，原樣寫回去——
    /// 自己拆一次的話，一個跨伺服器的同義字會被寫成本機的名稱，
    /// 而那份指令碼照樣執行得動。
    /// </remarks>
    [Fact]
    public void 同義字組出CREATE_SYNONYM()
    {
        var script = SqlCatalogScript.ForSynonym(Synonym(), "[Lib].[dbo].[Loan]");

        Assert.Equal(
            "CREATE SYNONYM [dbo].[syn_Loan]" + System.Environment.NewLine +
            "FOR [Lib].[dbo].[Loan];" + System.Environment.NewLine,
            script);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 查不到指向的物件就沒有定義(string? baseObjectName)
    {
        Assert.Null(SqlCatalogScript.ForSynonym(Synonym(), baseObjectName));
    }

    [Fact]
    public void 序列組出CREATE_SEQUENCE()
    {
        var script = SqlCatalogScript.ForSequence(SequenceObject(), Sequence());

        Assert.Contains("CREATE SEQUENCE [dbo].[seq_LoanNo]", script);
        Assert.Contains("    AS int", script);
        Assert.Contains("    START WITH 1", script);
        Assert.Contains("    INCREMENT BY 1", script);
        Assert.Contains("    MINVALUE 1", script);
        Assert.Contains("    MAXVALUE 2147483647", script);
        Assert.Contains("    NO CYCLE", script);
        Assert.EndsWith("    CACHE 50;" + System.Environment.NewLine, script);
    }

    /// <remarks>
    /// 三種寫法而不是兩種：<c>is_cached = 1</c> 加上 <c>cache_size</c> 為 NULL
    /// 是「開著但大小交給引擎決定」，寫成 <c>CACHE 0</c> 會被拒絕。
    /// </remarks>
    [Theory]
    [InlineData(true, 50, "CACHE 50")]
    [InlineData(true, null, "CACHE")]
    [InlineData(false, null, "NO CACHE")]
    public void 快取子句有三種寫法(bool isCached, int? cacheSize, string expected)
    {
        var script = SqlCatalogScript.ForSequence(
            SequenceObject(),
            Sequence(isCached: isCached, cacheSize: cacheSize));

        Assert.EndsWith("    " + expected + ";" + System.Environment.NewLine, script);
    }

    [Fact]
    public void 循環的序列寫成CYCLE()
    {
        Assert.Contains(
            "    CYCLE",
            SqlCatalogScript.ForSequence(SequenceObject(), Sequence(isCycling: true)));
    }

    /// <remarks>
    /// <c>decimal</c> 非帶精確度與小數位不可：<c>AS decimal</c> 建出來的
    /// 不是同一個序列。型別走與資料行同一支格式化，這裡守的是它真的被帶過來。
    /// </remarks>
    [Fact]
    public void 型別原樣帶著精確度()
    {
        Assert.Contains(
            "    AS decimal(18,0)",
            SqlCatalogScript.ForSequence(SequenceObject(), Sequence(dataType: "decimal(18,0)")));
    }

    [Fact]
    public void 查不到序列那一列就沒有定義()
    {
        Assert.Null(SqlCatalogScript.ForSequence(SequenceObject(), sequence: null));
    }
}
