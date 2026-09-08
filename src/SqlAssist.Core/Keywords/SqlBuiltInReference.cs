using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 一個內建名稱的某個引數查得到什麼值，例如 <c>CONVERT</c> 的 style 數值。
/// </summary>
/// <remarks>
/// 刻意是一張沒有語意的表格而不是每個函式一個型別：這種內容的形狀差太多
/// （style 是「編號、格式、輸出」，datepart 是「名稱、說明」），一個函式一個
/// 型別的話，每補一份對照表都要動 C# 與 WPF 兩層，而它們其實只是資料。
///
/// 上限四欄，因為資料格的欄要繫結到真的屬性——索引子路徑在複製與空欄收合
/// 那條路上讀不出來（見 <c>Ssms22/UI/SqlDataGridText</c>）。
/// 四欄放得下目前所有對照表，超過的時候該問的是「這張表是不是該拆成兩張」。
/// </remarks>
public sealed class SqlBuiltInReference
{
    /// <summary>一張對照表最多幾欄。</summary>
    public const int MaximumColumns = 4;

    public SqlBuiltInReference(
        string title,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
    }

    /// <summary>分頁標題，例如「style（日期時間）」。</summary>
    public string Title { get; }

    public IReadOnlyList<string> Columns { get; }

    /// <summary>每一列的儲存格；欄數不足的列由呼叫端補空字串。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
}
