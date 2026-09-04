using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// 右對齊猜錯時，整條限定字要往左挪。
/// </summary>
/// <remarks>
/// 只看文字時 <c>dbo.</c>、<c>LibArchive.</c> 與 <c>LibMirror.</c> 是同一個形狀，
/// 右對齊只能一律先當成結構描述。猜錯沒有徵兆：清單一筆都比不中，
/// 而使用者看到的只是「沒有建議」。
/// </remarks>
public sealed class SqlQualifierRealignmentTests
{
    private static SqlObjectPath Qualifier(params string[] parts)
    {
        Assert.True(SqlObjectPath.TryParseQualifier(parts, out var path));
        return path!;
    }

    [Fact]
    public void 一段限定字改讀成資料庫()
    {
        Assert.True(Qualifier("LibArchive").TryRealign(SqlQualifierSlot.Database, out var path));

        Assert.Equal("LibArchive", path.DatabaseName);
        Assert.Null(path.SchemaName);
        Assert.Null(path.ServerName);
        Assert.Equal(SqlQualifierSlot.Database, path.QualifierEnd);
    }

    [Fact]
    public void 一段限定字改讀成連結伺服器()
    {
        Assert.True(Qualifier("LibMirror").TryRealign(SqlQualifierSlot.Server, out var path));

        Assert.Equal("LibMirror", path.ServerName);
        Assert.Null(path.DatabaseName);
        Assert.Null(path.SchemaName);
        Assert.Equal(SqlQualifierSlot.Server, path.QualifierEnd);
    }

    /// <remarks>
    /// 兩段的情形只挪一格：最左邊那一段原本落在資料庫那一格，往左一格就是伺服器。
    /// 挪固定格數的話，這個寫法會被推出上限而整個認不得。
    /// </remarks>
    [Fact]
    public void 兩段限定字改讀成伺服器加資料庫()
    {
        Assert.True(Qualifier("LibMirror", "LibArchive").TryRealign(SqlQualifierSlot.Server, out var path));

        Assert.Equal("LibMirror", path.ServerName);
        Assert.Equal("LibArchive", path.DatabaseName);
        Assert.Null(path.SchemaName);
        Assert.Equal(SqlQualifierSlot.Database, path.QualifierEnd);
    }

    /// <remarks>
    /// 三段式的限定字最左邊本來就落在伺服器那一格，不必挪也不能挪。
    /// </remarks>
    [Fact]
    public void 已經對齊的限定字原樣回傳()
    {
        var original = Qualifier("LibMirror", "LibArchive", "dbo");

        Assert.True(original.TryRealign(SqlQualifierSlot.Server, out var path));
        Assert.Same(original, path);
    }

    /// <remarks>
    /// 挪過的不再挪第二次：重複套用會把已經正確的路徑推出上限，
    /// 而挪出去的那一段會安靜地消失。
    /// </remarks>
    [Fact]
    public void 挪過的限定字不再挪第二次()
    {
        Assert.True(Qualifier("LibMirror").TryRealign(SqlQualifierSlot.Server, out var once));
        Assert.False(once.TryRealign(SqlQualifierSlot.Server, out var twice));
        Assert.Same(once, twice);
    }

    /// <remarks>
    /// 三段的限定字已經用滿了四段式名稱的前三段，往左沒有位置了。
    /// </remarks>
    [Fact]
    public void 挪不動時維持原樣()
    {
        var original = Qualifier("LibMirror", "LibArchive", "dbo");

        Assert.False(original.TryRealign(SqlQualifierSlot.Database, out var path));
        Assert.Same(original, path);
    }

    /// <remarks>
    /// 挪到伺服器那一格之後，寫回文字時不能把右邊的點號補滿——
    /// <c>LibMirror...</c> 是「伺服器加預設資料庫加預設結構描述」，
    /// 不是使用者打的那三個字元。
    /// </remarks>
    [Fact]
    public void 挪過的限定字寫回文字時只寫到那一格()
    {
        Assert.True(Qualifier("LibMirror").TryRealign(SqlQualifierSlot.Server, out var server));
        Assert.Equal("LibMirror.", server.ToString());

        Assert.True(Qualifier("LibMirror", "LibArchive").TryRealign(SqlQualifierSlot.Server, out var database));
        Assert.Equal("LibMirror.LibArchive.", database.ToString());
    }

    /// <remarks>
    /// 空的中間段仍然佔一格：<c>LibArchive..</c> 的最左邊那一段落在資料庫那一格，
    /// 右對齊本來就猜對了。當成一段來挪的話會被推成伺服器，
    /// 於是清單改列一台不存在的伺服器的內容。
    /// </remarks>
    [Fact]
    public void 空的中間段仍然佔一格()
    {
        var original = Qualifier("LibArchive", string.Empty);

        Assert.Equal("LibArchive", original.LeftmostQualifier);
        Assert.True(original.TryRealign(SqlQualifierSlot.Database, out var path));
        Assert.Same(original, path);
        Assert.Equal(SqlQualifierSlot.Schema, path.QualifierEnd);
    }

    [Fact]
    public void 最左邊那一段是要拿去認的那一個()
    {
        Assert.Equal("LibMirror", Qualifier("LibMirror", "LibArchive", "dbo").LeftmostQualifier);
        Assert.Equal("dbo", Qualifier("dbo").LeftmostQualifier);
    }

    /// <remarks>
    /// 完整名稱沒有「還沒打完的那一格」，重新對齊對它沒有意義：
    /// <c>LibArchive.dbo.Loan</c> 的每一段都已經被使用者寫死了。
    /// </remarks>
    [Fact]
    public void 完整名稱不重新對齊()
    {
        Assert.True(SqlObjectPath.TryParseName(new[] { "LibArchive", "Loan" }, out var name));

        Assert.Null(name!.LeftmostQualifier);
        Assert.False(name.TryRealign(SqlQualifierSlot.Database, out var path));
        Assert.Same(name, path);
    }
}
