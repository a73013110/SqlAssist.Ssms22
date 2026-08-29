using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

public sealed class SqlCompletionContext
{
    private static readonly IReadOnlyList<SqlColumnSource> NoSources = Array.Empty<SqlColumnSource>();

    public SqlCompletionContext(
        bool isValid,
        int tokenStart,
        string prefix,
        CompletionTarget target,
        string? qualifier = null,
        int targetKeywordStart = -1,
        CompletionIntent intent = CompletionIntent.Reference,
        IReadOnlyList<SqlColumnSource>? columnSources = null,
        SqlKeywordPosition keywordPosition = SqlKeywordPosition.Any,
        IReadOnlyList<SqlColumnSource>? scopeSources = null)
    {
        IsValid = isValid;
        TokenStart = tokenStart;
        Prefix = prefix;
        Target = target;
        Qualifier = qualifier;
        TargetKeywordStart = targetKeywordStart;
        Intent = intent;
        ColumnSources = columnSources;
        KeywordPosition = keywordPosition;
        ScopeSources = scopeSources ?? NoSources;
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
    /// 因此由帶語句範圍的多載負責解析，解析成功時會填入 <see cref="ColumnSources"/>。
    /// </remarks>
    public string? Qualifier { get; }

    /// <summary>
    /// 限定字解析出的欄位來源；<see cref="Target"/> 為
    /// <see cref="CompletionTarget.Column"/> 時必定不為 null。
    /// </summary>
    /// <remarks>
    /// 是一串而不是一張資料表：<c>FROM (SELECT Id, * FROM T t) d</c> 之後的
    /// <c>d.</c> 同時來自寫死的名稱與一張資料表，只用一個
    /// <see cref="SqlTableReference"/> 表示不了。
    /// </remarks>
    public IReadOnlyList<SqlColumnSource>? ColumnSources { get; }

    /// <summary>
    /// 敘述在游標處看得到的所有欄位來源。
    /// </summary>
    /// <remarks>
    /// 沒有限定字的位置（<c>SELECT |</c>、<c>WHERE |</c>、<c>ON |</c>）要列出
    /// 敘述看得到的欄位，用的就是這一份。與 <see cref="ColumnSources"/> 同一次
    /// 詞法分析算出來：呼叫端再自己分析一次就是同一份文字掃兩遍，
    /// 而這條路徑在每一次按鍵上。
    /// </remarks>
    public IReadOnlyList<SqlColumnSource> ScopeSources { get; }

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

    /// <summary>複製這個上下文，補上敘述看得到的欄位來源。</summary>
    internal SqlCompletionContext WithScopeSources(IReadOnlyList<SqlColumnSource> sources)
    {
        return new SqlCompletionContext(
            IsValid,
            TokenStart,
            Prefix,
            Target,
            Qualifier,
            TargetKeywordStart,
            Intent,
            ColumnSources,
            KeywordPosition,
            sources);
    }

    /// <summary>複製這個上下文，改以欄位為建議目標。</summary>
    internal SqlCompletionContext AsColumnsOf(IReadOnlyList<SqlColumnSource> sources)
    {
        return new SqlCompletionContext(
            isValid: true,
            TokenStart,
            Prefix,
            CompletionTarget.Column,
            Qualifier,
            TargetKeywordStart,
            CompletionIntent.Reference,
            sources,
            KeywordPosition,
            ScopeSources);
    }
}
