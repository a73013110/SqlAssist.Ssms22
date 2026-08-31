using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

public sealed class SqlCompletionContext
{
    private static readonly IReadOnlyList<SqlColumnSource> NoSources = Array.Empty<SqlColumnSource>();

    private static readonly IReadOnlyList<SqlSuggestion> NoScriptSources = Array.Empty<SqlSuggestion>();

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
        IReadOnlyList<SqlColumnSource>? scopeSources = null,
        IReadOnlyList<SqlSuggestion>? scriptSources = null,
        SqlExecutedModule? executedModule = null)
    {
        ScriptSources = scriptSources ?? NoScriptSources;
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
        ExecutedModule = executedModule;
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
    /// 這份指令碼自己宣告的名稱：CTE、暫存資料表與變數。
    /// </summary>
    /// <remarks>
    /// 共同點是中繼資料一個都看不到，而且是使用者上面幾行才寫下的。
    /// 哪一種放進來由位置決定：資料來源位置（<c>FROM </c>、<c>JOIN </c>…）
    /// 而且沒有限定字時是 CTE 與暫存資料表，<c>@</c> 之後是變數。
    /// 其餘位置留空是刻意的：掃描不必要的話就不掃。
    /// </remarks>
    public IReadOnlyList<SqlSuggestion> ScriptSources { get; }

    /// <summary>
    /// <c>EXEC</c> 正在呼叫的模組；只有 <see cref="Target"/> 是
    /// <see cref="CompletionTarget.Variable"/> 而且游標落在它的引數清單裡時才有值。
    /// </summary>
    /// <remarks>
    /// 參數名稱只在中繼資料裡，Core 讀不到資料庫——這裡只回答「他在呼叫誰」，
    /// 換成參數清單是 SSMS 那一層的事。
    /// </remarks>
    public SqlExecutedModule? ExecutedModule { get; }

    /// <summary>
    /// 這個位置要不要 <c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 底下的系統物件。
    /// </summary>
    /// <remarks>
    /// 那一份光是一個使用者資料庫底下就有一兩千筆，而且只在兩個位置有意義：
    /// 使用者自己打出了 <c>sys.</c>／<c>INFORMATION_SCHEMA.</c>，
    /// 或者他在 <c>EXEC </c> 之後——<c>sp_executesql</c>、<c>sp_help</c> 一律不加
    /// 結構描述就呼叫。
    ///
    /// <c>ALTER PROCEDURE </c> 不算：那裡目標同樣是預存程序，但系統程序改不動，
    /// 列出來只會讓使用者選到一個改不了的東西——與內建函式不進
    /// <c>ALTER FUNCTION</c> 是同一條理由。分辨兩者的正是
    /// <see cref="Intent"/>，所以這裡認的是 <see cref="CompletionIntent.ExecuteCall"/>
    /// 而不是「不是 ALTER」——往後再多一種提交行為時，這一條不必跟著改。
    ///
    /// 判斷放在這裡而不是取得清單的那一層：它只跟上下文有關，可以完整單元測試。
    /// </remarks>
    public bool WantsSystemObjects =>
        (Target == CompletionTarget.Procedure && Intent == CompletionIntent.ExecuteCall) ||
        string.Equals(Qualifier, "sys", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Qualifier, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);

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
            sources,
            ScriptSources,
            ExecutedModule);
    }

    /// <summary>複製這個上下文，補上指令碼自己宣告的資料來源。</summary>
    internal SqlCompletionContext WithScriptSources(IReadOnlyList<SqlSuggestion> sources)
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
            ScopeSources,
            sources,
            ExecutedModule);
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
            ScopeSources,
            ScriptSources,
            ExecutedModule);
    }
}
