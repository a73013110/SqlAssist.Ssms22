using System;
using SqlAssist.Core.Localization;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryTimeTextTests
{
    [Theory]
    [InlineData(0, "剛剛")]
    [InlineData(59, "剛剛")]
    [InlineData(60, "1 分鐘前")]
    [InlineData(3599, "59 分鐘前")]
    [InlineData(3600, "1 小時前")]
    [InlineData(86400, "昨天")]
    [InlineData(259200, "3 天前")]
    public void RelativeTimeUsesStableBoundaries(int seconds, string expected)
    {
        var now = new DateTimeOffset(new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Local));
        var text = SqlMemoryTimeText.RelativeTime(now.AddSeconds(-seconds), now);
        Assert.StartsWith(expected, text);
        Assert.Contains(now.AddSeconds(-seconds).ToLocalTime().ToString("HH:mm"), text);
    }

    [Fact]
    public void OldTimestampRetainsYearAndFutureClockSkewDoesNotShowNegativeMinutes()
    {
        var now = new DateTimeOffset(new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Local));
        Assert.Equal("2025/09/13 14:00", SqlMemoryTimeText.RelativeTime(now.AddYears(-1), now));
        Assert.StartsWith("剛剛", SqlMemoryTimeText.RelativeTime(now.AddMinutes(2), now));
    }

    /// <summary>儲存層訊息是隔離 AppDomain 寫的診斷繁中；使用者看到的說明只從分類、原因與錯誤碼組。</summary>
    [Fact]
    public void StorageFailuresAreDescribedFromTheClassificationNotTheDiagnosticMessage()
    {
        var io = new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Io,
            "SQL Memory 儲存失敗（SQLite 10/3338）：disk I/O error", 10, 3338);
        Assert.Equal("備份失敗：無法讀寫資料庫檔案；請檢查磁碟空間與權限（SQLite 10/3338）。", SqlMemoryTimeText.Failure("備份", io));
        var exists = new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidArgument, "備份檔案已存在；請選擇新的檔名。",
            reason: SqlMemoryStorageReason.BackupFileExists);
        var provider = SqlMemoryRuntimeStatus.Initial.WithOpenFailure(new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Unknown,
            "SQL Memory 儲存失敗（InvalidOperationException）：SQLite provider 不是來自擴充目錄。"));
        var now = new DateTimeOffset(new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Local));

        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            Assert.Equal("Back up failed: The backup file already exists; choose a new file name.",
                SqlMemoryTimeText.Failure("Back up", exists));
            Assert.Equal("Can't open SQL Memory: An unexpected storage error occurred.", provider.Message);
            Assert.StartsWith("5 min ago (", SqlMemoryTimeText.RelativeTime(now.AddMinutes(-5), now));
        }
    }
}
