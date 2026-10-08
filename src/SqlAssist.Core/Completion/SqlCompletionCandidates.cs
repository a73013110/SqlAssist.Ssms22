using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 一個上下文的建議清單：依 <see cref="SqlCompletionContext.Target"/> 分派到各份候選，再做上下文過濾。
/// </summary>
/// <remarks>
/// 分派只看文字，資料庫那一份由 <see cref="ISqlCompletionMetadata"/> 注入，所以產品與召回稽核走同一條：
/// 分派放在 Ssms22 的時候，稽核只能在測試裡抄一份，而那一份連欄位位置都分派錯了也沒有人發現。
/// 交出去的是平台排名之前的那一份，排名與篩選在 <see cref="SuggestionList"/>。
/// </remarks>
public static class SqlCompletionCandidates
{
    /// <param name="builtIn">
    /// 關鍵字、內建函式與片段（<see cref="BuiltInSuggestionCatalog.Create"/>）；關掉片段的設定在這裡套用。
    /// </param>
    public static async Task<IReadOnlyList<SqlSuggestion>> GetAsync(
        SqlCompletionContext context,
        IReadOnlyList<SqlSuggestion> builtIn,
        SqlAssistSettings settings,
        ISqlCompletionMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (builtIn is null)
        {
            throw new ArgumentNullException(nameof(builtIn));
        }

        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        var candidates = await GetCandidatesAsync(context, builtIn, settings, metadata, cancellationToken)
            .ConfigureAwait(false);

        // 上下文過濾要在建立清單時做完：平台會快取這份清單，
        // 之後每一次按鍵只重新比對前綴，不會再問來源一次。
        return SuggestionContextFilter.Filter(candidates, context);
    }

    private static async Task<IReadOnlyList<SqlSuggestion>> GetCandidatesAsync(
        SqlCompletionContext context,
        IReadOnlyList<SqlSuggestion> builtIn,
        SqlAssistSettings settings,
        ISqlCompletionMetadata metadata,
        CancellationToken cancellationToken)
    {
        // 全域變數是一份封閉的內建清單，這個位置不必等中繼資料——
        // 而物件清單在快取還沒暖的時候會真的去查一次資料庫。
        if (context.Target == CompletionTarget.GlobalVariable)
        {
            return SqlGlobalVariableCatalog.All;
        }

        // 變數全部讀自指令碼本身，上下文分析已經把它們算好了。
        // EXEC dbo.usp_Renew @| 還要加上那個程序的參數——兩者在這個位置都對。
        if (context.Target == CompletionTarget.Variable)
        {
            if (context.ExecutedModule is not { } module || !settings.IncludeDatabaseObjects)
            {
                return context.ScriptSources;
            }

            var parameters = await metadata.GetParametersAsync(module, cancellationToken).ConfigureAwait(false);
            return parameters.Concat(context.ScriptSources).ToArray();
        }

        // 游標與視窗名稱同樣只寫在指令碼裡；FETCH | 還接得了 NEXT、PRIOR 這些方向，
        // OVER ( 還接得了 PARTITION、ORDER，都由片語給。
        if (context.Target is CompletionTarget.Cursor or CompletionTarget.Window)
        {
            return context.ClausePhrase is { } scriptPhrase
                ? context.ScriptSources.Concat(scriptPhrase.Suggestions).ToArray()
                : context.ScriptSources;
        }

        // 引數與提示是純粹的封閉清單，一次資料庫都不必問。片語的字照接上來：前一格判不出位置時，
        // WITH ( 可能是資料表提示，也可能是重建選項。
        switch (context.Target)
        {
            case CompletionTarget.DatePart:
                return SqlArgumentCatalog.DateParts.Concat(PhraseOf(context)).ToArray();
            case CompletionTarget.ExtractField:
                return SqlArgumentCatalog.ExtractFields.Concat(PhraseOf(context)).ToArray();
            case CompletionTarget.TableHint:
                return SqlArgumentCatalog.TableHints.Concat(PhraseOf(context)).ToArray();
            case CompletionTarget.QueryHint:
                return SqlArgumentCatalog.QueryHints.Concat(PhraseOf(context)).ToArray();
            case CompletionTarget.OdbcFunction:
                return SqlArgumentCatalog.OdbcFunctions;

            // 封閉片語的字之外還有以那些字開頭的片段（CURSOR FOR 之後的 ssf），由上下文過濾挑；
            // 收變數的那一格（SET ）再接上指令碼的變數。
            case CompletionTarget.ClauseKeyword:
                return builtIn
                    .Where(item => item.Kind == SuggestionKind.Snippet && IsBuiltInEnabled(item, settings))
                    .Concat(PhraseOf(context))
                    .Concat(context.ScriptSources)
                    .ToArray();
        }

        // 定序、語言與時區的名單只有伺服器知道，但那個位置不會因為問不到而空掉：
        // 文法上的字（DATABASE_DEFAULT）與這份指令碼已經寫過的值都不必送出查詢。
        // 關掉「列出資料庫物件與欄位」的人要的是「不要連線」，剩下的正好是這一份。
        // 指令碼那一份也放著片語收的變數（AT TIME ZONE @tz），見 SqlCompletionPolicy.OffersPhraseVariables。
        if (SqlInstanceList.For(context.Target) is { } instanceList)
        {
            var server = settings.IncludeDatabaseObjects
                ? await metadata.GetInstanceListAsync(instanceList, cancellationToken).ConfigureAwait(false)
                : SqlInstanceListData.Empty;

            return instanceList.Suggestions(context.ScriptSources, server);
        }

        // 登入、使用者、憑證這些名稱只有目錄檢視知道；片語的字照接上來（ALTER DATABASE 之後的 CURRENT、
        // DROP USER 之後的 IF），目錄的關鍵字由上下文過濾照位置挑。關掉「列出資料庫物件與欄位」時只剩字，
        // 與執行個體名單同一條。片語收變數的那一格（BACKUP DATABASE @db）另有指令碼的變數。
        if (context.Target == CompletionTarget.CatalogEntity)
        {
            var suggestions = builtIn.Where(item => item.Kind == SuggestionKind.Keyword).ToList();

            if (settings.IncludeDatabaseObjects)
            {
                foreach (var entity in context.CatalogEntities)
                {
                    var names = await metadata.GetCatalogEntityNamesAsync(entity, cancellationToken).ConfigureAwait(false);
                    suggestions.AddRange(entity.Suggestions(names));
                }
            }

            return suggestions.Concat(PhraseOf(context)).Concat(context.ScriptSources).ToArray();
        }

        // 內建型別是一份封閉的清單，但使用者自訂的資料表型別在資料庫裡，
        // DECLARE @t dbo.XType 要的正是後者。片語的字照接上來：資料行定義的 PERIOD 之後還有 FOR。
        // 宣告的型別前面可以先寫 AS（DECLARE @x AS int），那個字從目錄接上來；不寫型別的 timestamp 資料行另接 NOT、NULL 這些尾端的字。
        if (context.Target == CompletionTarget.DataType)
        {
            var types = settings.IncludeDatabaseObjects
                ? await metadata.GetObjectsAsync(context.QualifierPath, cancellationToken).ConfigureAwait(false)
                : Array.Empty<SqlSuggestion>();

            return SqlDataTypeCatalog.All
                .Concat(types)
                .Concat(PhraseOf(context))
                .Concat(builtIn.Where(item => SuggestionContextFilter.IsTypeSlotKeyword(item, context)))
                .ToArray();
        }

        if (context.Target == CompletionTarget.Column)
        {
            // 片語的字照接上來：DROP COLUMN 之後還有 IF EXISTS。指派的左邊另有寫得出的限定字（別名）。
            var columns = await GetColumnsAsync(context.ColumnSources, settings, metadata, cancellationToken)
                .ConfigureAwait(false);

            return columns.Concat(PhraseOf(context)).Concat(context.ScriptSources).ToArray();
        }

        // 跨資料庫或跨伺服器的限定字：清單只能來自那個地方。混進本地的物件、
        // 關鍵字與敘述裡的欄位就是「看起來完全正常，選中的每一個名稱卻不是
        // 使用者指名的那一個」——而關鍵字與片段在限定字之後本來就一個都不對。
        if (context.QualifierPath is { IsLocal: false })
        {
            return settings.IncludeDatabaseObjects
                ? await metadata.GetObjectsAsync(context.QualifierPath, cancellationToken).ConfigureAwait(false)
                : Array.Empty<SqlSuggestion>();
        }

        // 指令碼自己宣告的 CTE 與暫存資料表不必對資料庫送出任何查詢，
        // 因此與「列出資料庫物件」的設定無關——關掉那個設定的人要的是
        // 「不要連線」，不是「看不到我上一行才寫的名稱」。
        // 不封閉的子句片語（SET IDENTITY_INSERT 之後是資料表）把它的字接上來；
        // 目錄裡的關鍵字在那一格由上下文過濾換成片語的字。
        var local = builtIn
            .Where(item => IsBuiltInEnabled(item, settings))
            .Concat(PhraseOf(context))
            .Concat(context.ScriptSources);

        if (!settings.IncludeDatabaseObjects)
        {
            return local.ToArray();
        }

        var database = await metadata.GetObjectsAsync(qualifierPath: null, cancellationToken).ConfigureAwait(false);

        // 敘述裡看得到的欄位放在資料庫物件前面：SELECT | FROM PUBLISHER a 這種位置，
        // 使用者要的幾乎都是欄位，而不是整個資料庫的物件清單。
        var candidates = local
            .Concat(PeekScopeColumns(context.ScopeSources, settings, metadata))
            .Concat(database);

        // sys.| 與 EXEC | 才把系統物件拉進來：那一份有一兩千筆，混進一般清單的話，
        // 打第一個字元時真正要找的東西會被 sp_ 開頭的名稱淹掉。
        if (context.WantsSystemObjects)
        {
            var system = await metadata
                .GetSystemObjectsAsync(context.QualifierPath, cancellationToken)
                .ConfigureAwait(false);

            candidates = candidates.Concat(system);
        }

        return candidates.ToArray();
    }

    private static IReadOnlyList<SqlSuggestion> PhraseOf(SqlCompletionContext context) =>
        context.ClausePhrase?.Suggestions ?? Array.Empty<SqlSuggestion>();

    /// <summary>
    /// 內建項目是否啟用。
    /// </summary>
    /// <remarks>
    /// 關鍵字不受「輸入時轉大寫」影響：那個開關管的是輸入分隔字元時要不要
    /// 改寫已經打出來的字，與清單裡要不要列出 SELECT 是兩件事。
    /// 目前只有程式碼片段可以個別關掉，關鍵字一律列出。
    /// </remarks>
    private static bool IsBuiltInEnabled(SqlSuggestion item, SqlAssistSettings settings)
    {
        return item.Kind switch
        {
            SuggestionKind.Snippet => settings.IncludeSnippets,
            _ => true
        };
    }

    /// <summary>
    /// 限定字所指資料來源的欄位。
    /// </summary>
    /// <remarks>
    /// 只在使用者真的輸入 <c>別名.</c> 時才走到這裡，因此會落在第二層按需載入：
    /// 一次只查一個物件的欄位，不會因為敘述裡有幾張資料表就全部撈回來。
    /// 插入的文字一律<b>不</b>補限定字：使用者已經自己打了 <c>a.</c>，
    /// 再補一次會變成 <c>a.a.欄位</c>。
    ///
    /// 關掉「列出資料庫物件與欄位」等於不對資料庫送出任何查詢，
    /// 那時只有欄位名稱寫在指令碼裡的來源（子查詢、CTE）列得出來。
    /// </remarks>
    private static async Task<IReadOnlyList<SqlSuggestion>> GetColumnsAsync(
        IReadOnlyList<SqlColumnSource>? sources,
        SqlAssistSettings settings,
        ISqlCompletionMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (sources is null || sources.Count == 0)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var suggestions = new List<SqlSuggestion>();

        foreach (var source in sources)
        {
            if (source.Kind == SqlColumnSourceKind.Names)
            {
                AddScriptColumns(suggestions, source, settings, qualifier: null);
                continue;
            }

            if (settings.IncludeDatabaseObjects)
            {
                suggestions.AddRange(await metadata
                    .GetColumnsAsync(source.Table!, settings, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// 敘述中所有資料來源的欄位，供沒有限定字的位置使用。
    /// </summary>
    /// <remarks>
    /// 資料表與檢視的欄位只取<b>已經在快取裡</b>的，絕不觸發查詢：這條路徑在
    /// 每一次按鍵上。沒命中就這一輪不顯示欄位，<see cref="ISqlCompletionMetadata.WarmColumnsAsync"/>
    /// 會在背景補上，下一次按鍵就有了。子查詢與 CTE 的欄位名稱寫在指令碼裡，不必等任何東西。
    ///
    /// 有兩個以上相異的限定字時，插入的文字會補上別名，否則
    /// <c>SELECT Name FROM A a JOIN B b</c> 這種寫法會因為欄位名稱模稜兩可而執行失敗。
    /// </remarks>
    private static IReadOnlyList<SqlSuggestion> PeekScopeColumns(
        IReadOnlyList<SqlColumnSource> sources,
        SqlAssistSettings settings,
        ISqlCompletionMetadata metadata)
    {
        if (sources is null || sources.Count == 0)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var qualify = NeedsQualifier(sources);
        var suggestions = new List<SqlSuggestion>();

        foreach (var source in sources)
        {
            var qualifier = qualify ? source.Qualifier : null;

            if (source.Kind == SqlColumnSourceKind.Names)
            {
                AddScriptColumns(suggestions, source, settings, qualifier);
                continue;
            }

            suggestions.AddRange(metadata.PeekColumns(source.Table!, qualifier, settings));
        }

        return suggestions;
    }

    /// <summary>
    /// 插入的欄位名稱要不要補限定字。
    /// </summary>
    /// <remarks>
    /// 依據是<b>相異</b>的限定字數量而不是來源數量：<c>FROM (SELECT Id, * FROM T t) d</c>
    /// 攤平出兩個來源，但它們都叫 <c>d</c>，欄位名稱不可能因此模稜兩可。
    /// </remarks>
    private static bool NeedsQualifier(IReadOnlyList<SqlColumnSource> sources)
    {
        string? first = null;

        foreach (var source in sources)
        {
            if (source.Qualifier is null)
            {
                continue;
            }

            if (first is null)
            {
                first = source.Qualifier;
                continue;
            }

            if (!string.Equals(first, source.Qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把只知道名稱的欄位轉成建議項。
    /// </summary>
    /// <remarks>
    /// 子查詢與 CTE 的輸出欄位寫在指令碼裡，型別、NULL 與 PK 都無從得知——
    /// 那些要追到最內層的資料表，而中間任何一段運算式都會讓答案不成立。
    /// 說明欄改寫來源本身：使用者要的是「這個名稱打不打得出來」。
    ///
    /// 暫存資料表與資料表變數說得出出處，就寫它的名字：在
    /// <c>UPDATE #Loan SET |</c> 看到「查詢結果」會讓人以為認錯了東西。
    ///
    /// 欄位的排序刻意保留選取清單的順序，與資料表欄位保留定義順序同一個理由。
    /// </remarks>
    private static void AddScriptColumns(
        List<SqlSuggestion> suggestions,
        SqlColumnSource source,
        SqlAssistSettings settings,
        string? qualifier)
    {
        var origin = source.SourceName ?? ScriptSuggestionText.QueryResult;
        var from = qualifier is null ? string.Empty : $" · {qualifier}";

        foreach (var name in source.Names)
        {
            suggestions.Add(new SqlSuggestion(
                name,
                SqlInsertionText.Column(name, qualifier, settings),
                $"{origin}{from}",
                $"{origin}\r\n{name}",
                SuggestionKind.Column));
        }
    }
}
