using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Tabular;
using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

public sealed class SqlDescriptionPasteTests
{
    private static readonly IReadOnlyList<SqlDescriptionPasteSlot> Slots =
        LibraryDictionary.Create().Objects
            .SelectMany(item => item.Columns.Select(column => new SqlDescriptionPasteSlot(item, column)))
            .ToList();

    private static string[] Describe(SqlDescriptionPastePlan plan) =>
        plan.Assignments.Select(pair => pair.Key.Target + "=" + pair.Value).ToArray();

    /// <summary>一欄：從選定那一列往下一格一列，超出畫面最後一列的算略過。</summary>
    [Fact]
    public void 一欄從選定那一列往下填()
    {
        var plan = SqlDescriptionPaste.Plan(Slots, Slots.Count - 2, SqlTabularText.ParseTsv("讀者\r\n次數\r\n多的\r\n"));

        Assert.Equal(new[] { "rpt.LoanByReader.READERNO=讀者", "rpt.LoanByReader.LoanCount=次數" }, Describe(plan));
        Assert.Equal(1, plan.Skipped);
    }

    /// <summary>
    /// 兩欄：照名稱找列，同名的每一列都補；表.資料行只補那一張；標頭對不到算略過。
    /// </summary>
    [Fact]
    public void 兩欄照名稱填同名的每一列()
    {
        var plan = SqlDescriptionPaste.Plan(Slots, 0,
            SqlTabularText.ParseTsv("欄位\t說明\r\nreaderno\t讀者編號\r\nLoanDetail.LoanNo\t借閱編號\r\n"));

        Assert.Equal(
            new[]
            {
                "dbo.Lib_Reader.ReaderNo=讀者編號", "dbo.Loan.ReaderNo=讀者編號", "rpt.LoanByReader.READERNO=讀者編號",
                "dbo.LoanDetail.LoanNo=借閱編號",
            },
            Describe(plan));
        Assert.Equal(1, plan.Skipped);
    }
}
