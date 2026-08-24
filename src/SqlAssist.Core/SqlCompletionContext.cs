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
        SqlTableReference? qualifiedTable = null)
    {
        IsValid = isValid;
        TokenStart = tokenStart;
        Prefix = prefix;
        Target = target;
        Qualifier = qualifier;
        TargetKeywordStart = targetKeywordStart;
        Intent = intent;
        QualifiedTable = qualifiedTable;
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
            table);
    }
}
