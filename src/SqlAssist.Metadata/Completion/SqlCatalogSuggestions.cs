using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Completion;

/// <summary>
/// 把中繼資料的物件、欄位與參數轉成建議項。
/// </summary>
/// <remarks>
/// 轉換不需要 SSMS，放在這裡讓產品的查詢視窗連線與召回稽核的直連資料庫共用一份；
/// 兩邊各轉一次的話，稽核看到的說明、插入文字與種類就不是使用者看到的那一份。
/// </remarks>
public static class SqlCatalogSuggestions
{
    /// <summary>第一層快照裡的物件、有物件的結構描述與資料庫。</summary>
    /// <param name="includeLinkedServers">
    /// 連結伺服器只在目前這條連線的清單裡才對：往右走過任何一格之後，
    /// 那一格的下一段不可能再是一台伺服器（T-SQL 沒有五段式名稱）。
    /// </param>
    public static IReadOnlyList<SqlSuggestion> Objects(SqlDatabaseSnapshot snapshot, bool includeLinkedServers)
    {
        // 結構描述讀擁有物件的那一份：與角色同名的空結構描述選了也接不到任何東西。
        // 完整名單留給 SqlQualifierResolver 認限定字。
        var schemas = snapshot.SchemasWithObjects;
        var suggestions = new List<SqlSuggestion>(
            snapshot.Objects.Count + schemas.Count + snapshot.Databases.Count);

        AddObjects(suggestions, snapshot.Objects);

        // 名稱的中間段一律只寫名稱本身：點號由使用者自己打，而打出點號會讓上下文
        // 整個換掉，重開清單那條路本來就會接手。連點號一起寫進去等於替使用者決定
        // 「你還要繼續往下走」，想直接用這個名稱的人得先退掉一個他沒要求的字元。
        var schemaKind = SqlKindText.Schema;
        var databaseKind = SqlKindText.Database;

        foreach (var schema in schemas)
        {
            suggestions.Add(new SqlSuggestion(
                schema,
                schema,
                schemaKind,
                SqlKindText.Named(schemaKind, SqlIdentifier.Quote(schema)),
                SuggestionKind.Schema,
                schemaName: schema));
        }

        foreach (var database in snapshot.Databases)
        {
            // 插入文字留空給 SqlInsertionText 依設定加括號：資料庫名稱與其他
            // 物件名稱適用同一條規則，含空白或連字號時一定會加，其餘看使用者偏好。
            suggestions.Add(new SqlSuggestion(
                database,
                database,
                databaseKind,
                $"USE {SqlIdentifier.QuoteIfNeeded(database)}",
                SuggestionKind.Database));
        }

        if (includeLinkedServers)
        {
            foreach (var server in snapshot.LinkedServers)
            {
                // 名稱不保證是識別字的形狀——連結伺服器可以直接以位址命名，
                // 方括號由 SqlInsertionText 依與其他名稱同一條規則補。
                suggestions.Add(new SqlSuggestion(
                    server,
                    server,
                    SqlKindText.LinkedServer,
                    SqlKindText.Named(SqlKindText.LinkedServer, SqlIdentifier.QuoteIfNeeded(server)),
                    SuggestionKind.LinkedServer));
            }
        }

        return suggestions;
    }

    /// <summary>一份物件清單（系統物件）；建議清單列不出來的種類略過。</summary>
    public static IReadOnlyList<SqlSuggestion> Objects(IReadOnlyList<SqlObjectInfo> objects)
    {
        var suggestions = new List<SqlSuggestion>(objects.Count);
        AddObjects(suggestions, objects);
        return suggestions;
    }

    /// <summary>
    /// 把中繼資料裡的欄位轉成建議項。
    /// </summary>
    /// <remarks>
    /// 呼叫端一律照資料表的定義順序逐欄呼叫，不重排：模糊比對的分數才是主要排名依據，
    /// 而分數相同時（例如還沒輸入任何字元）依序號排列比字母序更接近使用者的心智模型。
    /// </remarks>
    /// <param name="qualifier">
    /// 插入時要補在欄位前面的別名或資料表名稱；不需要限定時為 null。
    /// </param>
    public static SqlSuggestion Column(
        SqlObjectInfo info,
        SqlColumnInfo column,
        SqlAssistSettings settings,
        string? qualifier)
    {
        var annotations = column.IsPrimaryKey ? " · PK" : string.Empty;
        var source = qualifier is null ? string.Empty : $" · {qualifier}";
        var insertionText = SqlInsertionText.Column(column.Name, qualifier, settings);

        return new SqlSuggestion(
            column.Name,
            insertionText,
            $"{column.DataType}{(column.IsNullable ? " NULL" : " NOT NULL")}{annotations}{source}",
            $"{info.QualifiedName}\r\n{column.ToScriptLine()}",
            SuggestionKind.Column,
            schemaName: info.SchemaName,
            tag: column);
    }

    /// <summary>
    /// 一個可執行模組的參數。
    /// </summary>
    /// <remarks>
    /// 插入文字連 <c> = </c> 一起寫進去：打出參數名稱就是要做具名傳值，
    /// 而 <c>EXEC p @readerId</c>（沒有等號）在文法上是照順序傳一個變數，
    /// 那是另一件事，由變數那一份負責。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Parameters(SqlObjectInfo module, SqlObjectDetail detail)
    {
        var suggestions = new List<SqlSuggestion>(detail.Parameters.Count);

        foreach (var parameter in detail.Parameters)
        {
            // 純量函式的傳回值也在這一份裡，它的名稱是空字串。
            if (parameter.Name.Length == 0)
            {
                continue;
            }

            suggestions.Add(new SqlSuggestion(
                parameter.Name,
                parameter.Name + " = ",
                parameter.IsOutput ? parameter.DataType + " OUTPUT" : parameter.DataType,
                CatalogSuggestionText.ParameterOf(module.QualifiedName, parameter.ToScriptLine()),
                SuggestionKind.Parameter));
        }

        return suggestions;
    }

    private static void AddObjects(List<SqlSuggestion> suggestions, IReadOnlyList<SqlObjectInfo> objects)
    {
        foreach (var info in objects)
        {
            var kind = ToSuggestionKind(info.Kind);

            if (kind is null)
            {
                continue;
            }

            suggestions.Add(new SqlSuggestion(
                info.Name,
                info.QualifiedName,
                $"{info.Kind.ToDisplayName()} · {info.SchemaName}",
                // 預覽內容改為選取時才載入，這裡只放立即可得的標題。
                info.Kind.ToDisplayTitle(info.QualifiedName),
                kind.Value,
                schemaName: info.SchemaName,
                tag: info));
        }
    }

    private static SuggestionKind? ToSuggestionKind(SqlObjectKind kind)
    {
        return kind switch
        {
            SqlObjectKind.Table => SuggestionKind.Table,
            // 同義字幾乎都指向資料表或檢視，放在資料來源清單裡才找得到。
            SqlObjectKind.Synonym => SuggestionKind.Table,
            SqlObjectKind.View => SuggestionKind.View,
            SqlObjectKind.Procedure => SuggestionKind.Procedure,
            SqlObjectKind.ScalarFunction => SuggestionKind.Function,

            // 資料表值函式與純量函式分開：前者接得上 FROM、JOIN 與 APPLY，
            // 後者只出現在運算式位置。壓成同一類的症狀是 FROM 之後一個函式都不出現。
            SqlObjectKind.InlineTableFunction => SuggestionKind.TableFunction,
            SqlObjectKind.TableValuedFunction => SuggestionKind.TableFunction,
            SqlObjectKind.Trigger => SuggestionKind.Trigger,
            SqlObjectKind.Sequence => SuggestionKind.Sequence,
            SqlObjectKind.TableType => SuggestionKind.UserDefinedType,
            _ => null
        };
    }
}
