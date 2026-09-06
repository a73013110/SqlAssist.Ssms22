using System.Collections.Generic;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 把物件結構寫成一份文字輸出。
/// </summary>
/// <remarks>
/// 介面只收清單，不另開單一物件的多載：多物件才是難的那一邊（相依順序、共用的
/// 檔頭、敘述之間怎麼隔），而單一物件是它長度為一的特例。兩個方法各寫一份的話，
/// 只改到其中一份的症狀是「一次選一張表」與「一次選一張表加另一張」排版不同。
///
/// T-SQL 以外的輸出（資料字典、ERD、程式碼）都實作這個介面，
/// <see cref="Format"/> 讓呼叫端知道拿到的是哪一種文字，而不必自己記。
/// </remarks>
public interface ISqlScriptRenderer
{
    /// <summary>產出的格式代號，例如 <c>sql</c>、<c>markdown</c>。</summary>
    string Format { get; }

    string Render(IReadOnlyList<SqlObjectStructure> objects, SqlScriptContext context);
}
