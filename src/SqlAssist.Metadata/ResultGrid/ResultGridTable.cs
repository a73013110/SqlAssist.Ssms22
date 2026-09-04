using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 從結果格線取出來的一塊矩形資料：欄位、資料列，以及它是不是整份結果。
/// </summary>
/// <remarks>
/// 所有結果格線命令都吃這一個型別，格線的反射只在
/// <c>Ssms22/ResultGrid/</c> 那一層做一次。分成兩層是為了遵守分層護欄：
/// 「禁止把只看文字就能判斷的邏輯寫進 Ssms22」：產指令碼、去重欄名、判斷值轉不轉得出來
/// 全部是純邏輯，寫在這裡才跑得了單元測試。
///
/// <c>null</c> 就是 SQL 的 <c>NULL</c>。格線那一層必須用 <c>IsCellDataNull</c> 問過
/// 才填 <c>null</c>，不能靠字串比對——真正的 <c>NULL</c> 與內容剛好是 <c>NULL</c>
/// 這四個字的字串，字串化之後長得一模一樣。
/// </remarks>
public sealed class ResultGridTable
{
    private IReadOnlyList<string>? _scriptColumnNames;

    public ResultGridTable(
        IReadOnlyList<ResultGridColumn> columns,
        IReadOnlyList<object?[]> rows,
        bool isWholeResult)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        IsWholeResult = isWholeResult;
    }

    public IReadOnlyList<ResultGridColumn> Columns { get; }

    /// <summary>每一列一個陣列，長度等於 <see cref="Columns"/>；<c>null</c> 代表 SQL 的 <c>NULL</c>。</summary>
    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>這塊資料是不是整份結果（使用者沒有選取，或選滿了）。</summary>
    public bool IsWholeResult { get; }

    public bool IsEmpty => Columns.Count == 0 || Rows.Count == 0;

    /// <summary>
    /// 寫進指令碼的欄名：補上沒有名字的，並讓重複的名字各自唯一。
    /// </summary>
    /// <remarks>
    /// 兩種情形都是隨手查詢的常態，而不處理的話 <c>CREATE TABLE</c> 直接是語法錯誤：
    /// <c>SELECT COUNT(*) FROM Loan</c> 的那一欄沒有名字，
    /// <c>SELECT l.Id, c.Id FROM Loan l JOIN Copy c ...</c> 有兩個 <c>Id</c>。
    ///
    /// 去重用不分大小寫比對，因為 <c>CREATE TABLE</c> 的欄名唯一性照的是資料庫的
    /// 定序，而預設定序不分大小寫——只用序數比對的話 <c>Id</c> 與 <c>ID</c>
    /// 會通過這裡，然後在伺服器上失敗。
    /// </remarks>
    public IReadOnlyList<string> ScriptColumnNames =>
        _scriptColumnNames ??= BuildScriptColumnNames();

    private IReadOnlyList<string> BuildScriptColumnNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new string[Columns.Count];

        for (var index = 0; index < Columns.Count; index++)
        {
            var name = Columns[index].Name;

            if (name.Length == 0)
            {
                name = "Column" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var candidate = name;

            for (var suffix = 2; !used.Add(candidate); suffix++)
            {
                candidate = name + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            names[index] = candidate;
        }

        return names;
    }
}
