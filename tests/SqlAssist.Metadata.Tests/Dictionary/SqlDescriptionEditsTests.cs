using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

public sealed class SqlDescriptionEditsTests
{
    private static readonly SqlDescriptionTarget ReaderNo =
        new(SqlDictionaryObjectKind.Table, "dbo", "Loan", "ReaderNo");

    /// <summary>改回原值就不算變更；前後空白與 NULL／空字串都不算內容。</summary>
    [Fact]
    public void 改回原值就不算變更()
    {
        var edits = new SqlDescriptionEdits();

        Assert.True(edits.Set(ReaderNo, null, "讀者編號"));
        Assert.Equal(1, edits.Count);
        Assert.Equal("讀者編號", edits.Current(ReaderNo, null));
        Assert.False(edits.Set(ReaderNo, null, " 讀者編號 "));

        Assert.True(edits.Set(ReaderNo, null, "  "));
        Assert.Equal(0, edits.Count);
        Assert.Equal("", edits.Current(ReaderNo, null));
    }

    /// <summary>
    /// 原值記第一次的：重新整理之後快照換成別人改過的值，併發檢查仍要拿使用者開始編輯時看到的那一句比。
    /// </summary>
    [Fact]
    public void 原值記第一次編輯時的值()
    {
        var edits = new SqlDescriptionEdits();
        edits.Set(ReaderNo, "舊說明", "新說明");
        edits.Set(ReaderNo, "別人改過", "再改一次");

        var change = Assert.Single(edits.Changes);
        Assert.Equal("舊說明", change.Original);
        Assert.Equal("再改一次", change.Value);
    }

    [Fact]
    public void 清空是拿掉屬性_太長整批不能寫()
    {
        var edits = new SqlDescriptionEdits();
        edits.Set(ReaderNo, "讀者編號", "");
        Assert.True(Assert.Single(edits.Changes).IsRemoval);

        edits.Set(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan"), null,
            new string('借', SqlDescriptionValue.MaxLength + 1));
        Assert.True(edits.HasTooLong);
    }

    /// <summary>
    /// 重新載入之後伺服器上已經是那一句的不再算變更；寫回之後又改了的那一格留著，原值不換。
    /// </summary>
    [Fact]
    public void 載入的值已經是編輯那一句就不再算變更()
    {
        var edits = new SqlDescriptionEdits();
        var loan = new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan");
        edits.Set(ReaderNo, null, "讀者編號");
        edits.Set(loan, null, "借閱");
        edits.Set(loan, null, "借閱主檔");

        var loaded = new System.Collections.Generic.Dictionary<SqlDescriptionTarget, string>
        {
            [ReaderNo] = "讀者編號",
            [loan] = "借閱",
        };
        var settled = edits.Settle(target => loaded[target]);

        Assert.Equal(new[] { ReaderNo }, settled);
        var left = Assert.Single(edits.Changes);
        Assert.Equal(loan, left.Target);
        Assert.Equal("", left.Original);
    }

    /// <summary>指令碼照目標排：物件本身在它的資料行之前，同一批變更每次產生同一份文字。</summary>
    [Fact]
    public void 變更照目標排序()
    {
        var edits = new SqlDescriptionEdits();
        edits.Set(ReaderNo, null, "讀者編號");
        edits.Set(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan", "LoanDate"), null, "借閱日期");
        edits.Set(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan"), null, "借閱");
        edits.Set(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Branch"), null, "分館");

        Assert.Equal(
            new[] { "dbo.Branch", "dbo.Loan", "dbo.Loan.LoanDate", "dbo.Loan.ReaderNo" },
            System.Linq.Enumerable.Select(edits.Changes, change => change.Target.ToString()));
    }
}
