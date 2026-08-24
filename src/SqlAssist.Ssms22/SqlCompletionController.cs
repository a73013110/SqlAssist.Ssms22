using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using SqlAssist.Core;
using SqlAssist.Core.Matching;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22;

internal sealed class SqlCompletionController : IDisposable
{
    private static readonly IReadOnlyList<SqlSuggestion> BuiltInSuggestions =
        BuiltInSuggestionCatalog.Create();

    private readonly SqlMetadataService _metadataService;
    private readonly ICompletionBroker _nativeCompletionBroker;
    private readonly SuggestionPopupControl _popup;
    private readonly DispatcherTimer _refreshTimer;
    private readonly IWpfTextView _textView;
    private IReadOnlyList<SqlSuggestion> _databaseSuggestions = Array.Empty<SqlSuggestion>();
    private CancellationTokenSource? _previewCancellation;
    private bool _disposed;
    private bool _metadataLoadStarted;
    private bool _suppressBufferChange;

    public SqlCompletionController(
        IWpfTextView textView,
        IServiceProvider serviceProvider,
        ICompletionBroker nativeCompletionBroker)
    {
        _textView = textView;
        _nativeCompletionBroker = nativeCompletionBroker;
        _metadataService = new SqlMetadataService(serviceProvider);
        _popup = new SuggestionPopupControl(textView, () => CommitSelected());
        _popup.SelectionChanged += OnPopupSelectionChanged;
        _refreshTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            textView.VisualElement.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _textView.TextBuffer.Changed += OnBufferChanged;
        _textView.Caret.PositionChanged += OnCaretPositionChanged;
        _textView.LayoutChanged += OnLayoutChanged;
        _textView.LostAggregateFocus += OnLostAggregateFocus;
        _textView.Closed += OnTextViewClosed;
        SuggestionRefreshBroker.RefreshRequested += OnRefreshRequested;
    }

    public bool IsOpen => _popup.IsOpen;

    public bool CommitSelected()
    {
        if (!_popup.IsOpen || _popup.SelectedSuggestion is not { } selected)
        {
            return false;
        }

        var caret = _textView.Caret.Position.BufferPosition;
        var textBeforeCaret = caret.Snapshot.GetText(0, caret.Position);
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        if (!context.IsValid)
        {
            Hide();
            return false;
        }

        var settings = SettingsService.Default.GetSnapshot();
        var insertionText = GetInsertionText(selected, context, settings);
        ModuleExpansion? moduleExpansion = null;
        _suppressBufferChange = true;

        try
        {
            using var edit = caret.Snapshot.TextBuffer.CreateEdit();
            edit.Replace(context.TokenStart, caret.Position - context.TokenStart, insertionText);
            var snapshot = edit.Apply();

            if (edit.Canceled)
            {
                return false;
            }

            var newPosition = Math.Min(context.TokenStart + insertionText.Length, snapshot.Length);
            _textView.Caret.MoveTo(new SnapshotPoint(snapshot, newPosition));
            _textView.Caret.EnsureVisible();
            SqlAssistRuntimeState.MarkExpansion(insertionText.TrimEnd());
            SqlAssistDiagnostics.Write($"Suggestion 已提交：{selected.DisplayText}", _textView);
            moduleExpansion = TryBeginModuleExpansion(selected, context, snapshot, newPosition);
        }
        finally
        {
            _suppressBufferChange = false;
        }

        if (moduleExpansion is not null)
        {
            Hide();
            _ = ExpandModuleAsync(moduleExpansion.Value.ObjectInfo, moduleExpansion.Value.StatementSpan);
            return true;
        }

        if (selected.TriggerFollowUp)
        {
            Hide();
            SqlAssistDiagnostics.WriteAlways(
                $"已進入接續建議：{selected.DisplayText}，下一步只顯示對應資料庫物件",
                _textView);
            _textView.VisualElement.Dispatcher.BeginInvoke(
                new Action(RefreshNow),
                DispatcherPriority.Background);
        }
        else
        {
            Hide();
        }

        return true;
    }

    /// <summary>
    /// 判斷這次提交是否應該展開成完整的 ALTER 語句。
    /// </summary>
    /// <remarks>
    /// 使用者輸入 ap 展開成 ALTER PROCEDURE 之後選了某個程序，想要的是可以直接
    /// 修改並執行的完整定義，而不是只把名稱補上去。這裡只回報「要展開」與
    /// 「要替換哪一段」，實際的定義載入在背景進行。
    /// </remarks>
    private ModuleExpansion? TryBeginModuleExpansion(
        SqlSuggestion selected,
        SqlCompletionContext context,
        ITextSnapshot snapshot,
        int caretPosition)
    {
        if (context.Target is not (CompletionTarget.Procedure or CompletionTarget.Function) ||
            context.TargetKeywordStart < 0 ||
            selected.Tag is not SqlObjectInfo objectInfo ||
            !objectInfo.Kind.IsModule())
        {
            return null;
        }

        // 以追蹤範圍記住整個 ALTER 語句：定義是非同步取得的，
        // 期間使用者仍可能編輯緩衝區，用固定位置會替換到錯誤的地方。
        var statementSpan = snapshot.CreateTrackingSpan(
            Span.FromBounds(context.TargetKeywordStart, caretPosition),
            SpanTrackingMode.EdgeExclusive);

        return new ModuleExpansion(objectInfo, statementSpan);
    }

    private async Task ExpandModuleAsync(SqlObjectInfo objectInfo, ITrackingSpan statementSpan)
    {
        try
        {
            var detail = await _metadataService.GetDetailAsync(objectInfo, CancellationToken.None);

            if (_disposed || detail?.Definition is not { } definition)
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
            SqlAssistDiagnostics.WriteAlways($"展開 ALTER 語句失敗：{exception.Message}");
        }
    }

    private void ReplaceWithScript(ITrackingSpan statementSpan, string script, SqlObjectInfo objectInfo)
    {
        var dispatcher = _textView.VisualElement.Dispatcher;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ReplaceWithScript(statementSpan, script, objectInfo)));
            return;
        }

        if (_disposed || _textView.IsClosed)
        {
            return;
        }

        _suppressBufferChange = true;

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
            SqlAssistDiagnostics.WriteAlways($"已展開 {objectInfo.QualifiedName} 的完整 ALTER 語句", _textView);
        }
        finally
        {
            _suppressBufferChange = false;
        }
    }

    /// <summary>待展開的 ALTER 語句：要展開的物件，以及要被取代的那一段。</summary>
    private readonly struct ModuleExpansion
    {
        public ModuleExpansion(SqlObjectInfo objectInfo, ITrackingSpan statementSpan)
        {
            ObjectInfo = objectInfo;
            StatementSpan = statementSpan;
        }

        public SqlObjectInfo ObjectInfo { get; }

        public ITrackingSpan StatementSpan { get; }
    }

    public bool MoveSelection(int delta)
    {
        if (!_popup.IsOpen)
        {
            return false;
        }

        _popup.MoveSelection(delta);
        return true;
    }

    public bool Hide()
    {
        if (!_popup.IsOpen)
        {
            return false;
        }

        _popup.Hide();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        _popup.SelectionChanged -= OnPopupSelectionChanged;
        _metadataService.Dispose();
        _textView.TextBuffer.Changed -= OnBufferChanged;
        _textView.Caret.PositionChanged -= OnCaretPositionChanged;
        _textView.LayoutChanged -= OnLayoutChanged;
        _textView.LostAggregateFocus -= OnLostAggregateFocus;
        _textView.Closed -= OnTextViewClosed;
        SuggestionRefreshBroker.RefreshRequested -= OnRefreshRequested;
        _popup.Hide();
        CompletionSessionRegistry.Remove(_textView);
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs eventArgs)
    {
        if (!_suppressBufferChange)
        {
            ScheduleRefresh(); // 每次輸入字元或退格後重新篩選，不需要先按 Tab。
        }
    }

    private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs eventArgs)
    {
        if (_popup.IsOpen)
        {
            _popup.Reposition(_textView);
        }
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs eventArgs)
    {
        if (_popup.IsOpen)
        {
            _popup.Reposition(_textView);
        }
    }

    private void OnLostAggregateFocus(object sender, EventArgs eventArgs)
    {
        Hide();
    }

    private void OnTextViewClosed(object sender, EventArgs eventArgs)
    {
        Dispose();
    }

    private void OnRefreshRequested(object? sender, EventArgs eventArgs)
    {
        _databaseSuggestions = Array.Empty<SqlSuggestion>();
        _metadataLoadStarted = false;
        RefreshNow();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs eventArgs)
    {
        _refreshTimer.Stop();
        RefreshNow();
    }

    private void ScheduleRefresh()
    {
        var delay = SettingsService.Default.GetSnapshot().Suggestions.DelayMilliseconds;
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(1000, delay)));
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void RefreshNow()
    {
        if (_disposed || !_textView.Selection.IsEmpty)
        {
            Hide();
            return;
        }

        var settings = SettingsService.Default.GetSnapshot();

        if (!settings.Enabled || !settings.Suggestions.Enabled)
        {
            Hide();
            return;
        }

        var caret = _textView.Caret.Position.BufferPosition;
        var textBeforeCaret = caret.Snapshot.GetText(0, caret.Position);
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        if (!context.IsValid)
        {
            Hide();
            return;
        }

        var triggerCharacters = Math.Max(1, Math.Min(10, settings.Suggestions.TriggerAfterCharacters));

        if (context.Target == CompletionTarget.Any &&
            context.SchemaQualifier is null &&
            context.Prefix.Length < triggerCharacters)
        {
            Hide();
            return;
        }

        var candidates = BuiltInSuggestions
            .Where(item => IsBuiltInFeatureEnabled(item, settings))
            .Concat(settings.Features.ObjectPicker ? _databaseSuggestions : Array.Empty<SqlSuggestion>());
        var maximumItems = Math.Max(1, Math.Min(500, settings.Suggestions.MaximumItems));
        var matches = SuggestionMatcher.Rank(candidates, context, maximumItems);

        if (matches.Count == 0)
        {
            Hide();
        }
        else
        {
            DismissNativeCompletion(); // 自製清單顯示時，關閉 SSMS 原生 IntelliSense 清單。
            _popup.Show(matches, _textView, settings.Suggestions.ShowPreview);
        }

        if (settings.Features.ObjectPicker && !_metadataLoadStarted)
        {
            _metadataLoadStarted = true;
            _ = LoadMetadataAsync();
        }
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            _databaseSuggestions = await _metadataService.GetSuggestionsAsync(CancellationToken.None);
            SqlAssistDiagnostics.WriteAlways($"已載入 {_databaseSuggestions.Count} 筆資料庫物件建議", _textView);

            if (!_disposed)
            {
                RefreshNow();
            }
        }
        catch (OperationCanceledException)
        {
            // 編輯器關閉時取消屬於正常流程。
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"建議資料載入失敗：{exception.Message}");
        }
    }

    /// <summary>
    /// 選取項改變時補上預覽內容。
    /// </summary>
    /// <remarks>
    /// 欄位與定義是中繼資料的第二、三層，只在這裡按需載入；
    /// 若改成建立建議時就一併帶齊，開啟編輯器就得把整個資料庫的定義本文拉回來。
    /// </remarks>
    private void OnPopupSelectionChanged(object? sender, EventArgs eventArgs)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;

        if (_disposed ||
            _popup.SelectedSuggestion is not { } selected ||
            selected.Tag is not SqlObjectInfo objectInfo)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _ = LoadPreviewAsync(selected, objectInfo, cancellation.Token);
    }

    private async Task LoadPreviewAsync(
        SqlSuggestion suggestion,
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _metadataService.GetDetailAsync(objectInfo, cancellationToken);

            if (detail is null || cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            _popup.TrySetPreviewBody(suggestion, detail.BuildPreview());
        }
        catch (OperationCanceledException)
        {
            // 使用者已經移動到別的項目。
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"載入物件明細失敗：{exception.Message}");
        }
    }

    private static bool IsBuiltInFeatureEnabled(SqlSuggestion item, SqlAssistSettings settings)
    {
        return item.Kind switch
        {
            SuggestionKind.Snippet => settings.Features.TabExpansion,
            SuggestionKind.Keyword => settings.Features.KeywordUppercase,
            _ => true
        };
    }

    private void DismissNativeCompletion()
    {
        try
        {
            if (_nativeCompletionBroker.IsCompletionActive(_textView))
            {
                _nativeCompletionBroker.DismissAllSessions(_textView);
                SqlAssistDiagnostics.Write("已關閉 SSMS 原生 Completion Session", _textView);
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"關閉 SSMS 原生建議失敗：{exception.Message}");
        }
    }

    private static string GetInsertionText(
        SqlSuggestion suggestion,
        SqlCompletionContext context,
        SqlAssistSettings settings)
    {
        if (suggestion.Kind == SuggestionKind.Keyword || suggestion.Kind == SuggestionKind.Snippet)
        {
            return suggestion.InsertionText;
        }

        var objectName = settings.Suggestions.UseSquareBrackets
            ? QuoteIdentifier(suggestion.DisplayText)
            : suggestion.DisplayText;

        if (suggestion.Kind == SuggestionKind.Schema)
        {
            return objectName + ".";
        }

        if (context.SchemaQualifier is not null ||
            !settings.Suggestions.QualifyObjectNames ||
            string.IsNullOrWhiteSpace(suggestion.SchemaName))
        {
            return objectName;
        }

        var schemaName = settings.Suggestions.UseSquareBrackets
            ? QuoteIdentifier(suggestion.SchemaName!)
            : suggestion.SchemaName;
        return schemaName + "." + objectName;
    }

    private static string QuoteIdentifier(string name)
    {
        return "[" + name.Replace("]", "]]") + "]";
    }
}
