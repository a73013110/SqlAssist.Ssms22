using System.Threading;
using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

/// <summary>寫回說明：先確認沒有人先改過，再在同一個交易裡全部寫入。</summary>
public sealed class SqlDescriptionWriterTests
{
    private static readonly SqlDescriptionTarget Loan = new(SqlDictionaryObjectKind.Table, "dbo", "Loan");

    private static readonly SqlDescriptionTarget ReaderNo = new(SqlDictionaryObjectKind.Table, "dbo", "Loan", "ReaderNo");

    [Fact]
    public void 沒有人先改過就全部寫入並提交()
    {
        var server = new FakeDictionaryServer();
        server.Descriptions["dbo.Loan"] = "借閱";
        var changes = new[]
        {
            new SqlDescriptionChange(Loan, "借閱", "借閱主檔"),
            new SqlDescriptionChange(ReaderNo, null, "讀者編號"),
        };

        var result = SqlDescriptionWriter.Write(server, changes, CancellationToken.None);

        Assert.Equal(SqlDescriptionWriteStatus.Applied, result.Status);
        Assert.True(server.Committed);
        Assert.False(server.RolledBack);
        Assert.Equal(SqlDescriptionScript.Statements(changes, "\r\n"), Assert.Single(server.Executed));
    }

    /// <summary>
    /// 載入之後別人改過的目標擋下整批，一項都不寫；別人改成的正好是要寫的值不算衝突。
    /// </summary>
    [Fact]
    public void 別人先改過就整批退回()
    {
        var server = new FakeDictionaryServer();
        server.Descriptions["dbo.Loan"] = "別人寫的";
        server.Descriptions["dbo.Loan.ReaderNo"] = "讀者編號";
        var changes = new[]
        {
            new SqlDescriptionChange(Loan, "借閱", "借閱主檔"),
            new SqlDescriptionChange(ReaderNo, null, "讀者編號"),
        };

        var result = SqlDescriptionWriter.Write(server, changes, CancellationToken.None);

        Assert.Equal(SqlDescriptionWriteStatus.Conflicted, result.Status);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(Loan, conflict.Change.Target);
        Assert.Equal("別人寫的", conflict.Current);
        Assert.True(server.RolledBack);
        Assert.False(server.Committed);
        Assert.Empty(server.Executed);
    }

    /// <summary>伺服器拒絕時帶回它說的那一句，不提交。</summary>
    [Fact]
    public void 伺服器拒絕時帶回訊息()
    {
        var server = new FakeDictionaryServer { DenyWrite = "沒有 ALTER 權限。" };

        var result = SqlDescriptionWriter.Write(server, new[] { new SqlDescriptionChange(Loan, null, "借閱") }, CancellationToken.None);

        Assert.Equal(SqlDescriptionWriteStatus.Failed, result.Status);
        Assert.Equal("沒有 ALTER 權限。", result.Message);
        Assert.False(server.Committed);
    }
}
