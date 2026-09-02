using System;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 結果格線裡的一欄：名稱，加上伺服器回報的型別。
/// </summary>
/// <remarks>
/// 型別是整組結果格線功能的關鍵，也是「不走剪貼簿」的全部理由。剪貼簿那份 TSV
/// 裡沒有型別，於是 <c>2024-01-15</c> 究竟是日期還是一段剛好長這樣的字串無從判斷，
/// 產出的 <c>INSERT</c> 會執行得動而資料是錯的。
///
/// 型別取不到時 <see cref="ServerDataType"/> 是空字串，而不是猜一個。
/// 猜出來的型別會讓 <c>CREATE TABLE</c> 產得出來卻對不上原本的資料，
/// 那比整段拒絕輸出糟——理由與 <c>SqlObjectStructure.CanBuildExecutableScript</c> 相同。
/// </remarks>
public sealed class ResultGridColumn
{
    public ResultGridColumn(string? name, string? serverDataType)
    {
        Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name!.Trim();
        ServerDataType = string.IsNullOrWhiteSpace(serverDataType)
            ? string.Empty
            : serverDataType!.Trim();
    }

    /// <summary>
    /// 欄名；查詢沒有替運算式取別名時是空字串。
    /// </summary>
    /// <remarks>
    /// 空字串是常態不是例外：<c>SELECT COUNT(*)</c> 這種欄在格線上顯示成
    /// 「(沒有資料行名稱)」，而那串字是 SSMS 的顯示文字，不是欄名。
    /// 補名字是產指令碼那一層的事（見 <see cref="ResultGridTable.ScriptColumnNames"/>），
    /// 這裡照實記錄。
    /// </remarks>
    public string Name { get; }

    /// <summary>伺服器回報的型別，例如 <c>nvarchar(50)</c>；取不到時是空字串。</summary>
    public string ServerDataType { get; }

    /// <summary>去掉長度與精確度的小寫基底型別名。</summary>
    public string BaseTypeName => SqlTypeName.BaseOf(ServerDataType);

    /// <summary>
    /// 建 <c>#temp</c> 用的型別。
    /// </summary>
    /// <remarks>
    /// 多數型別照抄就好，只有兩類不行，而且兩類都是「建得起來卻插不進去」：
    /// <c>timestamp</c>／<c>rowversion</c> 由引擎自己產生，明確插值會直接失敗；
    /// 型別名稱取不到時沒有東西可抄。前者換成 <c>varbinary(8)</c>——那是它實際的
    /// 儲存形狀，值原封不動搬得過去；後者回傳空字串，由呼叫端整段拒絕。
    /// </remarks>
    public string TempTableType()
    {
        switch (BaseTypeName)
        {
            case "":
                return string.Empty;

            case "timestamp":
            case "rowversion":
                return "varbinary(8)";

            default:
                return ServerDataType;
        }
    }
}
