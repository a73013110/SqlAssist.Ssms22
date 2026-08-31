using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.QuickInfo;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.Snippets;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 平台原生非同步 IntelliSense 的建議來源。
/// </summary>
/// <remarks>
/// 定位、螢幕邊界、捲動、滑鼠操作與佈景主題全部由編輯器負責，
/// 中繼資料可以直接在 <see cref="GetCompletionContextAsync"/> 裡 await。
/// </remarks>
internal sealed class SqlAsyncCompletionSource : IAsyncCompletionSource
{
    /// <summary>把建議項原始資料掛回 <see cref="CompletionItem"/> 的鍵。</summary>
    internal const string SuggestionKey = "SqlAssist.Suggestion";

    /// <summary>建立 <see cref="_builtIn"/> 時所用的那一份 Snippet 清單。</summary>
    private static SqlSnippetLibrary? _builtInSnippets;

    private static IReadOnlyList<SqlSuggestion> _builtIn = Array.Empty<SqlSuggestion>();

    private static readonly object BuiltInGate = new();

    private readonly SqlMetadataService _metadataService;
    private readonly IServiceProvider _serviceProvider;

    public SqlAsyncCompletionSource(SqlMetadataService metadataService, IServiceProvider serviceProvider)
    {
        _metadataService = metadataService;
        _serviceProvider = serviceProvider;
    }

    public CompletionStartData InitializeCompletion(
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        CancellationToken token)
    {
        // 這個方法在按鍵路徑上同步執行，丟出例外會直接打斷輸入。
        return SqlAssistPlatformGuard.Run(
            "建議來源初始化",
            () => InitializeCompletionCore(triggerLocation),
            fallback: CompletionStartData.DoesNotParticipateInCompletion);
    }

    private CompletionStartData InitializeCompletionCore(SnapshotPoint triggerLocation)
    {
        var settings = SqlAssistSettingsStore.Current;

        if (!settings.Enabled || !settings.SuggestionsEnabled)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        // 只看游標前文就夠：適用範圍與要不要參與只跟詞元起點、前綴與前方關鍵字有關。
        // 這個方法在按鍵路徑上同步執行，換成全文分析等於每按一鍵就多掃一次整份指令碼。
        var context = SqlCompletionContextAnalyzer.Analyze(
            triggerLocation.Snapshot.GetText(0, triggerLocation.Position));

        if (!context.IsValid)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        if (context.Target == CompletionTarget.Any &&
            context.Qualifier is null &&
            context.Prefix.Length < settings.TriggerAfterCharacters)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        // 範圍必須自己驗一次，不能靠例外兜底：TokenStart 是從文字分析算出來的，
        // 而觸發位置來自平台，兩者之間只要有一次不同步（例如編輯剛好插在中間），
        // Span.FromBounds 就會丟出例外，那在按鍵路徑上等於一次錯誤對話框。
        if (context.TokenStart < 0 || context.TokenStart > triggerLocation.Position)
        {
            SqlAssistDiagnostics.Write(
                $"略過這次建議：詞元起點 {context.TokenStart} 不在觸發位置 {triggerLocation.Position} 之前");
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        var applicableSpan = new SnapshotSpan(
            triggerLocation.Snapshot,
            Span.FromBounds(context.TokenStart, triggerLocation.Position));

        return new CompletionStartData(CompletionParticipation.ProvidesItems, applicableSpan);
    }

    public Task<CompletionContext> GetCompletionContextAsync(
        IAsyncCompletionSession session,
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        SnapshotSpan applicableToSpan,
        CancellationToken token)
    {
        var preview = SqlAssistSettingsStore.Current.PreviewMode == SqlPreviewMode.Off
            ? null
            : SqlAssistPlatformGuard.Run<SqlStructurePreview?>(
                "取得結構預覽 session",
                () => SqlStructurePreview.GetOrCreate(session.TextView, _serviceProvider),
                fallback: null);

        return SqlAssistPlatformGuard.RunAsync(
            "建議清單取得",
            () => GetCompletionContextCoreAsync(session, preview, triggerLocation, token),
            fallback: CompletionContext.Empty);
    }

    private async Task<CompletionContext> GetCompletionContextCoreAsync(
        IAsyncCompletionSession session,
        SqlStructurePreview? preview,
        SnapshotPoint triggerLocation,
        CancellationToken token)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var settings = SqlAssistSettingsStore.Current;
        var context = Analyze(triggerLocation);

        // 使用者輸入 a. 的那一刻才查欄位，等待就完全落在打字的節奏上。
        // 但這時他已經打過 FROM PUBLISHER a，敘述裡有哪些資料表是已知的，
        // 先在背景把欄位撈回來，按下點號時就能直接命中快取。
        if (settings.IncludeDatabaseObjects)
        {
            _metadataService.WarmColumns(context.ScopeSources);
        }

        var candidates = await GetCandidatesAsync(context, settings, token).ConfigureAwait(false);

        // 上下文過濾要在建立清單時做完：平台會快取這份清單，
        // 之後每一次按鍵只重新比對前綴，不會再問來源一次。
        var suggestions = SuggestionMatcher.Filter(candidates, context);

        if (suggestions.Count == 0)
        {
            return CompletionContext.Empty;
        }

        // 分類是否掛得上要看整份清單，不是逐項決定的：只有一種分類時
        // 篩選列不該出現。
        var withFilters = settings.ShowCategoryFilters &&
            SqlCompletionFilters.HasMultipleCategories(suggestions);

        var items = suggestions
            .Select(suggestion => CreateItem(suggestion, settings, context, withFilters))
            .ToImmutableArray();

        // 使用者感受到的就是這個數字：從平台要清單，到清單交出去為止。
        total.Stop();

        if (total.ElapsedMilliseconds >= 200)
        {
            SqlAssistDiagnostics.WriteAlways(
                $"耗時 {total.ElapsedMilliseconds} ms：建議清單（目標 {context.Target}，{items.Length} 筆）");
        }

        var result = new CompletionContext(items);

        // 只有真的產出 SqlAssist items 才取得 ownership；空 context 可能仍由別的來源顯示。
        // 在交回結果前切回 UI 執行緒完成訂閱，ItemsUpdated 才不會先一步漏掉第一次選取。
        if (preview is not null &&
            !session.IsDismissed &&
            session.TextView is IWpfTextView textView)
        {
            await textView.VisualElement.Dispatcher.InvokeAsync(
                () => preview.OwnSession(session, _metadataService),
                DispatcherPriority.Normal,
                token);
        }

        return result;
    }

    /// <summary>
    /// 右側說明面板的內容。
    /// </summary>
    /// <remarks>
    /// 資料庫物件的欄位與定義是中繼資料的第二、三層，只有在使用者真的停在該項目上
    /// 才會載入，因此不會為了顯示清單就把整個資料庫的定義本文拉回來。
    /// </remarks>
    public Task<object?> GetDescriptionAsync(
        IAsyncCompletionSession session,
        CompletionItem item,
        CancellationToken token)
    {
        return SqlAssistPlatformGuard.RunAsync<object?>(
            "建議說明取得",
            () => GetDescriptionCoreAsync(session, item, token),
            fallback: null);
    }

    private async Task<object?> GetDescriptionCoreAsync(
        IAsyncCompletionSession session,
        CompletionItem item,
        CancellationToken token)
    {
        if (!item.Properties.TryGetProperty<SqlSuggestion>(SuggestionKey, out var suggestion))
        {
            return null;
        }

        var objectInfo = suggestion.Tag as SqlObjectInfo;
        var mode = SqlAssistSettingsStore.Current.PreviewMode;

        // 平台每換一次選取就問一次說明，這正是「選取換了項目」的信號。
        // 預覽只記下是誰，沒展開就不畫也不查。
        if (mode != SqlPreviewMode.Off &&
            SqlStructurePreview.Peek(session.TextView) is { } preview)
        {
            preview.ReconcileSelection(session, _metadataService);

            // 預覽視窗接手之後就不要再回傳說明內容：
            // 兩個視窗同時貼在清單旁邊只會互相搶位置。
            return null;
        }

        if (objectInfo is null)
        {
            return suggestion.Preview;
        }

        var detail = await _metadataService.GetDetailAsync(objectInfo, token).ConfigureAwait(false);

        return detail is null
            ? SqlQuickInfoContentBuilder.BuildLoading(objectInfo)
            : SqlQuickInfoContentBuilder.Build(detail);
    }

    private async Task<IReadOnlyList<SqlSuggestion>> GetCandidatesAsync(
        SqlCompletionContext context,
        SqlAssistSettings settings,
        CancellationToken token)
    {
        // 全域變數是一份封閉的內建清單，這個位置不必等中繼資料——
        // 而 GetSuggestionsAsync 在快取還沒暖的時候會真的去查一次資料庫。
        if (context.Target == CompletionTarget.GlobalVariable)
        {
            return SqlGlobalVariableCatalog.All;
        }

        // 變數全部讀自指令碼本身，上下文分析已經把它們算好了。
        // EXEC dbo.usp_Renew @| 還要加上那個程序的參數——兩者在這個位置都對。
        if (context.Target == CompletionTarget.Variable)
        {
            if (context.ExecutedModule is not { } module)
            {
                return context.ScriptSources;
            }

            var parameters = await _metadataService
                .GetParameterSuggestionsAsync(module, settings.IncludeDatabaseObjects, token)
                .ConfigureAwait(false);

            return parameters.Concat(context.ScriptSources).ToArray();
        }

        // 引數與提示是純粹的封閉清單，一次資料庫都不必問。
        switch (context.Target)
        {
            case CompletionTarget.DatePart:
                return SqlArgumentCatalog.DateParts;
            case CompletionTarget.TableHint:
                return SqlArgumentCatalog.TableHints;
            case CompletionTarget.QueryHint:
                return SqlArgumentCatalog.QueryHints;
        }

        // 內建型別是一份封閉的清單，但使用者自訂的資料表型別在資料庫裡，
        // DECLARE @t dbo.XType 要的正是後者。
        if (context.Target == CompletionTarget.DataType)
        {
            if (!settings.IncludeDatabaseObjects)
            {
                return SqlDataTypeCatalog.All;
            }

            var types = await _metadataService.GetSuggestionsAsync(token).ConfigureAwait(false);
            return SqlDataTypeCatalog.All.Concat(types).ToArray();
        }

        if (context.Target == CompletionTarget.Column)
        {
            // 關掉「列出資料庫物件與欄位」等於不對資料庫送出任何查詢，
            // 那時只有欄位名稱寫在指令碼裡的來源（子查詢、CTE）列得出來。
            return await _metadataService
                .GetColumnSuggestionsAsync(context.ColumnSources!, settings.IncludeDatabaseObjects, token)
                .ConfigureAwait(false);
        }

        // 指令碼自己宣告的 CTE 與暫存資料表不必對資料庫送出任何查詢，
        // 因此與「列出資料庫物件」的設定無關——關掉那個設定的人要的是
        // 「不要連線」，不是「看不到我上一行才寫的名稱」。
        var builtIn = GetBuiltIn()
            .Where(item => IsBuiltInEnabled(item, settings))
            .Concat(context.ScriptSources);

        if (!settings.IncludeDatabaseObjects)
        {
            return builtIn.ToArray();
        }

        var database = await _metadataService.GetSuggestionsAsync(token).ConfigureAwait(false);

        // 敘述裡看得到的欄位放在資料庫物件前面：SELECT | FROM PUBLISHER a 這種位置，
        // 使用者要的幾乎都是欄位，而不是整個資料庫的物件清單。
        var scopeColumns = _metadataService.GetCachedScopeColumns(context.ScopeSources);
        var candidates = builtIn.Concat(scopeColumns).Concat(database);

        // sys.| 與 EXEC | 才把系統物件拉進來：那一份有一兩千筆，混進一般清單的話，
        // 打第一個字元時真正要找的東西會被 sp_ 開頭的名稱淹掉。
        if (context.WantsSystemObjects)
        {
            var system = await _metadataService
                .GetSystemSuggestionsAsync(token)
                .ConfigureAwait(false);

            candidates = candidates.Concat(system);
        }

        return candidates.ToArray();
    }

    private CompletionItem CreateItem(
        SqlSuggestion suggestion,
        SqlAssistSettings settings,
        SqlCompletionContext context,
        bool withFilters)
    {
        var item = new CompletionItem(
            displayText: suggestion.DisplayText,
            source: this,
            icon: null!,
            filters: withFilters
                ? SqlCompletionFilters.For(suggestion.Kind)
                : ImmutableArray<CompletionFilter>.Empty,
            suffix: suggestion.Description,
            insertText: SqlInsertionText.Build(suggestion, context, settings),
            sortText: suggestion.DisplayText,
            filterText: suggestion.DisplayText,
            automationText: suggestion.DisplayText,
            attributeIcons: ImmutableArray<ImageElement>.Empty);

        // 提交與排名都需要拿回原始建議項；PropertyCollection 是官方提供的掛載點。
        item.Properties.AddProperty(SuggestionKey, suggestion);
        return item;
    }

    /// <summary>
    /// 關鍵字與 Snippet 的候選清單。
    /// </summary>
    /// <remarks>
    /// 關鍵字是固定的，Snippet 則會被管理介面改掉，因此整份重建，但只在
    /// Snippet 清單真的換過之後才重建——<see cref="SqlSnippetLibrary"/> 不可變，
    /// 存檔時整份換新，所以比對參考就足夠。
    ///
    /// 這個方法在背景執行緒上被呼叫，重建期間要擋住其他人拿到半成品。
    /// </remarks>
    private static IReadOnlyList<SqlSuggestion> GetBuiltIn()
    {
        var snippets = SqlSnippetStore.Current;

        lock (BuiltInGate)
        {
            if (!ReferenceEquals(_builtInSnippets, snippets))
            {
                _builtIn = BuiltInSuggestionCatalog.Create(snippets);
                _builtInSnippets = snippets;
            }

            return _builtIn;
        }
    }

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

    private static SqlCompletionContext Analyze(SnapshotPoint triggerLocation)
    {
        return SqlCompletionContextAnalyzer.Analyze(
            triggerLocation.Snapshot.GetText(),
            triggerLocation.Position);
    }
}
