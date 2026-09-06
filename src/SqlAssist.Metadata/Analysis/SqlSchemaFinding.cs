using System;

namespace SqlAssist.Metadata.Analysis;

/// <summary>一則健檢發現的嚴重度。</summary>
/// <remarks>
/// 三級而不是兩級：把「建議改用 datetime2」與「這張表沒有主索引鍵」放在同一級，
/// 使用者看第三次就會把整個健檢關掉。
/// </remarks>
public enum SqlSchemaSeverity
{
    /// <summary>可以這樣，但有更好的寫法。</summary>
    Information,

    /// <summary>多半是沒想到，會在資料長大之後咬人。</summary>
    Warning,

    /// <summary>幾乎確定是問題。</summary>
    Error
}

/// <summary>
/// 一則健檢發現。
/// </summary>
/// <remarks>
/// 規則識別碼寫進輸出裡，使用者才關得掉單獨一條——而「關得掉」是這整個功能
/// 可以預設開啟的前提。訊息本身也要說得出「所以呢」：只寫
/// 「<c>Status</c> 沒有 CHECK」的話，看到的人不知道那是不是刻意的。
/// </remarks>
public sealed class SqlSchemaFinding
{
    public SqlSchemaFinding(
        string ruleId,
        SqlSchemaSeverity severity,
        string message,
        string? targetName = null)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            throw new ArgumentException("規則識別碼不可為空。", nameof(ruleId));
        }

        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("訊息不可為空。", nameof(message));
        }

        RuleId = ruleId;
        Severity = severity;
        Message = message;
        TargetName = targetName;
    }

    /// <summary>例如 <c>SCHEMA-001</c>。</summary>
    public string RuleId { get; }

    public SqlSchemaSeverity Severity { get; }

    public string Message { get; }

    /// <summary>這一則指的是哪一個資料行或索引；指整張表時為 null。</summary>
    public string? TargetName { get; }

    /// <summary>寫成指令碼註解的那一行（不含開頭的 <c>--</c>）。</summary>
    public string Describe() =>
        TargetName is null
            ? $"[{RuleId}][{Severity}] {Message}"
            : $"[{RuleId}][{Severity}] {TargetName}：{Message}";

    public override string ToString() => Describe();
}
