using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Connections;

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
    public static ITrackingSpan? TryCreateStatementSpan(
        SqlSuggestion selected,
        SqlCompletionContext context,
        ITextSnapshot snapshot,
        int caretPosition)
    {
        if (context.Intent != CompletionIntent.AlterDefinition ||
            context.TargetKeywordStart < 0 ||
            selected.Tag is not SqlObjectInfo objectInfo ||
            !objectInfo.Kind.IsModule() ||
            caretPosition < context.TargetKeywordStart)
        {
            return null;
        }

        return snapshot.CreateTrackingSpan(
            Span.FromBounds(context.TargetKeywordStart, caretPosition),
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
        var dispatcher = ResolveDispatcher();

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ReplaceWithScript(statementSpan, script, objectInfo)));
            return;
        }

        if (_textView.IsClosed)
        {
            return;
        }

        _setSuppressBufferChange?.Invoke(true);

        try
        {
            var buffer = _textView.TextBuffer;
            var target = statementSpan.GetSpan(buffer.CurrentSnapshot);

            using var edit = buffer.CreateEdit();
            edit.Replace(target, script);
            var updated = edit.Apply();

            if (edit.Canceled)
            {
                return;
            }

            var caretPosition = Math.Min(target.Start.Position + script.Length, updated.Length);
            _textView.Caret.MoveTo(new SnapshotPoint(updated, caretPosition));
            _textView.Caret.EnsureVisible();
            SqlAssistRuntimeState.MarkExpansion($"ALTER {objectInfo.QualifiedName}");
            SqlAssistDiagnostics.WriteAlways($"已展開 {objectInfo.QualifiedName} 的完整 ALTER 語句");
        }
        catch (Exception exception)
        {
            // 這裡是從背景工作回到 UI 執行緒後執行的，沒有其他人會接這個例外。
            SqlAssistDiagnostics.WriteAlways($"替換 ALTER 語句失敗：{exception}");
        }
        finally
        {
            _setSuppressBufferChange?.Invoke(false);
        }
    }

    private Dispatcher? ResolveDispatcher()
    {
        return _textView is IWpfTextView wpfTextView
            ? wpfTextView.VisualElement.Dispatcher
            : Application.Current?.Dispatcher;
    }
}
