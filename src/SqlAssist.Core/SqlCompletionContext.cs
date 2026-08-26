using SqlAssist.Core.Parsing;

namespace SqlAssist.Core;

public sealed class SqlCompletionContext
{
    public SqlCompletionContext(
        bool isValid,
        int tokenStart,
        string prefix,
        CompletionTarget target,
        string? qualifier = null,
        int targetKeywordStart = -1,
        CompletionIntent intent = CompletionIntent.Reference,
        SqlTableReference? qualifiedTable = null,
        SqlKeywordPosition keywordPosition = SqlKeywordPosition.Any)
    {
        IsValid = isValid;
        TokenStart = tokenStart;
        Prefix = prefix;
        Target = target;
        Qualifier = qualifier;
        TargetKeywordStart = targetKeywordStart;
        Intent = intent;
        QualifiedTable = qualifiedTable;
        KeywordPosition = keywordPosition;
    }

    public bool IsValid { get; }

    public int TokenStart { get; }

    public string Prefix { get; }

    public CompletionTarget Target { get; }

    /// <summary>
    /// 點號前方的識別字。
    /// </summary>
    /// <remarks>
    /// 光靠語彙分析無法判斷它是結構描述、別名還是資料表名稱——<c>dbo.</c> 與 <c>u.</c>
    /// 在文字上長得一樣。要區分必須知道敘述看得到哪些資料來源，
    /// 因此由帶語句範圍的多載負責解析，解析成功時會填入 <see cref="QualifiedTable"/>。
    /// </remarks>
    public string? Qualifier { get; }

    /// <summary>
    /// 限定字解析出的資料來源；<see cref="Target"/> 為
    /// <see cref="CompletionTarget.Column"/> 時必定不為 null。
    /// </summary>
    public SqlTableReference? QualifiedTable { get; }

    /// <summary>
    /// 決定 <see cref="Target"/> 的關鍵字在原文中的起點，例如 <c>ALTER PROCEDURE</c> 的
    /// <c>ALTER</c>。<see cref="Target"/> 為 <see cref="CompletionTarget.Any"/> 時為 -1。
    /// 提交時要替換整個語句（而不只是游標前的字）就靠這個位置。
    /// </summary>
    public int TargetKeywordStart { get; }

    /// <summary>提交建議時應該做什麼。</summary>
    public CompletionIntent Intent { get; }

    /// <summary>
    /// 游標落在哪一個關鍵字位置。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Target"/> 是兩個不同的軸：<see cref="Target"/> 說的是
    /// 「該列哪一類資料庫物件」，這個說的是「該列哪些關鍵字」。
    /// <c>FROM |</c> 兩者都有話要說——物件只列資料表與檢視，關鍵字只列
    /// 能接在 FROM 後面的那幾個。
    /// </remarks>
    public SqlKeywordPosition KeywordPosition { get; }

    /// <summary>複製這個上下文，改以欄位為建議目標。</summary>
    internal SqlCompletionContext AsColumnsOf(SqlTableReference table)
    {
        return new SqlCompletionContext(
            isValid: true,
            TokenStart,
            Prefix,
            CompletionTarget.Column,
            Qualifier,
            TargetKeywordStart,
            CompletionIntent.Reference,
            table,
            KeywordPosition);
    }
}
