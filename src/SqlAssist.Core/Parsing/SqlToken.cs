using System;

namespace SqlAssist.Core.Parsing;

/// <summary>一個 T-SQL 詞法單元。</summary>
public readonly struct SqlToken
{
    public SqlToken(SqlTokenKind kind, int start, int length, string text, string value, bool isQuoted)
    {
        Kind = kind;
        Start = start;
        Length = length;
        Text = text;
        Value = value;
        IsQuoted = isQuoted;
    }

    public SqlTokenKind Kind { get; }

    /// <summary>在原始文字中的起始位置。</summary>
    public int Start { get; }

    public int Length { get; }

    /// <summary>原始文字，含引號與跳脫字元。</summary>
    public string Text { get; }

    /// <summary>
    /// 語意值：識別字會去掉外層引號並還原跳脫字元，
    /// 因此 <c>[Weird]]Name]</c> 的值是 <c>Weird]Name</c>。
    /// </summary>
    public string Value { get; }

    /// <summary>識別字是否由方括號或雙引號包住。</summary>
    public bool IsQuoted { get; }

    public int End => Start + Length;

    /// <summary>
    /// 是否為指定的關鍵字。
    /// </summary>
    /// <remarks>
    /// 加引號的識別字一律不算關鍵字：<c>FROM [FROM]</c> 裡的 <c>[FROM]</c> 是資料表名稱。
    /// </remarks>
    public bool IsKeyword(string keyword)
    {
        return Kind == SqlTokenKind.Identifier
            && !IsQuoted
            && string.Equals(Value, keyword, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsPunctuation(string text)
    {
        return Kind == SqlTokenKind.Punctuation && string.Equals(Value, text, StringComparison.Ordinal);
    }

    public override string ToString() => $"{Kind}@{Start}:{Text}";
}
