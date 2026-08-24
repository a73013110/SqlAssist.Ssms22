namespace SqlAssist.Core;

public sealed class SqlCompletionContext
{
    public SqlCompletionContext(
        bool isValid,
        int tokenStart,
        string prefix,
        CompletionTarget target,
        string? schemaQualifier = null,
        int targetKeywordStart = -1)
    {
        IsValid = isValid;
        TokenStart = tokenStart;
        Prefix = prefix;
        Target = target;
        SchemaQualifier = schemaQualifier;
        TargetKeywordStart = targetKeywordStart;
    }

    public bool IsValid { get; }

    public int TokenStart { get; }

    public string Prefix { get; }

    public CompletionTarget Target { get; }

    public string? SchemaQualifier { get; }

    /// <summary>
    /// 決定 <see cref="Target"/> 的關鍵字在原文中的起點，例如 <c>ALTER PROCEDURE</c> 的
    /// <c>ALTER</c>。<see cref="Target"/> 為 <see cref="CompletionTarget.Any"/> 時為 -1。
    /// 提交時要替換整個語句（而不只是游標前的字）就靠這個位置。
    /// </summary>
    public int TargetKeywordStart { get; }
}
