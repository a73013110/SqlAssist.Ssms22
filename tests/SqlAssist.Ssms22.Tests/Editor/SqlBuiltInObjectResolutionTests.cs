using System.Threading.Tasks;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Editor;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Editor;

/// <summary>
/// 四條入口共用的「內建說明先答，還是先問物件」判斷。
/// </summary>
/// <remarks>
/// 只驗證順序本身，不依賴 JSON 內容：系統程序與語句的說明用測試自己建立的
/// <see cref="SqlBuiltInDoc"/>，物件解析用一個計數的假委派，兩者搭起來就能守住
/// 「系統程序命中時完全不呼叫物件解析」這種只有委派才驗證得到的事。
/// </remarks>
public sealed class SqlBuiltInObjectResolutionTests
{
    private static SqlBuiltInDoc Doc(SqlBuiltInKind kind, string name = "NAME") =>
        new(name, kind, "signature", "summary", examples: System.Array.Empty<SqlBuiltInExample>(), docsUrl: string.Empty);

    private static SqlObjectLocation Location(string name = "Lib_Reader")
    {
        var reference = new SqlIdentifierReference(name, path: null, start: 0, length: name.Length);
        var objectInfo = new SqlObjectInfo(1, "dbo", name, SqlObjectKind.Table);
        return new SqlObjectLocation(reference, objectInfo);
    }

    // ---- 同步版本（QuickInfo：物件解析只查快取） ----

    /// <summary>函式：物件解析先問，命中就用物件那一份，不理會內建說明。</summary>
    [Fact]
    public void 函式名稱先物件後說明()
    {
        var calls = 0;
        var location = Location();

        var result = SqlBuiltInObjectResolution.Resolve(Doc(SqlBuiltInKind.Function), () =>
        {
            calls++;
            return location;
        });

        Assert.Equal(1, calls);
        Assert.Same(location, result.Location);
        Assert.Null(result.BuiltIn);
    }

    /// <summary>函式：物件解析落空才退回內建說明。</summary>
    [Fact]
    public void 函式名稱物件解析落空才退回說明()
    {
        var doc = Doc(SqlBuiltInKind.Function);
        var calls = 0;

        var result = SqlBuiltInObjectResolution.Resolve(doc, () =>
        {
            calls++;
            return null;
        });

        Assert.Equal(1, calls);
        Assert.Null(result.Location);
        Assert.Same(doc, result.BuiltIn);
    }

    /// <summary>系統程序：說明優先，物件解析的委派完全不會被呼叫。</summary>
    [Fact]
    public void 系統程序說明優先時不呼叫物件解析()
    {
        var doc = Doc(SqlBuiltInKind.SystemProcedure);
        var calls = 0;

        var result = SqlBuiltInObjectResolution.Resolve(doc, () =>
        {
            calls++;
            return Location();
        });

        Assert.Equal(0, calls);
        Assert.Same(doc, result.BuiltIn);
        Assert.Null(result.Location);
    }

    /// <summary>語句：與系統程序同一條規則，一樣搶在物件解析之前。</summary>
    [Fact]
    public void 語句說明優先時不呼叫物件解析()
    {
        var doc = Doc(SqlBuiltInKind.Statement);
        var calls = 0;

        var result = SqlBuiltInObjectResolution.Resolve(doc, () =>
        {
            calls++;
            return Location();
        });

        Assert.Equal(0, calls);
        Assert.Same(doc, result.BuiltIn);
    }

    /// <summary>兩者都沒有時回傳 <see cref="SqlBuiltInOrObject.None"/>。</summary>
    [Fact]
    public void 都沒有命中時回傳None()
    {
        var result = SqlBuiltInObjectResolution.Resolve(null, () => null);

        Assert.Null(result.BuiltIn);
        Assert.Null(result.Location);
    }

    // ---- 非同步版本（Ctrl+F12／F12：物件解析等得起一次查詢） ----

    /// <summary>函式：非同步版本一樣是先物件後說明。</summary>
    [Fact]
    public async Task 非同步版本函式名稱先物件後說明()
    {
        var calls = 0;
        var location = Location();

        var result = await SqlBuiltInObjectResolution.ResolveAsync(Doc(SqlBuiltInKind.Function), () =>
        {
            calls++;
            return Task.FromResult<SqlObjectLocation?>(location);
        });

        Assert.Equal(1, calls);
        Assert.Same(location, result.Location);
    }

    /// <summary>非同步版本一樣不會為了系統程序等一次查詢。</summary>
    [Fact]
    public async Task 非同步版本系統程序說明優先時不呼叫物件解析()
    {
        var doc = Doc(SqlBuiltInKind.SystemProcedure);
        var calls = 0;

        var result = await SqlBuiltInObjectResolution.ResolveAsync(doc, () =>
        {
            calls++;
            return Task.FromResult<SqlObjectLocation?>(Location());
        });

        Assert.Equal(0, calls);
        Assert.Same(doc, result.BuiltIn);
    }
}
