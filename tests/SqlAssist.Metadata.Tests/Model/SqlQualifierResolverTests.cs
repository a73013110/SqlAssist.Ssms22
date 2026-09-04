using System;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 限定字最左邊那一段是結構描述、資料庫還是連結伺服器。
/// </summary>
/// <remarks>
/// 三者在文字上是同一個形狀，而右對齊一律先猜結構描述。猜錯沒有徵兆：
/// 清單一筆都比不中，使用者看到的只是「沒有建議」。
/// </remarks>
public sealed class SqlQualifierResolverTests
{
    private static readonly SqlDatabaseSnapshot Local = new(
        "Lib",
        Array.Empty<SqlObjectInfo>(),
        new[] { "dbo", "Cat" },
        new[] { "LibArchive", "Lib" },
        DateTimeOffset.UtcNow,
        new[] { "LibMirror", "192.0.2.10" });

    private static SqlObjectPath Qualifier(params string[] parts)
    {
        Assert.True(SqlObjectPath.TryParseQualifier(parts, out var path));
        return path!;
    }

    private static SqlObjectPath Resolve(params string[] parts)
    {
        return SqlQualifierResolver.Resolve(Qualifier(parts), Local);
    }

    [Fact]
    public void 結構描述維持右對齊的原判()
    {
        var path = Resolve("dbo");

        Assert.Equal("dbo", path.SchemaName);
        Assert.True(path.IsLocal);
        Assert.Equal(SqlQualifierSlot.Schema, path.QualifierEnd);
    }

    [Fact]
    public void 資料庫改讀成跨資料庫()
    {
        var path = Resolve("LibArchive");

        Assert.Equal("LibArchive", path.DatabaseName);
        Assert.Null(path.SchemaName);
        Assert.True(path.IsCrossDatabase);
    }

    [Fact]
    public void 連結伺服器改讀成跨伺服器()
    {
        var path = Resolve("LibMirror");

        Assert.Equal("LibMirror", path.ServerName);
        Assert.True(path.IsCrossServer);
    }

    /// <remarks>
    /// 連結伺服器可以直接以位址命名，那時它只有加了方括號才寫得出來。
    /// 任何「這看起來像不像識別字」的判斷都不成立，只能照名單比對。
    /// </remarks>
    [Fact]
    public void 以位址命名的連結伺服器一樣認得()
    {
        Assert.Equal("192.0.2.10", Resolve("192.0.2.10").ServerName);
    }

    [Fact]
    public void 伺服器加資料庫兩段一起往左挪()
    {
        var path = Resolve("LibMirror", "LibArchive");

        Assert.Equal("LibMirror", path.ServerName);
        Assert.Equal("LibArchive", path.DatabaseName);
        Assert.Null(path.SchemaName);
    }

    /// <remarks>
    /// 三份名單都沒有時刻意不猜：猜出來的目標會讓下游真的去開一條連線，
    /// 而使用者打到一半的名稱本來就什麼都不是。實測的症狀是每按一次鍵
    /// 就對一個不存在的資料庫開一條註定失敗的連線。
    /// </remarks>
    [Fact]
    public void 認不出來時維持原判()
    {
        var path = Resolve("libr");

        Assert.Equal("libr", path.SchemaName);
        Assert.True(path.IsLocal);
    }

    /// <remarks>
    /// 名稱撞在一起時選近的那一個：結構描述就在眼前這個資料庫裡，
    /// 選遠的會安靜地把清單換成另一台伺服器的內容，而畫面上看不出來。
    /// </remarks>
    [Fact]
    public void 名稱相撞時結構描述優先()
    {
        var snapshot = new SqlDatabaseSnapshot(
            "Lib",
            Array.Empty<SqlObjectInfo>(),
            new[] { "Cat" },
            new[] { "Cat" },
            DateTimeOffset.UtcNow,
            new[] { "Cat" });

        var path = SqlQualifierResolver.Resolve(Qualifier("Cat"), snapshot);

        Assert.Equal("Cat", path.SchemaName);
        Assert.True(path.IsLocal);
    }

    /// <remarks>
    /// 空的中間段仍然佔一格，最左邊那一段本來就落在資料庫那一格。
    /// 當成一段來挪的話會被推成伺服器，於是清單改列一台不存在的伺服器的內容。
    /// </remarks>
    [Fact]
    public void 省略結構描述的寫法不再往左挪()
    {
        var path = Resolve("LibArchive", string.Empty);

        Assert.Equal("LibArchive", path.DatabaseName);
        Assert.Null(path.ServerName);
        Assert.Equal(SqlQualifierSlot.Schema, path.QualifierEnd);
    }

    [Fact]
    public void 沒有快照時維持原判()
    {
        var qualifier = Qualifier("LibArchive");

        Assert.Same(qualifier, SqlQualifierResolver.Resolve(qualifier, SqlDatabaseSnapshot.Empty));
    }
}
