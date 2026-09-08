using SqlAssist.Core.Keywords;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Preview;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Preview;

/// <summary>
/// 兩次對帳指的是不是同一個東西。
/// </summary>
/// <remarks>
/// 平台一次換選取會通知好幾輪，全靠這個判斷擋下重畫。放行太寬的症狀是換了目標畫面
/// 卻停在上一份內容；擋得太嚴則是使用者每按一次方向鍵眼前閃一下，而且剛送出的查詢
/// 會被取消再送一次。
/// </remarks>
public sealed class SqlPreviewSubjectTests
{
    private static SqlBuiltInDoc Doc(string name, SqlBuiltInKind kind) =>
        new(name, kind, string.Empty, "說明", string.Empty, string.Empty);

    private static SqlPreviewSubject Object(int objectId, string name) =>
        SqlPreviewSubject.ForObject(new SqlObjectInfo(objectId, "dbo", name, SqlObjectKind.Table));

    /// <summary>同一個編號就是同一個物件，即使是兩次分別建出來的描述。</summary>
    [Fact]
    public void 同一個編號視為同一次選取()
    {
        Assert.True(SqlPreviewSubject.IsSame(Object(42, "Loan"), Object(42, "Loan")));
        Assert.False(SqlPreviewSubject.IsSame(Object(42, "Loan"), Object(43, "Copy")));
    }

    /// <summary>
    /// 指令碼自己宣告的名稱靠名稱分辨。
    /// </summary>
    /// <remarks>
    /// 它們的編號一律是 0，只比編號會把 <c>#a</c> 與 <c>#b</c> 當成同一個。
    /// </remarks>
    [Fact]
    public void 沒有編號的宣告靠名稱分辨()
    {
        Assert.True(SqlPreviewSubject.IsSame(Object(0, "#Loan"), Object(0, "#loan")));
        Assert.False(SqlPreviewSubject.IsSame(Object(0, "#Loan"), Object(0, "#Copy")));
    }

    /// <summary>
    /// 同名不同種類的內建名稱不算同一個。
    /// </summary>
    /// <remarks>
    /// <c>CHAR</c> 在函式與型別兩份目錄裡各有一筆，只比名稱的症狀是從
    /// <c>CHAR(13)</c> 換到 <c>char(10)</c> 那一次畫面不換。
    /// </remarks>
    [Fact]
    public void 內建名稱連種類一起比()
    {
        var function = SqlPreviewSubject.ForBuiltIn(Doc("CHAR", SqlBuiltInKind.Function));
        var dataType = SqlPreviewSubject.ForBuiltIn(Doc("CHAR", SqlBuiltInKind.DataType));

        Assert.True(SqlPreviewSubject.IsSame(
            function,
            SqlPreviewSubject.ForBuiltIn(Doc("char", SqlBuiltInKind.Function))));
        Assert.False(SqlPreviewSubject.IsSame(function, dataType));
    }

    /// <summary>物件與內建說明畫的是兩種東西，任何情況下都不算同一個。</summary>
    [Fact]
    public void 物件與內建說明永遠不相等()
    {
        var doc = SqlPreviewSubject.ForBuiltIn(Doc("CONVERT", SqlBuiltInKind.Function));

        Assert.False(SqlPreviewSubject.IsSame(Object(42, "CONVERT"), doc));
        Assert.False(SqlPreviewSubject.IsSame(doc, Object(42, "CONVERT")));
    }

    /// <summary>
    /// 沒有主體與有主體不算同一次。
    /// </summary>
    /// <remarks>
    /// 兩邊都是 null 才算，否則展開狀態下從關鍵字換到資料表那一次不會重畫。
    /// </remarks>
    [Fact]
    public void 沒有主體只與沒有主體相等()
    {
        Assert.True(SqlPreviewSubject.IsSame(null, null));
        Assert.False(SqlPreviewSubject.IsSame(null, Object(42, "Loan")));
        Assert.False(SqlPreviewSubject.IsSame(Object(42, "Loan"), null));
    }
}
