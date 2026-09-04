using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Statements;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 把已插入的模組名稱換成可直接執行的完整 ALTER 語句。
/// </summary>
/// <remarks>
/// 使用者輸入 <c>ap</c> 展開成 <c>ALTER PROCEDURE</c> 之後選了某個程序，想要的是
/// 可以立刻修改並執行的完整定義，而不是只把名稱補上去。
/// </remarks>
internal sealed class SqlAlterStatementExpansion : ISqlCommitExpansion
{
    public SqlAlterStatementExpansion(SqlObjectInfo objectInfo)
    {
        Object = objectInfo;
    }

    public SqlCommitExpansionScope Scope => SqlCommitExpansionScope.Statement;

    public SqlObjectInfo Object { get; }

    /// <summary>定義只有中繼資料層拿得到，這裡永遠要查。</summary>
    public SqlObjectDetail? KnownDetail => null;

    public string OperationName => "ALTER 語句";

    public string LeadingKeyword => "ALTER";

    public TextReplacement? Build(SqlObjectDetail detail, SqlStatementSite site, string insertedName)
    {
        if (detail.Definition is not { } definition)
        {
            SqlAssistDiagnostics.WriteAlways(
                $"無法取得 {Object.QualifiedName} 的定義，維持只插入名稱");
            return null;
        }

        if (!SqlModuleScript.TryConvertCreateToAlter(definition, out var script))
        {
            SqlAssistDiagnostics.WriteAlways(
                $"{Object.QualifiedName} 的定義不是 CREATE 開頭，維持只插入名稱");
            return null;
        }

        return new TextReplacement(
            script,
            SqlAssistActivityKind.AlterExpanded,
            $"已展開 {Object.QualifiedName} 的完整 ALTER 語句",
            SqlModuleScript.FindHeaderNameEnd(script));
    }
}

/// <summary>
/// 把已插入的資料表名稱換成完整的 <c>INSERT</c> 骨架。
/// </summary>
/// <remarks>
/// 插不進去的欄位一個都不能留（見 <see cref="SqlColumnInfo.CanInsert"/>）；漏掉一種
/// 的症狀不是少幾個欄位，而是整句一執行就錯。
///
/// 反過來，欄位一個都撈不到時<b>整個放棄</b>，維持只插入名稱：同義字在
/// <c>sys.columns</c> 裡沒有列，撈到空清單就組出 <c>INSERT INTO syn () VALUES ()</c>
/// ——那比什麼都不做糟糕得多。這與展開 <c>SELECT *</c> 不做部分展開是同一條理由。
///
/// 暫存資料表與資料表變數走的是同一個類別，只是細節由呼叫端先讀好交進來
/// （建構函式的 <c>knownDetail</c>）：它們的資料行寫在指令碼裡而不在中繼資料裡，
/// 但「哪些插得進去」與「排版長什麼樣」與資料庫物件一模一樣。
/// </remarks>
internal sealed class SqlInsertStatementExpansion : ISqlCommitExpansion
{
    private readonly SqlAssistSettings _settings;

    /// <param name="knownDetail">
    /// 提交當下就讀好的細節；資料庫物件傳 null，由中繼資料層去查。
    /// </param>
    public SqlInsertStatementExpansion(
        SqlObjectInfo objectInfo,
        SqlAssistSettings settings,
        SqlObjectDetail? knownDetail = null)
    {
        Object = objectInfo;
        _settings = settings;
        KnownDetail = knownDetail;
    }

    public SqlObjectInfo Object { get; }

    public SqlCommitExpansionScope Scope => SqlCommitExpansionScope.Statement;

    public SqlObjectDetail? KnownDetail { get; }

    public string OperationName => "INSERT 語句";

    public string LeadingKeyword => "INSERT";

    public TextReplacement? Build(SqlObjectDetail detail, SqlStatementSite site, string insertedName)
    {
        var columns = new List<SqlStatementColumn>(detail.Columns.Count);

        foreach (var column in detail.Columns)
        {
            if (!column.CanInsert)
            {
                continue;
            }

            columns.Add(new SqlStatementColumn(
                SqlInsertionText.Quote(column.Name, _settings),
                column.DataType,
                column.IsNullable,
                !string.IsNullOrEmpty(column.DefaultDefinition)));
        }

        if (columns.Count == 0)
        {
            SqlAssistDiagnostics.WriteAlways(
                $"{Object.QualifiedName} 沒有插得進去的欄位，維持只插入名稱");
            return null;
        }

        var text = SqlInsertStatementText.Build(
            insertedName,
            columns,
            site.Indent,
            site.NewLine,
            out var caretOffset);

        return new TextReplacement(
            text,
            SqlAssistActivityKind.InsertExpanded,
            $"已展開 {Object.QualifiedName} 的 {columns.Count} 個欄位與 VALUES",
            caretOffset,
            columns.Count);
    }
}

/// <summary>
/// 把已插入的資料表名稱換成一整句 <c>MERGE</c> 骨架。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlInsertStatementExpansion"/> 共用同一條「一個欄位都撈不到就整個
/// 放棄」的規則：組出一句沒有欄位的 MERGE 比什麼都不做糟糕得多。
///
/// 比對鍵取主索引鍵，而且<b>不</b>過濾 <c>CanInsert</c>——IDENTITY 的主索引鍵插不
/// 進去，但它正是最該拿來比對的那一欄。排版與「沒有主索引鍵時留佔位字」的理由見
/// <see cref="SqlMergeStatementText"/>。
/// </remarks>
internal sealed class SqlMergeStatementExpansion : ISqlCommitExpansion
{
    private readonly SqlAssistSettings _settings;

    /// <param name="knownDetail">
    /// 提交當下就讀好的細節；資料庫物件傳 null，由中繼資料層去查。
    /// 與 <see cref="SqlInsertStatementExpansion"/> 同一條理由。
    /// </param>
    public SqlMergeStatementExpansion(
        SqlObjectInfo objectInfo,
        SqlAssistSettings settings,
        SqlObjectDetail? knownDetail = null)
    {
        Object = objectInfo;
        _settings = settings;
        KnownDetail = knownDetail;
    }

    public SqlObjectInfo Object { get; }

    public SqlCommitExpansionScope Scope => SqlCommitExpansionScope.Statement;

    public SqlObjectDetail? KnownDetail { get; }

    public string OperationName => "MERGE 語句";

    public string LeadingKeyword => "MERGE";

    public TextReplacement? Build(SqlObjectDetail detail, SqlStatementSite site, string insertedName)
    {
        var keys = new List<string>();
        var columns = new List<string>(detail.Columns.Count);

        foreach (var column in detail.Columns)
        {
            if (column.IsPrimaryKey)
            {
                keys.Add(SqlInsertionText.Quote(column.Name, _settings));
            }

            if (column.CanInsert)
            {
                columns.Add(SqlInsertionText.Quote(column.Name, _settings));
            }
        }

        if (columns.Count == 0)
        {
            SqlAssistDiagnostics.WriteAlways(
                $"{Object.QualifiedName} 沒有插得進去的欄位，維持只插入名稱");
            return null;
        }

        var text = SqlMergeStatementText.Build(
            insertedName,
            keys,
            columns,
            site.Indent,
            site.NewLine,
            out var caretOffset);

        var keyNote = keys.Count > 0
            ? $"{keys.Count} 個主索引鍵欄位"
            : "沒有主索引鍵，比對鍵留了佔位字";

        return new TextReplacement(
            text,
            SqlAssistActivityKind.MergeExpanded,
            $"已展開 {Object.QualifiedName} 的 {columns.Count} 個欄位（{keyNote}）",
            caretOffset,
            columns.Count);
    }
}

/// <summary>
/// 把已插入的模組名稱換成一整句具名傳值的 <c>EXEC</c>。
/// </summary>
/// <remarks>
/// 「哪些參數可以省略」只能從模組定義讀出來（見
/// <see cref="SqlModuleParameterDefaults"/>），而定義與參數在同一次
/// <c>GetDetailAsync</c> 就一起回來了，因此不多付一次往返。
/// </remarks>
internal sealed class SqlProcedureCallExpansion : ISqlCommitExpansion
{
    private readonly SqlAssistSettings _settings;

    public SqlProcedureCallExpansion(SqlObjectInfo objectInfo, SqlAssistSettings settings)
    {
        Object = objectInfo;
        _settings = settings;
    }

    public SqlObjectInfo Object { get; }

    public SqlCommitExpansionScope Scope => SqlCommitExpansionScope.Statement;

    /// <summary>參數與定義只有中繼資料層拿得到，這裡永遠要查。</summary>
    public SqlObjectDetail? KnownDetail => null;

    public string OperationName => "EXEC 語句";

    public string LeadingKeyword => "EXEC";

    public TextReplacement? Build(SqlObjectDetail detail, SqlStatementSite site, string insertedName)
    {
        var optional = SqlModuleParameterDefaults.Find(detail.Definition);
        var parameters = new List<SqlStatementParameter>(detail.Parameters.Count);

        foreach (var parameter in detail.Parameters)
        {
            // parameter_id 0 是純量函式的傳回值，不是呼叫時傳得進去的東西。
            if (parameter.Ordinal <= 0)
            {
                continue;
            }

            var isOptional = optional.Contains(parameter.Name);

            // 省略選擇性參數是合法的呼叫方式，不是少展開一半。定義讀不到時
            // optional 是空的，於是每個參數都算必填——寧可展開得多，也不要因為
            // 讀不到定義就把該填的參數吞掉，那一句貼上去才是真的執行不了。
            if (isOptional && !_settings.IncludeOptionalParameters)
            {
                continue;
            }

            parameters.Add(new SqlStatementParameter(
                parameter.Name,
                parameter.DataType,
                parameter.IsOutput,
                isOptional));
        }

        // 沒有參數的程序展開起來與只插入名稱完全一樣，那就不必動它——
        // 擴充預存程序（sp_executesql 的鄰居）在 sys.parameters 裡也沒有列，
        // 同樣落在這一條。整支程序的參數都有預設值而使用者又選擇不展開它們時，
        // 篩完也是一個不剩，走的是同一條路：EXEC 加上名稱本身就是完整的呼叫。
        if (parameters.Count == 0)
        {
            SqlAssistDiagnostics.Write(
                $"{Object.QualifiedName} 沒有要展開的參數，維持只插入名稱");
            return null;
        }

        var text = SqlProcedureCallText.Build(
            ExecuteKeyword(site.StatementText),
            insertedName,
            parameters,
            site.Indent,
            site.NewLine,
            out var caretOffset);

        return new TextReplacement(
            text,
            SqlAssistActivityKind.ExecuteExpanded,
            $"已展開 {Object.QualifiedName} 的 {parameters.Count} 個參數",
            caretOffset,
            parameters.Count);
    }

    /// <summary>
    /// 使用者原本寫的是 <c>EXEC</c> 還是 <c>EXECUTE</c>，照原文帶回去。
    /// </summary>
    /// <remarks>
    /// 統一改寫成 <c>EXEC</c> 也是合法的 T-SQL，但那是他沒有要求的改動——
    /// 與展開萬用字元時保留使用者自己寫的限定字（<c>dbo.PUBLISHER.*</c>）是同一條。
    /// 大小寫同樣不動：關鍵字要不要大寫由另一個功能決定。
    /// </remarks>
    private static string ExecuteKeyword(string statementText)
    {
        var text = statementText.TrimStart();
        var length = 0;

        while (length < text.Length && char.IsLetter(text[length]))
        {
            length++;
        }

        return length == 0 ? "EXEC" : text.Substring(0, length);
    }

}

/// <summary>
/// 在剛插入的函式名稱後面補上一整組引數。
/// </summary>
/// <remarks>
/// 與上面四種的差別只有一個：換掉的不是整句，而是<b>剛插入的那個名稱</b>
/// （<see cref="SqlCommitExpansionScope.InsertedName"/>）。函式出現在哪個子句
/// 由使用者決定，那些位置大多沒有「決定目標的關鍵字」可以當整句的起點。
///
/// 括號不是體貼而是必要：<c>SELECT dbo.fn_DueDate</c> 是語法錯誤，
/// 沒有參數的函式也一樣要寫 <c>()</c>。引數的值一律是預留位置，
/// 挑選規則與 EXEC 骨架共用 <see cref="SqlLiteralDefaults"/>——各寫一份的下場是
/// 其中一份給日期填了空字串，而那會安靜地存進 1900-01-01。
///
/// 參數一個都撈不到時<b>照樣</b>補上一對空括號，這與 INSERT 骨架「欄位撈不到就整個
/// 放棄」相反，因為兩者的失敗長得不一樣：沒有欄位的 <c>INSERT</c> 是一句跑得動卻
/// 錯的話，而沒有參數的函式呼叫本來就寫成 <c>()</c>——那是正確答案，不是半成品。
/// </remarks>
internal sealed class SqlFunctionCallExpansion : ISqlCommitExpansion
{
    private readonly string _insertedName;

    /// <param name="insertedName">
    /// 提交之後緩衝區裡站著的那個完整名稱，含使用者自己打的限定字。
    /// 等待期間的原文比對要用它，見 <see cref="LeadingKeyword"/>——
    /// 要被換掉的範圍同樣從限定字起算，兩邊指的必須是同一段。
    /// </param>
    public SqlFunctionCallExpansion(SqlObjectInfo objectInfo, string insertedName)
    {
        Object = objectInfo;
        _insertedName = insertedName;
    }

    public SqlCommitExpansionScope Scope => SqlCommitExpansionScope.InsertedName;

    public SqlObjectInfo Object { get; }

    /// <summary>參數只有中繼資料層拿得到，這裡永遠要查。</summary>
    public SqlObjectDetail? KnownDetail => null;

    public string OperationName => "函式引數";

    /// <summary>
    /// 這一段必須仍是剛插入的那個名稱。
    /// </summary>
    /// <remarks>
    /// 整句展開比的是 <c>ALTER</c>、<c>EXEC</c> 這種關鍵字，這裡沒有關鍵字可比——
    /// 範圍本來就只有名稱。等待期間使用者若把它刪掉或改成別的字，比對就不成立，
    /// 括號因此不會補到別人的名稱上。
    /// </remarks>
    public string LeadingKeyword => _insertedName;

    public TextReplacement? Build(SqlObjectDetail detail, SqlStatementSite site, string insertedName)
    {
        // 等待期間使用者已經自己打了左括號：再補一組就變成 dbo.fn_DueDate(NULL)(。
        // 這一關其他四種展開不需要——它們換掉的是整句，而整句展開的下一個字元
        // 是什麼並不會讓結果重複。
        if (site.NextCharacter == '(')
        {
            SqlAssistDiagnostics.Write(
                $"{Object.QualifiedName} 後面已經有左括號，這一次不補引數");
            return null;
        }

        var arguments = new List<SqlStatementParameter>(detail.Parameters.Count);

        foreach (var parameter in detail.Parameters)
        {
            // parameter_id 0 是函式的傳回值，不是呼叫時傳得進去的東西。
            if (parameter.Ordinal <= 0)
            {
                continue;
            }

            // 函式只收位置引數，沒有「這一個可以省略」這回事：定義裡寫了預設值的
            // 參數，呼叫時要嘛給值、要嘛寫 DEFAULT，位置照留。因此這裡不必像
            // EXEC 那樣去讀定義找預設值，也就少一次剖析。
            arguments.Add(new SqlStatementParameter(
                parameter.Name,
                parameter.DataType,
                parameter.IsOutput,
                isOptional: false));
        }

        var text = SqlFunctionCallText.Build(insertedName, arguments, out var caretOffset);

        return new TextReplacement(
            text,
            SqlAssistActivityKind.FunctionCallExpanded,
            arguments.Count == 0
                ? $"已補上 {Object.QualifiedName} 的空括號（沒有參數）"
                : $"已補上 {Object.QualifiedName} 的 {arguments.Count} 個引數",
            caretOffset,
            arguments.Count);
    }
}
