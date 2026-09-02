using System;
using System.Collections.Generic;
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

    public SqlObjectInfo Object { get; }

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
/// </remarks>
internal sealed class SqlInsertStatementExpansion : ISqlCommitExpansion
{
    private readonly SqlAssistSettings _settings;

    public SqlInsertStatementExpansion(SqlObjectInfo objectInfo, SqlAssistSettings settings)
    {
        Object = objectInfo;
        _settings = settings;
    }

    public SqlObjectInfo Object { get; }

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

    public SqlMergeStatementExpansion(SqlObjectInfo objectInfo, SqlAssistSettings settings)
    {
        Object = objectInfo;
        _settings = settings;
    }

    public SqlObjectInfo Object { get; }

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
    public SqlProcedureCallExpansion(SqlObjectInfo objectInfo)
    {
        Object = objectInfo;
    }

    public SqlObjectInfo Object { get; }

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

            parameters.Add(new SqlStatementParameter(
                parameter.Name,
                parameter.DataType,
                parameter.IsOutput,
                optional.Contains(parameter.Name)));
        }

        // 沒有參數的程序展開起來與只插入名稱完全一樣，那就不必動它——
        // 擴充預存程序（sp_executesql 的鄰居）在 sys.parameters 裡也沒有列，
        // 同樣落在這一條。
        if (parameters.Count == 0)
        {
            SqlAssistDiagnostics.Write(
                $"{Object.QualifiedName} 沒有參數，維持只插入名稱");
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
