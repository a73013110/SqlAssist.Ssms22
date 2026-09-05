using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>指令碼裡的一個 CTE：<c>WITH 名稱 [(資料行清單)] AS ( 主體 )</c>。</summary>
/// <remarks>
/// 位置以<b>詞法單元索引</b>表示而不是字元位置：讀主體的選取清單時本來就在
/// 詞法串流上工作，換算成字元位置只會多一次來回。
/// </remarks>
public sealed class SqlCommonTableExpression
{
    /// <param name="start">名稱在原始文字裡的位置。</param>
    /// <param name="end">主體右括號之後的字元位置。</param>
    public SqlCommonTableExpression(
        string name,
        IReadOnlyList<string> columnNames,
        int bodyStart,
        int bodyEnd,
        int start,
        int end)
    {
        Name = name;
        ColumnNames = columnNames;
        BodyStart = bodyStart;
        BodyEnd = bodyEnd;
        Start = start;
        End = end;
    }

    public string Name { get; }

    /// <summary>
    /// 明確寫出來的資料行清單，沒寫時是空的。
    /// </summary>
    /// <remarks>
    /// 有寫的話它就是這個 CTE 的輸出欄位名稱，主體裡的選取清單不必再看——
    /// 資料行清單本來就會覆寫主體算出來的名稱。
    /// </remarks>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>主體的第一個詞法單元索引（左括號之後）。</summary>
    public int BodyStart { get; }

    /// <summary>主體的結束索引（右括號的位置，不含）。</summary>
    public int BodyEnd { get; }

    /// <summary>
    /// <c>名稱 [(資料行清單)] AS ( 主體 )</c> 這一整段在原始文字裡的範圍，
    /// 不含前面的 <c>WITH</c> 與逗號。
    /// </summary>
    /// <remarks>
    /// 字元位置與上面兩個詞法單元索引並存，因為要的東西不同：讀選取清單在詞法
    /// 串流上工作，而結構預覽要交出去的是使用者眼前那一段<b>原文</b>。
    /// </remarks>
    public int Start { get; }

    public int End { get; }
}
