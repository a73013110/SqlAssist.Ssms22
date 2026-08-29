using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 把已插入的模組名稱換成可直接執行的完整 ALTER 語句。
/// </summary>
/// <remarks>
/// 使用者輸入 <c>ap</c> 展開成 <c>ALTER PROCEDURE</c> 之後選了某個程序，想要的是
/// 可以立刻修改並執行的完整定義，而不是只把名稱補上去。
///
/// 定義屬於中繼資料的第三層，取得需要另一次查詢，期間使用者仍可能編輯緩衝區，
/// 因此要替換的範圍以 <see cref="ITrackingSpan"/> 記住，不能用固定位置。
/// 兩種建議引擎共用這個流程。
/// </remarks>
internal sealed class SqlModuleExpander
{
    private readonly ITextView _textView;
    private readonly SqlMetadataService _metadataService;
    private readonly Action<bool>? _setSuppressBufferChange;

    public SqlModuleExpander(
        ITextView textView,
        SqlMetadataService metadataService,
        Action<bool>? setSuppressBufferChange = null)
    {
        _textView = textView;
        _metadataService = metadataService;
        _setSuppressBufferChange = setSuppressBufferChange;
    }

    /// <summary>判斷這次提交是否應該展開成完整的 ALTER 語句。</summary>
    public static bool ShouldExpand(
        SqlSuggestion selected,
        SqlCompletionContext context,
        int caretPosition)
    {
        return context.Intent == CompletionIntent.AlterDefinition &&
            context.TargetKeywordStart >= 0 &&
            selected.Tag is SqlObjectInfo objectInfo &&
            objectInfo.Kind.IsModule() &&
            caretPosition >= context.TargetKeywordStart;
    }

    /// <summary>記住要被完整定義換掉的那一段。</summary>
    /// <remarks>
    /// **一定要在名稱插進緩衝區之後才呼叫**，範圍的結尾也必須是插入後的實際結尾。
    ///
    /// 曾經在提交前就用「關鍵字起點 → 游標」建好這個範圍，結果是展開後編輯器裡多出
    /// 一截 <c>dbo.uspFoo</c>：<see cref="SpanTrackingMode.EdgeExclusive"/> 的結尾邊界
    /// 往負方向追蹤，而提交的取代動作剛好就結束在那個邊界上，於是新插入的名稱被判在
    /// 範圍**外**。範圍縮回「ALTER PROCEDURE 」，定義蓋掉的只有這一段，名稱原封不動
    /// 留在後面——正好停在游標之後，看起來就像展開完又補了一次名稱。
    ///
    /// 結尾邊界改追蹤正方向可以讓這一次對，但接下來使用者在游標處打的每一個字也會被
    /// 算進範圍裡，定義回來時連同打好的字一起被吃掉。所以維持 EdgeExclusive，
    /// 改成在編輯之後才建立範圍。
    /// </remarks>
    public static ITrackingSpan CreateStatementSpan(
        ITextSnapshot snapshot,
        int statementStart,
        int statementEnd)
    {
        return snapshot.CreateTrackingSpan(
            Span.FromBounds(statementStart, Math.Min(statementEnd, snapshot.Length)),
            SpanTrackingMode.EdgeExclusive);
    }

    /// <summary>在背景取得定義並替換整個語句。</summary>
    public void Begin(SqlObjectInfo objectInfo, ITrackingSpan statementSpan)
    {
        _ = ExpandAsync(objectInfo, statementSpan);
    }

    private async Task ExpandAsync(SqlObjectInfo objectInfo, ITrackingSpan statementSpan)
    {
        try
        {
            var detail = await _metadataService
                .GetDetailAsync(objectInfo, CancellationToken.None)
                .ConfigureAwait(false);

            if (detail?.Definition is not { } definition)
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"無法取得 {objectInfo.QualifiedName} 的定義，維持只插入名稱");
                return;
            }

            if (!SqlModuleScript.TryConvertCreateToAlter(definition, out var script))
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"{objectInfo.QualifiedName} 的定義不是 CREATE 開頭，維持只插入名稱");
                return;
            }

            ReplaceWithScript(statementSpan, script, objectInfo);
        }
        catch (OperationCanceledException)
        {
            // 編輯器已關閉。
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"展開 ALTER 語句失敗：{exception}");
        }
    }

    private void ReplaceWithScript(ITrackingSpan statementSpan, string script, SqlObjectInfo objectInfo)
    {
        new TextViewEditCoordinator(_textView).ReplaceTracked(
            statementSpan,
            "ALTER 語句",
            target => BuildReplacement(target, script, objectInfo),
            _setSuppressBufferChange);
    }

    /// <remarks>
    /// 查詢期間使用者可能已經把整句刪掉或改寫了。範圍的起點就是 ALTER 那個字，
    /// 起點不再是 ALTER 就代表要換的東西已經不在原處——這時候把定義蓋上去
    /// 等於改到別人的語句。萬用字元展開走的是同一條防線。
    /// </remarks>
    private static TextReplacement? BuildReplacement(
        SnapshotSpan target,
        string script,
        SqlObjectInfo objectInfo)
    {
        if (target.IsEmpty || !StartsWithAlter(target.GetText()))
        {
            SqlAssistDiagnostics.WriteAlways("要展開的 ALTER 語句已經不在原處，放棄這次展開");
            return null;
        }

        return new TextReplacement(
            script,
            $"ALTER {objectInfo.QualifiedName}",
            $"已展開 {objectInfo.QualifiedName} 的完整 ALTER 語句");
    }

    private static bool StartsWithAlter(string text)
    {
        return text.TrimStart().StartsWith("ALTER", StringComparison.OrdinalIgnoreCase);
    }
}
