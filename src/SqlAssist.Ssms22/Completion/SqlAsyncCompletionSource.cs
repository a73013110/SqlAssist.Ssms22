using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata;
using SqlAssist.Ssms22.QuickInfo;
using SqlAssist.Ssms22.Preview;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 平台原生非同步 IntelliSense 的建議來源。
/// </summary>
/// <remarks>
/// 相對於自製 WPF 清單，改由編輯器負責定位、螢幕邊界、捲動、滑鼠操作與佈景主題。
/// 中繼資料可以直接在 <see cref="GetCompletionContextAsync"/> 裡 await，
/// 不必再用「先收起清單、載入完成後重新整理」的迂迴做法。
/// </remarks>
internal sealed class SqlAsyncCompletionSource : IAsyncCompletionSource
{
    /// <summary>把建議項原始資料掛回 <see cref="CompletionItem"/> 的鍵。</summary>
    internal const string SuggestionKey = "SqlAssist.Suggestion";

    private static readonly IReadOnlyList<SqlSuggestion> BuiltIn = BuiltInSuggestionCatalog.Create();

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
        try
        {
            var settings = SettingsService.Default.GetSnapshot();

            if (!settings.Enabled ||
                !settings.Suggestions.Enabled ||
                settings.Suggestions.Engine != CompletionEngine.Native)
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
            }

            var context = Analyze(triggerLocation);

            if (!context.IsValid)
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
            }

            var triggerCharacters = Math.Max(1, Math.Min(10, settings.Suggestions.TriggerAfterCharacters));

            if (context.Target == CompletionTarget.Any &&
                context.Qualifier is null &&
                context.Prefix.Length < triggerCharacters)
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

            AsyncCompletionProbe.RecordInitialize($"{trigger.Reason} '{trigger.Character}'");
            AsyncCompletionProbe.RecordParticipation();

            var applicableSpan = new SnapshotSpan(
                triggerLocation.Snapshot,
                Span.FromBounds(context.TokenStart, triggerLocation.Position));

            return new CompletionStartData(CompletionParticipation.ProvidesItems, applicableSpan);
        }
        catch (Exception exception)
        {
            // 這個方法在按鍵路徑上同步執行，丟出例外會直接打斷輸入。
            AsyncCompletionProbe.RecordError(exception);
            SqlAssistDiagnostics.WriteAlways($"建議來源初始化失敗：{exception}");
            return CompletionStartData.DoesNotParticipateInCompletion;
        }
    }

    public async Task<CompletionContext> GetCompletionContextAsync(
        IAsyncCompletionSession session,
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        SnapshotSpan applicableToSpan,
        CancellationToken token)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var settings = SettingsService.Default.GetSnapshot();
            var context = Analyze(triggerLocation);

            // 使用者輸入 a. 的那一刻才查欄位，等待就完全落在打字的節奏上。
            // 但這時他已經打過 FROM PUBLISHER a，敘述裡有哪些資料表是已知的，
            // 先在背景把欄位撈回來，按下點號時就能直接命中快取。
            var scope = SqlScopeAnalyzer.Analyze(
                triggerLocation.Snapshot.GetText(),
                triggerLocation.Position);

            if (settings.Features.ObjectPicker)
            {
                _metadataService.WarmColumns(scope.Tables);
            }

            var candidates = await GetCandidatesAsync(context, scope, settings, token).ConfigureAwait(false);

            // 上下文過濾要在建立清單時做完：平台會快取這份清單，
            // 之後每一次按鍵只重新比對前綴，不會再問來源一次。
            var suggestions = SuggestionMatcher.Filter(candidates, context);

            if (suggestions.Count == 0)
            {
                return CompletionContext.Empty;
            }

            var items = suggestions
                .Select(suggestion => CreateItem(suggestion, settings, context))
                .ToImmutableArray();

            AsyncCompletionProbe.RecordContext(items.Length);

            // 使用者感受到的就是這個數字：從平台要清單，到清單交出去為止。
            total.Stop();

            if (total.ElapsedMilliseconds >= 200)
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"耗時 {total.ElapsedMilliseconds} ms：建議清單（目標 {context.Target}，{items.Length} 筆）");
            }

            return new CompletionContext(items);
        }
        catch (OperationCanceledException)
        {
            return CompletionContext.Empty;
        }
        catch (Exception exception)
        {
            AsyncCompletionProbe.RecordError(exception);
            SqlAssistDiagnostics.WriteAlways($"建議清單取得失敗：{exception}");
            return CompletionContext.Empty;
        }
    }

    /// <summary>
    /// 右側說明面板的內容。
    /// </summary>
    /// <remarks>
    /// 資料庫物件的欄位與定義是中繼資料的第二、三層，只有在使用者真的停在該項目上
    /// 才會載入，因此不會為了顯示清單就把整個資料庫的定義本文拉回來。
    /// </remarks>
    public async Task<object?> GetDescriptionAsync(
        IAsyncCompletionSession session,
        CompletionItem item,
        CancellationToken token)
    {
        try
        {
            AsyncCompletionProbe.RecordDescription();

            if (!item.Properties.TryGetProperty<SqlSuggestion>(SuggestionKey, out var suggestion))
            {
                return null;
            }

            var objectInfo = suggestion.Tag as SqlObjectInfo;
            var mode = SettingsService.Default.GetSnapshot().Preview.Mode;

            // 平台每換一次選取就問一次說明，這正是「選取換了項目」的信號。
            // 預覽只記下是誰，沒展開就不畫也不查。
            if (mode != SqlPreviewMode.Off &&
                SqlStructurePreview.Peek(session.TextView) is { } preview)
            {
                preview.OnItemSelected(objectInfo, _metadataService);

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
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"建議說明取得失敗：{exception.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<SqlSuggestion>> GetCandidatesAsync(
        SqlCompletionContext context,
        SqlStatementScope scope,
        SqlAssistSettings settings,
        CancellationToken token)
    {
        if (context.Target == CompletionTarget.Column)
        {
            return settings.Features.ObjectPicker
                ? await _metadataService
                    .GetColumnSuggestionsAsync(context.QualifiedTable!, token)
                    .ConfigureAwait(false)
                : Array.Empty<SqlSuggestion>();
        }

        var builtIn = BuiltIn.Where(item => IsBuiltInEnabled(item, settings));

        if (!settings.Features.ObjectPicker)
        {
            return builtIn.ToArray();
        }

        var database = await _metadataService.GetSuggestionsAsync(token).ConfigureAwait(false);

        // 敘述裡看得到的欄位放在資料庫物件前面：SELECT | FROM PUBLISHER a 這種位置，
        // 使用者要的幾乎都是欄位，而不是整個資料庫的物件清單。
        var scopeColumns = _metadataService.GetCachedScopeColumns(scope.Tables);
        return builtIn.Concat(scopeColumns).Concat(database).ToArray();
    }

    private CompletionItem CreateItem(
        SqlSuggestion suggestion,
        SqlAssistSettings settings,
        SqlCompletionContext context)
    {
        var item = new CompletionItem(
            displayText: suggestion.DisplayText,
            source: this,
            icon: null!,
            filters: ImmutableArray<CompletionFilter>.Empty,
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
    /// 內建項目是否啟用。
    /// </summary>
    /// <remarks>
    /// 關鍵字建議不受「關鍵字自動大寫」影響：那個開關管的是輸入分隔字元時要不要
    /// 改寫已經打出來的字，與清單裡要不要列出 SELECT 是兩件事。
    /// </remarks>
    private static bool IsBuiltInEnabled(SqlSuggestion item, SqlAssistSettings settings)
    {
        return item.Kind switch
        {
            SuggestionKind.Snippet => settings.Features.TabExpansion,
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
