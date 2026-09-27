using System;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 一個名稱在使用者物件裡找不到時，該不該退回問系統物件，以及要問哪個結構描述。
/// </summary>
/// <remarks>
/// 只看文字，不查快照也不查資料庫：兩種情形——限定字本身就是系統結構描述
/// （<c>sys.sp_helpindex</c>、<c>INFORMATION_SCHEMA.TABLES</c>），或是未限定但長得像
/// 系統（擴充）預存程序（<c>sp_help</c>、<c>xp_cmdshell</c>）。使用者自己的同名物件
/// 永遠先找——這裡不知道、也不需要知道有沒有找到，呼叫端只在確認使用者物件裡沒有
/// 這個名稱之後才問這一支。
///
/// 系統預存程序在 <c>sys.all_objects</c> 裡一律掛在 <c>sys</c> 結構描述下（SQL Server
/// 把它們對映進每個資料庫的 <c>sys</c>），所以未限定名稱的退路固定問 <c>sys</c>，
/// 不是 <c>INFORMATION_SCHEMA</c>——那裡面只有檢視，見
/// <see cref="SqlAssist.Metadata.Caching.SqlMetadataCatalog"/> 載入系統物件那段查詢。
/// </remarks>
public static class SqlSystemObjectFallback
{
    /// <summary>未限定名稱退回系統物件時固定問的結構描述。</summary>
    private const string UnqualifiedFallbackSchema = "sys";

    /// <summary>
    /// 這個名稱該不該退回問系統物件；成立時 <paramref name="schemaName"/> 是要問的那個結構描述。
    /// </summary>
    public static bool TryGetFallbackSchema(string? qualifier, string? name, out string schemaName)
    {
        if (SqlSystemSchemas.IsSystem(qualifier))
        {
            schemaName = qualifier!;
            return true;
        }

        if (string.IsNullOrEmpty(qualifier) && LooksLikeSystemProcedure(name))
        {
            schemaName = UnqualifiedFallbackSchema;
            return true;
        }

        schemaName = string.Empty;
        return false;
    }

    /// <summary>名稱長得像系統（擴充）預存程序：<c>sp_</c> 或 <c>xp_</c> 開頭。</summary>
    /// <remarks>
    /// 只認前綴，不驗證後面接不接得成識別字——<see cref="SqlAssist.Core.Parsing.SqlIdentifierReference.Name"/>
    /// 本來就已經是一個合法的識別字本體。
    /// </remarks>
    private static bool LooksLikeSystemProcedure(string? name)
    {
        return name is { Length: > 3 } &&
            (name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase));
    }
}
