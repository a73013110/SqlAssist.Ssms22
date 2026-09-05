using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 把指令碼裡讀出來的宣告換成中繼資料層的物件描述。
/// </summary>
/// <remarks>
/// 換過來的理由只有一個：<b>不要有第二份「哪些欄位插得進去」</b>。那條規則寫在
/// <see cref="SqlColumnInfo.CanInsert"/>，而 IDENTITY、計算資料行、
/// <c>rowversion</c> 與 <c>GENERATED ALWAYS</c> 漏掉任何一種的症狀都一樣——
/// 展開出來的 <c>INSERT</c> 一執行就錯。換過來之後，暫存資料表與資料表變數走的
/// 就是資料庫物件那一份展開，一個字都不必重寫。
///
/// 提交後的整句展開、滑鼠停留提示與浮動結構預覽共用這一份轉換。各接一條的話，
/// 漏掉的那一條沒有徵兆，只是那個表面安靜地什麼都不顯示——那正是提示與預覽
/// 原本的情形。
///
/// 沒有結構描述、沒有 object_id：這些名稱在 <c>sys.objects</c> 裡查不到，
/// 硬填一個只會讓紀錄檔說謊，見 <see cref="SqlObjectInfo"/>。種類則說得出來，
/// 由 <see cref="SqlObjectKinds.IsScriptDeclared"/> 那三種分辨，
/// 中繼資料因此不會為它們去查 <c>sys.columns</c>。
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
    ///
    /// 這也是結構預覽的指令碼分頁交出<b>原文</b>而不是重組一份 <c>CREATE TABLE</c>
    /// 的理由：重組出來的那一段會寫著 <c>DEFAULT (指令碼宣告)</c>，貼回編輯器執行不了。
    /// </remarks>
    private const string DefaultMarker = "(指令碼宣告)";

    /// <param name="script">
    /// 整份指令碼的原文；傳進來時會從中取出這份宣告，成為
    /// <see cref="SqlObjectDetail.Definition"/>。只要名稱與資料行的呼叫端傳 null。
    /// </param>
    public static SqlObjectDetail Create(SqlScriptTable table, string? script = null)
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

        // 名稱決定種類，而那條規則有第二個呼叫端（建議清單選到的項目），所以不寫在這裡。
        var kind = SqlScriptDeclarations.KindOf(table.Name);
        var isVariable = kind == SqlObjectKind.TableVariable;

        return new SqlObjectDetail(
            new SqlObjectInfo(0, string.Empty, table.Name, kind),
            columns,
            parameters: null,
            // RETURNS @rows TABLE (…) 認得的是「變數 TABLE (」這個形狀本身，原文因此
            // 不一定從 DECLARE 開始。補上關鍵字之後兩種寫法都是一句貼得上去的宣告。
            definition: Slice(script, table.Start, table.End, isVariable ? "DECLARE " : null));
    }

    /// <summary>
    /// 把一個 CTE 換成物件描述。
    /// </summary>
    /// <remarks>
    /// 欄位只有名稱：型別、NULL 與 PK 要追到最內層的資料表，而中間任何一段運算式
    /// 都會讓答案不成立，見 <c>docs/completion-columns.md</c>。
    /// </remarks>
    /// <param name="columnNames">
    /// 輸出欄位名稱，由 <see cref="SqlColumnSourceResolver"/> 攤平；讀不出來時是空的。
    /// </param>
    public static SqlObjectDetail Create(
        SqlCommonTableExpression commonTableExpression,
        IReadOnlyList<string> columnNames,
        string? script = null)
    {
        if (commonTableExpression is null)
        {
            throw new ArgumentNullException(nameof(commonTableExpression));
        }

        if (columnNames is null)
        {
            throw new ArgumentNullException(nameof(columnNames));
        }

        var columns = new List<SqlColumnInfo>(columnNames.Count);

        for (var index = 0; index < columnNames.Count; index++)
        {
            // 型別留空字串不是遺漏而是實話，與計算資料行同一個做法；
            // 可為 NULL 同樣不知道，而預設的 true 剛好不會標出任何徽章。
            columns.Add(new SqlColumnInfo(index + 1, columnNames[index], string.Empty, isNullable: true));
        }

        return new SqlObjectDetail(
            new SqlObjectInfo(
                0,
                string.Empty,
                commonTableExpression.Name,
                SqlObjectKind.CommonTableExpression),
            columns,
            parameters: null,
            definition: Slice(script, commonTableExpression.Start, commonTableExpression.End, "WITH "));
    }

    /// <summary>取出宣告的原文；位置對不上目前的文字時回傳 null。</summary>
    /// <remarks>
    /// 位置與文字來自同一次分析，對不上只會發生在呼叫端把兩者配錯的時候。
    /// 那時寧可讓指令碼分頁說「取不到宣告原文」，也不要交出一段截錯的文字。
    /// </remarks>
    private static string? Slice(string? script, int start, int end, string? prefix)
    {
        if (script is null || start < 0 || end > script.Length || end <= start)
        {
            return null;
        }

        return prefix + script.Substring(start, end - start);
    }
}
