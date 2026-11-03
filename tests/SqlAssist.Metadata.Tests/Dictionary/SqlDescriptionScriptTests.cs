using System;
using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

public sealed class SqlDescriptionScriptTests
{
    private const string NewLine = "\n";

    /// <summary>
    /// 有值是 add-or-update、清空是 drop，兩種都包存在性判斷；檢視的資料行用 <c>'VIEW'</c>。
    /// </summary>
    [Fact]
    public void 有值寫入_清空拿掉_檢視用VIEW()
    {
        var changes = new[]
        {
            new SqlDescriptionChange(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan", "ReaderNo"), null, "讀者編號"),
            new SqlDescriptionChange(new SqlDescriptionTarget(SqlDictionaryObjectKind.View, "rpt", "LoanByReader"), "舊說明", ""),
        };

        var script = SqlDescriptionScript.Statements(changes, NewLine);

        Assert.Equal(
            "IF NOT EXISTS (SELECT 1 FROM sys.fn_listextendedproperty(N'MS_Description', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'ReaderNo'))\n" +
            "    EXEC sp_addextendedproperty N'MS_Description', N'讀者編號', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'ReaderNo'\n" +
            "ELSE\n" +
            "    EXEC sp_updateextendedproperty N'MS_Description', N'讀者編號', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'ReaderNo';\n" +
            "\n" +
            "IF EXISTS (SELECT 1 FROM sys.fn_listextendedproperty(N'MS_Description', 'SCHEMA', N'rpt', 'VIEW', N'LoanByReader', NULL, NULL))\n" +
            "    EXEC sp_dropextendedproperty N'MS_Description', 'SCHEMA', N'rpt', 'VIEW', N'LoanByReader', NULL, NULL;\n",
            script);
    }

    /// <summary>查詢視窗那一份多了 <c>USE</c> 與交易：查詢視窗連在哪個資料庫由 SSMS 決定。</summary>
    [Fact]
    public void 完整指令碼有USE與交易()
    {
        var changes = new[]
        {
            new SqlDescriptionChange(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan"), null, "含 ' 引號"),
        };

        var script = SqlDescriptionScript.Build("Lib]Archive", changes, NewLine);

        Assert.StartsWith("-- ", script, StringComparison.Ordinal);
        Assert.Contains("USE [Lib]]Archive];\n", script, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n", script, StringComparison.Ordinal);
        Assert.Contains("N'含 '' 引號'", script, StringComparison.Ordinal);
        Assert.EndsWith("COMMIT TRANSACTION;\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 併發檢查一輪問完每一項()
    {
        var changes = new[]
        {
            new SqlDescriptionChange(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan"), null, "借閱"),
            new SqlDescriptionChange(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Loan", "LoanNo"), null, "借閱編號"),
        };

        Assert.Equal(
            "SELECT 0 AS change_index, CONVERT(nvarchar(max), value) AS current_value FROM sys.fn_listextendedproperty(N'MS_Description', 'SCHEMA', N'dbo', 'TABLE', N'Loan', NULL, NULL)\n" +
            "UNION ALL\n" +
            "SELECT 1 AS change_index, CONVERT(nvarchar(max), value) AS current_value FROM sys.fn_listextendedproperty(N'MS_Description', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'LoanNo');",
            SqlDescriptionScript.CurrentValuesQuery(changes, NewLine));
    }
}
