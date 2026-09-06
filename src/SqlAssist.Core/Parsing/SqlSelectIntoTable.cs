using System;

namespace SqlAssist.Core.Parsing;

/// <summary><c>SELECT … INTO #tmp</c> 建立的暫存資料表。</summary>
/// <remarks>
/// 這是<b>名冊</b>裡的樣子，不是對外的樣子：形狀與 <see cref="SqlCommonTableExpression"/>
/// 同一種——名稱加上一段查詢——因為資料行要由 <see cref="SqlColumnSourceResolver"/>
/// 把選取清單遞迴攤平才算得出來，兩者走同一份遞迴。收集階段只記位置，不攤平。
///
/// 對外則與 <c>CREATE TABLE #tmp (…)</c> 合成同一個 <see cref="SqlScriptTable"/>，
/// 見 <see cref="SqlColumnSourceResolver.FindScriptTable"/>：下游問的都是同一件事
/// ——這個名稱有哪些資料行——差別只在型別讀不讀得出來。
/// </remarks>
public sealed class SqlSelectIntoTable
{
    /// <param name="bodyStart"><c>SELECT</c> 的詞法單元索引。</param>
    /// <param name="bodyEnd">這句敘述的結束索引（不含）。</param>
    /// <param name="start">敘述在原始文字裡的起點。</param>
    /// <param name="end">敘述在原始文字裡的終點。</param>
    public SqlSelectIntoTable(string name, int bodyStart, int bodyEnd, int start, int end)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料表名稱不可為空。", nameof(name));
        }

        Name = name;
        BodyStart = bodyStart;
        BodyEnd = bodyEnd;
        Start = start;
        End = end;
    }

    /// <summary>寫在 <c>INTO</c> 之後的名稱，含開頭的井號。</summary>
    public string Name { get; }

    public int BodyStart { get; }

    public int BodyEnd { get; }

    /// <summary>
    /// 整句 <c>SELECT … INTO …</c> 在原始文字裡的範圍。
    /// </summary>
    /// <remarks>
    /// 結構預覽的指令碼分頁交出的就是這一段<b>原文</b>：它本身已經是一句可以執行的
    /// 敘述，不必也不該由讀出來的資料行重組一份 <c>CREATE TABLE</c>——那一份猜不出
    /// 型別，貼回編輯器執行不了。
    /// </remarks>
    public int Start { get; }

    public int End { get; }

    public override string ToString() => $"{Name}（SELECT … INTO）";
}
