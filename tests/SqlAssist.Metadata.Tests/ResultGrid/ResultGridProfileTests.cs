using System;
using System.Data.SqlTypes;
using System.Linq;
using SqlAssist.Metadata.ResultGrid;
using Xunit;

namespace SqlAssist.Metadata.Tests.ResultGrid;

/// <summary>
/// 每一欄的統計摘要。
/// </summary>
public sealed class ResultGridProfileTests
{
    private static ResultGridTable Table((string Name, string Type)[] columns, object?[][] rows) =>
        new(columns.Select(c => new ResultGridColumn(c.Name, c.Type)).ToArray(), rows, isWholeResult: true);

    private static ResultGridColumnProfile Profile(string type, params object?[] values)
    {
        var rows = values.Select(v => new[] { v }).ToArray();
        return ResultGridProfile.Build(Table(new[] { ("CopyNo", type) }, rows))[0];
    }

    /// <remarks>
    /// 這兩件事是這個功能存在的理由。實測的查詢有 178 欄，「哪幾欄整欄是 NULL、
    /// 哪幾欄從頭到尾只有一個值」看資料看不出來，看摘要一眼就有。
    /// </remarks>
    [Fact]
    public void AllNullAndConstantColumnsAreFlagged()
    {
        var allNull = Profile("nvarchar(20)", null, null, null);
        Assert.True(allNull.IsAllNull);
        Assert.True(allNull.IsConstant);
        Assert.Equal(3, allNull.NullCount);
        Assert.Equal(1, allNull.DistinctCount);

        var constant = Profile("nvarchar(20)", new SqlString("A01"), new SqlString("A01"));
        Assert.False(constant.IsAllNull);
        Assert.True(constant.IsConstant);

        var varied = Profile("nvarchar(20)", new SqlString("A01"), new SqlString("A02"));
        Assert.False(varied.IsConstant);
        Assert.Equal(2, varied.DistinctCount);
    }

    /// <remarks>
    /// <c>NULL</c> 與空字串必須分開算：兩者在格線上都是不顯眼的一格，
    /// 而查問題的時候它們代表完全不同的事。
    /// </remarks>
    [Fact]
    public void EmptyTextIsCountedSeparatelyFromNull()
    {
        var profile = Profile(
            "nvarchar(20)",
            new SqlString(string.Empty),
            null,
            new SqlString("A01"));

        Assert.Equal(1, profile.NullCount);
        Assert.Equal(1, profile.EmptyTextCount);
        Assert.Equal(3, profile.DistinctCount);
    }

    /// <remarks>
    /// 最小與最大寫成字面值，因為它們的下一步幾乎一定是被貼進一句 <c>WHERE</c>。
    /// </remarks>
    [Fact]
    public void MinimumAndMaximumAreLiterals()
    {
        var profile = Profile("int", new SqlInt32(7), new SqlInt32(2), new SqlInt32(5));

        Assert.Equal("2", profile.Minimum);
        Assert.Equal("7", profile.Maximum);
    }

    /// <remarks>
    /// <c>NULL</c> 不參與最小最大。SQL 的彙總函式也是這樣算的，
    /// 而如果讓它參與，每一個有 <c>NULL</c> 的欄位最小值都會變成 <c>NULL</c>。
    /// </remarks>
    [Fact]
    public void NullsAreExcludedFromMinimumAndMaximum()
    {
        var profile = Profile("int", null, new SqlInt32(5), null, new SqlInt32(9));

        Assert.Equal("5", profile.Minimum);
        Assert.Equal("9", profile.Maximum);
    }

    /// <remarks>
    /// 文字欄位的字元數範圍是找截斷的第一個線索：整欄都剛好 20 個字元的
    /// <c>nvarchar(20)</c> 值得看一眼。
    /// </remarks>
    [Fact]
    public void TextLengthIsReportedAsARange()
    {
        Assert.Equal(
            "0–3",
            Profile("nvarchar(20)", new SqlString("A01"), new SqlString(string.Empty)).TextLength);

        Assert.Equal(
            "3",
            Profile("nvarchar(20)", new SqlString("A01"), new SqlString("A02")).TextLength);

        Assert.Equal(string.Empty, Profile("int", new SqlInt32(1)).TextLength);
    }

    /// <remarks>
    /// <c>byte[]</c> 的預設相等性是參考比較，兩個內容相同的位元組陣列會被算成
    /// 兩個相異值。用字面值當鍵就不會，而那個錯誤原本沒有任何徵兆。
    /// </remarks>
    [Fact]
    public void EqualBinaryValuesCountAsOne()
    {
        var profile = Profile(
            "varbinary(8)",
            new byte[] { 1, 2, 3 },
            new byte[] { 1, 2, 3 },
            new byte[] { 4 });

        Assert.Equal(2, profile.DistinctCount);
    }

    /// <remarks>
    /// 比不出大小的值讓整欄的最小最大一起放棄，不是只算得出來的那些——
    /// 後者會給出一個看起來正常、實際上少算了一部分資料的範圍。
    /// </remarks>
    [Fact]
    public void IncomparableValuesDropTheRangeEntirely()
    {
        var profile = Profile("sql_variant", new SqlInt32(1), new Uri("https://example.invalid"));

        Assert.Equal(string.Empty, profile.Minimum);
        Assert.Equal(string.Empty, profile.Maximum);
        Assert.Equal(2, profile.DistinctCount);
    }

    [Fact]
    public void EveryColumnGetsARow()
    {
        var profiles = ResultGridProfile.Build(Table(
            new[] { ("BranchId", "int"), ("CopyNo", "nvarchar(20)") },
            new[]
            {
                new object?[] { new SqlInt32(1), new SqlString("A01") },
                new object?[] { new SqlInt32(1), null },
            }));

        Assert.Equal(new[] { "BranchId", "CopyNo" }, profiles.Select(p => p.Name));
        Assert.Equal(new[] { "int", "nvarchar(20)" }, profiles.Select(p => p.DataType));
        Assert.All(profiles, p => Assert.Equal(2, p.RowCount));
    }

    /// <remarks>
    /// 型別問不出來時顯示 <c>?</c>，不是空白——空白看起來像沒算到，
    /// 而這一欄其實統計得好好的。
    /// </remarks>
    [Fact]
    public void MissingTypeShowsAQuestionMark()
    {
        Assert.Equal("?", Profile(string.Empty, new SqlInt32(1)).DataType);
    }
}
