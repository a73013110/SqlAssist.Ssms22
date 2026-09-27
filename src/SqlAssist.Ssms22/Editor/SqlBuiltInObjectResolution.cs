using System;
using System.Threading.Tasks;
using SqlAssist.Core.Keywords;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 一次判斷的結果：內建說明贏了、物件解析贏了，或兩者都沒有。
/// </summary>
/// <remarks>
/// 兩個欄位互斥，<see cref="None"/> 兩者都是 null；呼叫端先看 <see cref="Location"/>，
/// 落空才看 <see cref="BuiltIn"/>，與四條入口原本各自寫的判斷順序一致。
/// </remarks>
internal readonly struct SqlBuiltInOrObject
{
    private SqlBuiltInOrObject(SqlBuiltInDoc? builtIn, SqlObjectLocation? location)
    {
        BuiltIn = builtIn;
        Location = location;
    }

    public SqlBuiltInDoc? BuiltIn { get; }

    public SqlObjectLocation? Location { get; }

    public static readonly SqlBuiltInOrObject None = new(null, null);

    public static SqlBuiltInOrObject ForBuiltIn(SqlBuiltInDoc doc) => new(doc, null);

    public static SqlBuiltInOrObject ForObject(SqlObjectLocation location) => new(null, location);
}

/// <summary>
/// 四條入口（<see cref="SqlObjectNavigation"/>、<c>SqlQuickInfoSource</c>、
/// <see cref="SqlAssist.Core.Parsing.SqlClickTarget"/>、
/// <see cref="SqlAssist.Ssms22.Completion.SqlSuggestionTarget"/>）共用的「內建說明先答，
/// 還是先問物件」判斷。
/// </summary>
/// <remarks>
/// 呼叫端先用 <see cref="SqlBuiltInDocCatalog.TryGetAt"/> 找出這個名稱對到的說明（沒有就
/// 傳 null），這裡只負責照 <see cref="SqlBuiltInKinds.PrecedesObjectResolution"/> 那條單一
/// 規則決定順序：系統程序與語句優先，命中就跳過物件解析；函式與型別維持物件解析優先，
/// 只有物件解析落空才退回內建說明——<c>SELECT * FROM Format</c> 停在 <c>Format</c> 上要的
/// 是那張表，不是同名的內建函式。
///
/// 物件解析交給呼叫端的委派：<see cref="Resolve"/> 給同步版本（QuickInfo 只查快取，
/// <see cref="SqlObjectLocator.LocateCached"/> 不等連線），<see cref="ResolveAsync"/> 給
/// 非同步版本（Ctrl+F12／F12 等得起一次查詢，<see cref="SqlObjectLocator.LocateAsync"/>）。
/// 系統程序與語句命中時委派完全不會被呼叫——這正是設計要守的地方：說明不必等連線
/// 就答得出來。
/// </remarks>
internal static class SqlBuiltInObjectResolution
{
    public static SqlBuiltInOrObject Resolve(SqlBuiltInDoc? builtIn, Func<SqlObjectLocation?> resolveObject)
    {
        if (builtIn is not null && builtIn.Kind.PrecedesObjectResolution())
        {
            return SqlBuiltInOrObject.ForBuiltIn(builtIn);
        }

        if (resolveObject() is { } location)
        {
            return SqlBuiltInOrObject.ForObject(location);
        }

        return builtIn is not null ? SqlBuiltInOrObject.ForBuiltIn(builtIn) : SqlBuiltInOrObject.None;
    }

    public static async Task<SqlBuiltInOrObject> ResolveAsync(
        SqlBuiltInDoc? builtIn,
        Func<Task<SqlObjectLocation?>> resolveObject)
    {
        if (builtIn is not null && builtIn.Kind.PrecedesObjectResolution())
        {
            return SqlBuiltInOrObject.ForBuiltIn(builtIn);
        }

        if (await resolveObject().ConfigureAwait(true) is { } location)
        {
            return SqlBuiltInOrObject.ForObject(location);
        }

        return builtIn is not null ? SqlBuiltInOrObject.ForBuiltIn(builtIn) : SqlBuiltInOrObject.None;
    }
}
