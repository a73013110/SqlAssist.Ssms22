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
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.QuickInfo;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.Snippets;
using SqlAssist.Ssms22.UI;

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

    /// <summary>這一次的適用範圍是原生 Snippet 欄位時，樣板為它填的預設值。</summary>
    /// <remarks>
    /// 排名器要用它把「整格還是樣板的字」判成空前綴。放在 session 上而不是欄位：
    /// 排名器是所有編輯器共用的<b>一個</b>實例，存不了任何一個 session 的狀態。
    /// </remarks>
    internal const string FieldDefaultKey = "SqlAssist.SnippetFieldDefault";

    /// <summary>中繼資料認出的限定字最左邊那一段落在哪一格。</summary>
    /// <remarks>
    /// 只看文字時 <c>dbo.</c>、<c>LibArchive.</c> 與 <c>LibMirror.</c> 是同一個形狀，
    /// 認出來要問這條連線上的三份名單，而提交在按鍵路徑上，不能再問一次。
    /// 因此把答案掛在 session 上帶到提交那一端，由
    /// <see cref="SqlObjectPath.TryRealign"/> 照同一個方法挪回去。
    ///
    /// 記在 session 而不是每一個 <see cref="CompletionItem"/> 上：同一次清單裡
    /// 每一項的限定字都是同一串，掛幾百份是同一個答案。
    /// </remarks>
    internal const string QualifierSlotKey = "SqlAssist.QualifierSlot";

    /// <summary>建立 <see cref="_builtIn"/> 時所用的那一份 Snippet 清單。</summary>
    private static SqlSnippetLibrary? _builtInSnippets;

    private static IReadOnlyList<SqlSuggestion> _builtIn = Array.Empty<SqlSuggestion>();

    private static readonly object BuiltInGate = new();

    private readonly SqlMetadataService _metadataService;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>問原生 Snippet 欄位範圍要它；來源與編輯器是一對一的。</summary>
    private readonly ITextView _textView;

    /// <summary>
    /// 上一次 <see cref="InitializeCompletion"/> 判定的原生 Snippet 欄位範圍。
    /// </summary>
    /// <remarks>
    /// <see cref="GetCompletionContextAsync"/> 需要知道「這一次是不是欄位模式」，
    /// 但它跑在平台的背景執行緒上，而欄位範圍只能在 UI 執行緒向引擎要（COM）。
    /// 因此在 UI 執行緒上判定一次、記在這裡，背景那一步只比對 <see cref="Span"/>
    /// 相不相等——那是實質不可變的結構，而且要比對的就是上一步剛寫進去的那一次。
    ///
    /// 一個欄位就夠：來源是每個編輯器一份，而同一個編輯器同時只有一個 session。
    /// </remarks>
    private Span? _fieldSpan;

    /// <summary>與 <see cref="_fieldSpan"/> 同一次判定的欄位預設值。</summary>
    private string _fieldDefault = string.Empty;

    public SqlAsyncCompletionSource(
        ITextView textView,
        SqlMetadataService metadataService,
        IServiceProvider serviceProvider)
    {
        _textView = textView;
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
        _fieldSpan = null;
        _fieldDefault = string.Empty;

        if (!settings.Enabled || !settings.SuggestionsEnabled)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        // 原生 Snippet 欄位裡，適用範圍是整格、上下文只看這一格起點之前的文字；
        // 沒有 session 時這個查詢第一行就走掉，一般編輯不付任何代價。
        var fieldSpan = SqlSnippetExpansionController.FindFieldSpan(_textView, triggerLocation);

        // 只看游標前文就夠：適用範圍與要不要參與只跟詞元起點、前綴與前方關鍵字有關。
        // 這個方法在按鍵路徑上同步執行，換成全文分析等於每按一鍵就多掃一次整份指令碼。
        var context = SqlCompletionContextAnalyzer.Analyze(
            triggerLocation.Snapshot.GetText(
                0,
                SqlSnippetExpansionController.ResolveAnalysisEnd(
                    fieldSpan,
                    triggerLocation.Position)));

        if (!context.IsValid)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        if (context.Target == CompletionTarget.Any &&
            context.QualifierPath is null &&
            context.Prefix.Length < settings.TriggerAfterCharacters)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        // 欄位模式的範圍已經由引擎給定，不必再驗詞元起點——那個起點算的是
        // 「這一格之前」那一段的尾巴，與這次要取代的範圍無關。
        if (fieldSpan is { } field)
        {
            _fieldSpan = field.Span.Span;
            _fieldDefault = field.DefaultValue;
            return new CompletionStartData(CompletionParticipation.ProvidesItems, field.Span);
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
        return SqlAssistPlatformGuard.RunAsync(
            "建議清單取得",
            () => GetCompletionContextCoreAsync(session, triggerLocation, applicableToSpan, token),
            fallback: CompletionContext.Empty);
    }

    private async Task<CompletionContext> GetCompletionContextCoreAsync(
        IAsyncCompletionSession session,
        SnapshotPoint triggerLocation,
        SnapshotSpan applicableToSpan,
        CancellationToken token)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var settings = SqlAssistSettingsStore.Current;
        var context = Analyze(triggerLocation, applicableToSpan);

        // 限定字最左邊那一段是結構描述、資料庫還是連結伺服器，只看文字分不出來。
        // 在問清單之前就換成對齊過的上下文，後面的候選來源、過濾與插入文字才會
        // 讀到同一個答案；各自再判一次的話，症狀是清單列得出來、Tab 下去少一段。
        //
        // 關掉「列出資料庫物件與欄位」的人要的是「不要連線」，這裡跟著不問。
        if (settings.IncludeDatabaseObjects)
        {
            context = await _metadataService
                .ResolveQualifierAsync(context, token)
                .ConfigureAwait(false);
        }

        // 提交那一端的上下文是從文字重新分析的，認不出「LibArchive. 其實是資料庫」
        // ——那是中繼資料的答案，而提交在按鍵路徑上，不能再問一次。整條路上只認
        // 這一次，答案帶著走。
        if (context.QualifierPath is { } resolvedQualifier)
        {
            session.Properties[QualifierSlotKey] = resolvedQualifier.LeftmostSlot;
        }

        // 排名器讀這一份判斷「整格還不是使用者打的字」。它自己比對當下的文字，
        // 因此使用者一打字就自動失效，不必在這裡跟著更新。
        if (_fieldSpan == applicableToSpan.Span && _fieldDefault.Length > 0)
        {
            session.Properties[FieldDefaultKey] = _fieldDefault;
        }

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

        // 只有真的產出 SqlAssist items 才取得 ownership；空 context 可能仍由別的來源顯示。
        OwnPreviewSession(session);
        return new CompletionContext(items);
    }

    /// <summary>
    /// 把這份清單交給結構預覽接管。
    /// </summary>
    /// <remarks>
    /// 這個方法本身跑在平台的背景執行緒上，而預覽會訂閱編輯器事件、建立
    /// <see cref="DispatcherTimer"/>，那些都必須在 UI 執行緒上完成——連
    /// <see cref="SqlStructurePreview.GetOrCreate"/> 也是，它的建構函式就在掛事件。
    ///
    /// 刻意**不等**這一次派送完成：<c>GetCompletionContextAsync</c> 是平台的
    /// 建議清單來源，等 UI 執行緒空出來才回傳，等於把「清單多久出現」綁在 UI 的
    /// 忙碌程度上；而平台只要有任何一條路徑在 UI 執行緒上同步等 completion model
    /// （<c>GetComputedItems</c> 就是會阻塞的），互等就是死結。
    /// 晚一步接管沒有代價：<see cref="SqlStructurePreview.OwnSession"/> 自己會排一次
    /// 對帳，就算漏掉第一次 <c>ItemsUpdated</c>，選取仍然會被讀回來。
    /// </remarks>
    private void OwnPreviewSession(IAsyncCompletionSession session)
    {
        if (SqlAssistSettingsStore.Current.PreviewMode == SqlPreviewMode.Off ||
            session.IsDismissed ||
            session.TextView is not IWpfTextView textView)
        {
            return;
        }

        textView.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() => SqlAssistPlatformGuard.Run(
                "接管建議清單的結構預覽",
                () => SqlStructurePreview
                    .GetOrCreate(textView, _serviceProvider)
                    ?.OwnSession(session, _metadataService))));
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

            var types = await _metadataService
                .GetSuggestionsAsync(context.QualifierPath, token)
                .ConfigureAwait(false);

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

        // 跨資料庫或跨伺服器的限定字：清單只能來自那個地方。混進本地的物件、
        // 關鍵字與敘述裡的欄位就是「看起來完全正常，選中的每一個名稱卻不是
        // 使用者指名的那一個」——而關鍵字與片段在限定字之後本來就一個都不對。
        if (context.QualifierPath is { IsLocal: false })
        {
            return settings.IncludeDatabaseObjects
                ? await _metadataService
                    .GetSuggestionsAsync(context.QualifierPath, token)
                    .ConfigureAwait(false)
                : Array.Empty<SqlSuggestion>();
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
                .GetSystemSuggestionsAsync(context.QualifierPath, token)
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
            // 篩選類別與圖示不是同一個問題：同義字歸入資料表那一類，圖示仍須
            // 呈現真正的物件種類，判斷整份在 SqlIcons。
            icon: SqlIcons.GetImageElement(suggestion),
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

    /// <summary>
    /// 這一次清單的上下文。
    /// </summary>
    /// <remarks>
    /// 欄位模式下改從<b>這一格的起點</b>分析。不這樣做的話，前綴會是格子裡的
    /// <c>TargetTable</c>、限定字會是 <c>dbo</c>：前者讓
    /// <c>SuggestionMatcher</c> 把整份清單濾光，後者讓插入文字退化成不帶結構描述的
    /// 簡名。兩個症狀都沒有錯誤訊息。
    ///
    /// 判斷靠 <see cref="_fieldSpan"/> 而不是重問一次引擎：這個方法在平台的背景
    /// 執行緒上，那個查詢是 COM，只能在 UI 執行緒做。
    /// </remarks>
    private SqlCompletionContext Analyze(SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan)
    {
        // 只有「整格還是樣板填的預設值」那一次要當它不存在；使用者打過字之後，
        // 那幾個字就是前綴，照一般方式分析到游標為止。判斷與
        // SqlSnippetExpansionController.ResolveAnalysisEnd 是同一條規則，
        // 這裡不能改問引擎——這個方法在平台的背景執行緒上。
        var caret = _fieldSpan == applicableToSpan.Span &&
            string.Equals(applicableToSpan.GetText(), _fieldDefault, StringComparison.Ordinal)
            ? applicableToSpan.Start.Position
            : triggerLocation.Position;

        return SqlCompletionContextAnalyzer.Analyze(triggerLocation.Snapshot.GetText(), caret);
    }
}
