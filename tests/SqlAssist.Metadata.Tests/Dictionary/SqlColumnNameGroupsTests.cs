using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

public sealed class SqlColumnNameGroupsTests
{
    /// <summary>
    /// <c>ReaderNo</c> 在兩張表一個檢視：名稱不分大小寫歸一組，型別不一致，寫過的說明只有一種所以給得出建議。
    /// </summary>
    [Fact]
    public void 同名不分大小寫並比較型別與說明()
    {
        var groups = SqlColumnNameGroups.Build(LibraryDictionary.Create(), LibraryDictionary.Loaded);

        var readerNo = groups.Find("readerno")!;
        Assert.Equal(3, readerNo.Count);
        Assert.Equal(new[] { "varchar(10)", "varchar(12)" }, readerNo.Types);
        Assert.True(readerNo.HasMixedTypes);
        Assert.Equal(new[] { "讀者編號" }, readerNo.Descriptions);
        Assert.Equal(2, readerNo.MissingDescriptions);
        Assert.Equal("讀者編號", readerNo.Suggestion);
        Assert.True(readerNo.IsInconsistent);

        // 只有一張表的不算不一致。
        Assert.False(groups.Find("CopyNo")!.IsInconsistent);
        // LoanNo 兩張表、型別一樣、都沒說明：一致，但也沒有建議可給。
        var loanNo = groups.Find("LoanNo")!;
        Assert.False(loanNo.IsInconsistent);
        Assert.Null(loanNo.Suggestion);
    }

    /// <summary>還沒寫回的編輯算數：補了兩種不同的說明就變成不一致，也不再給建議。</summary>
    [Fact]
    public void 還沒寫回的編輯也算數()
    {
        var edits = new SqlDescriptionEdits();
        edits.Set(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan", "ReaderNo"), null, "借書證號");

        var groups = SqlColumnNameGroups.Build(LibraryDictionary.Create(), edits.Current);

        var readerNo = groups.Find("ReaderNo")!;
        Assert.True(readerNo.HasMixedDescriptions);
        Assert.Null(readerNo.Suggestion);
    }
}
