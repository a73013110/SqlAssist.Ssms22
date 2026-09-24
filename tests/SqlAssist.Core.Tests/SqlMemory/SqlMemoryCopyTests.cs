using System;
using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Core.Tabular;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryCopyTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);

    private static SqlHistoryItem History(string name, SqlHistoryFilter kind = SqlHistoryFilter.Executions,
        SqlConnectionLabel? connection = null, int count = 1) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "content", At, kind, name, "SELECT * FROM Loan;", connection, count);

    [Fact]
    public void History欄位不含SQL內文且時間是絕對格式()
    {
        var content = SqlTabularText.Build(SqlMemoryCopy.HistoryColumns, new[]
        {
            History("借閱查詢.sql", connection: new SqlConnectionLabel("LibraryServer", "Library"), count: 3),
            History("草稿", SqlHistoryFilter.Drafts),
        });

        var time = At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            "狀態\t名稱\t伺服器\t資料庫\t時間\t次數\r\n" +
            $"執行\t借閱查詢.sql\tLibraryServer\tLibrary\t{time}\t3\r\n" +
            $"草稿\t草稿\t\t\t{time}\t1\r\n",
            content.Tsv);
        Assert.DoesNotContain("SELECT", content.Tsv);
    }

    [Fact]
    public void Favorites欄位是名稱標註與更新時間()
    {
        var favorite = new SqlFavorite(Guid.NewGuid(), "讀者清單", "說明不複製", Guid.NewGuid(), "LibraryServer", null);
        var content = SqlTabularText.Build(SqlMemoryCopy.FavoriteColumns,
            new[] { new SqlFavoriteItem(favorite, Guid.NewGuid(), "content", "SELECT * FROM Lib_Reader;", At) });

        Assert.Equal("名稱\t伺服器\t資料庫\t更新時間\r\n讀者清單\tLibraryServer\t\t" + SqlMemoryCopy.Time(At) + "\r\n", content.Tsv);
    }
}
