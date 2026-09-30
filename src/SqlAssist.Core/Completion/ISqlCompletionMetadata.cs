using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 建議清單要問資料庫的那一份：物件、欄位、參數與執行個體名單。
/// </summary>
/// <remarks>
/// 分派本身只看文字（<see cref="SqlCompletionCandidates"/>），資料庫由這個介面注入：產品接查詢視窗
/// 那條連線，召回稽核接 <see cref="SqlCompletionMetadata.None"/> 或直連的資料庫。分派留在 Ssms22 的時候，
/// 稽核只好在測試裡抄一份「少了資料庫那一份」的複本，而複本與產品分岔也不會有任何測試失敗。
///
/// 每一支查不到都回空的，不擲例外；連不上、逾時與權限不足由實作降級。
/// </remarks>
public interface ISqlCompletionMetadata
{
    /// <summary>用這條連線的名單認出限定字最左邊那一段，回傳重新對齊過的上下文；認不出來原樣回傳。</summary>
    Task<SqlCompletionContext> ResolveQualifierAsync(SqlCompletionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// 限定字所指位置的物件、結構描述與資料庫；<paramref name="qualifierPath"/> 為 null 時是目前的資料庫，
    /// 那時連結伺服器也在裡面。
    /// </summary>
    Task<IReadOnlyList<SqlSuggestion>> GetObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken);

    /// <summary><c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 底下的系統物件。</summary>
    Task<IReadOnlyList<SqlSuggestion>> GetSystemObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken);

    /// <summary>一個資料表來源的欄位，允許查詢資料庫；插入文字不補限定字。</summary>
    Task<IReadOnlyList<SqlSuggestion>> GetColumnsAsync(
        SqlTableReference table,
        SqlAssistSettings settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// 一個資料表來源已經在快取裡的欄位，絕不觸發查詢；沒命中就是空的。
    /// </summary>
    /// <param name="qualifier">插入時補在欄位前面的別名或資料表名稱；不需要限定時為 null。</param>
    IReadOnlyList<SqlSuggestion> PeekColumns(SqlTableReference table, string? qualifier, SqlAssistSettings settings);

    /// <summary>把敘述裡各資料表來源的欄位載進快取，讓之後的 <see cref="PeekColumns"/> 命中。</summary>
    Task WarmColumnsAsync(IReadOnlyList<SqlColumnSource> sources, CancellationToken cancellationToken);

    /// <summary><c>EXEC</c> 正在呼叫的那個模組的參數。</summary>
    Task<IReadOnlyList<SqlSuggestion>> GetParametersAsync(SqlExecutedModule module, CancellationToken cancellationToken);

    /// <summary>一份執行個體名單（定序、語言、時區）與在用的那一個。</summary>
    Task<SqlInstanceListData> GetInstanceListAsync(SqlInstanceList list, CancellationToken cancellationToken);
}
