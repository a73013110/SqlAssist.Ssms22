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
    public ResultGridColumn(
        string? name,
        string? serverDataType,
        int? maxLength = null,
        int? precision = null,
        int? scale = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name!.Trim();
        ServerDataType = string.IsNullOrWhiteSpace(serverDataType)
            ? string.Empty
            : serverDataType!.Trim();
        MaxLength = maxLength;
        Precision = precision;
        Scale = scale;
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

    /// <summary>
    /// 這一欄的最大長度：文字算字元、二進位算位元組；問不出來時是 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 三個長度與精確度都是<b>額外</b>的資訊，不是 <see cref="ServerDataType"/>
    /// 的一部分：結果格線回報的型別名稱不帶括號（<c>varchar</c> 而不是
    /// <c>varchar(20)</c>），而 T-SQL 對省略的長度有自己的預設值，
    /// 於是 <c>CREATE TABLE</c> 建得起來、<c>INSERT</c> 也跑得動，
    /// 只是資料被截斷。怎麼補回去見 <c>SqlTempTableColumnType</c>。
    ///
    /// <c>null</c> 是常態不是例外：<c>int</c> 這種型別本來就沒有長度可言，
    /// 而運算式欄位有時候整份結構描述都問不出來。
    /// </remarks>
    public int? MaxLength { get; }

    /// <summary>數值型別的總位數；問不出來或不適用時是 <c>null</c>。</summary>
    public int? Precision { get; }

    /// <summary>數值型別的小數位數；問不出來或不適用時是 <c>null</c>。</summary>
    public int? Scale { get; }

    /// <summary>去掉長度與精確度的小寫基底型別名。</summary>
    public string BaseTypeName => SqlTypeName.BaseOf(ServerDataType);
}
