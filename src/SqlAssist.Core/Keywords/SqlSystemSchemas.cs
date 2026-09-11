using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 每一個資料庫裡都存在的兩個系統結構描述。
/// </summary>
/// <remarks>
/// 這兩個名字是產品事實而不是誰的 schema，因此不必等中繼資料；而問它們的位置有三個：
/// 建議清單要拿它們補「打 <c>sys</c> 再按 Tab」那一步、上下文要拿它們判斷該不該把
/// 系統物件拉進來、中繼資料層要拿它們判斷一個限定名稱該不該去問那份分開載入的清單。
/// 三處各寫一份字串的症狀是其中一處漏掉 <c>INFORMATION_SCHEMA</c>，而漏掉的那一處
/// 只是安靜地少一份答案。
/// </remarks>
public static class SqlSystemSchemas
{
    /// <summary>系統結構描述的名稱。</summary>
    public static readonly IReadOnlyList<string> Names = new[] { "sys", "INFORMATION_SCHEMA" };

    /// <summary>這個結構描述名稱是不是系統結構描述；大小寫不敏感，空值一律不是。</summary>
    public static bool IsSystem(string? schemaName)
    {
        if (string.IsNullOrEmpty(schemaName))
        {
            return false;
        }

        for (var index = 0; index < Names.Count; index++)
        {
            if (string.Equals(Names[index], schemaName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
