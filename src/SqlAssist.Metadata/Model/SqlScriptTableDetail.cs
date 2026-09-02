using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 把指令碼裡讀出來的資料表宣告換成中繼資料層的物件描述。
/// </summary>
/// <remarks>
/// 換過來的理由只有一個：<b>不要有第二份「哪些欄位插得進去」</b>。那條規則寫在
/// <see cref="SqlColumnInfo.CanInsert"/>，而 IDENTITY、計算資料行、
/// <c>rowversion</c> 與 <c>GENERATED ALWAYS</c> 漏掉任何一種的症狀都一樣——
/// 展開出來的 <c>INSERT</c> 一執行就錯。換過來之後，暫存資料表與資料表變數走的
/// 就是資料庫物件那一份展開，一個字都不必重寫。
///
/// 沒有結構描述、沒有 object_id：這兩種名稱在 <c>sys.objects</c> 裡查不到，
/// 硬填一個只會讓紀錄檔說謊，見 <see cref="SqlObjectInfo"/>。
/// </remarks>
public static class SqlScriptTableDetail
{
    /// <summary>
    /// 指令碼讀得出的預設值只有「有沒有」，讀不出寫的是什麼。
    /// </summary>
    /// <remarks>
    /// 展開 <c>INSERT</c> 只問這一件事（有 DEFAULT 就填 <c>DEFAULT</c>），
    /// 而把整段預設值運算式拼回字串是另一件事，這裡沒有人要。
    /// 留一個看得懂的字，勝過留一個空字串讓下一個人以為是漏掉的。
    /// </remarks>
    private const string DefaultMarker = "(指令碼宣告)";

    public static SqlObjectDetail Create(SqlScriptTable table)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        var columns = new List<SqlColumnInfo>(table.Columns.Count);

        for (var index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];

            columns.Add(new SqlColumnInfo(
                index + 1,
                column.Name,
                column.DataType,
                column.IsNullable,
                isIdentity: column.IsIdentity,
                isComputed: column.IsComputed,
                isPrimaryKey: column.IsPrimaryKey,
                defaultDefinition: column.HasDefault ? DefaultMarker : null));
        }

        return new SqlObjectDetail(
            new SqlObjectInfo(0, string.Empty, table.Name, SqlObjectKind.Table),
            columns);
    }
}
