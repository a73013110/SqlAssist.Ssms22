using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using SqlAssist.Core;

namespace SqlAssist.Ssms22;

internal sealed class SqlCompletionController : IDisposable
{
    private static readonly IReadOnlyList<SqlSuggestion> BuiltInSuggestions =
        BuiltInSuggestionCatalog.Create();

    private readonly SqlMetadataProvider _metadataProvider;
    private readonly ICompletionBroker _nativeCompletionBroker;
    private readonly SuggestionPopupControl _popup;
    private readonly DispatcherTimer _refreshTimer;
    private readonly IWpfTextView _textView;
    private IReadOnlyList<SqlSuggestion> _databaseSuggestions = Array.Empty<SqlSuggestion>();
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
        _metadataProvider = new SqlMetadataProvider(serviceProvider);
        _popup = new SuggestionPopupControl(textView, () => CommitSelected());
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
        }
        finally
        {
            _suppressBufferChange = false;
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
        var matches = SuggestionMatcher.Match(candidates, context, maximumItems);

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
            _databaseSuggestions = await _metadataProvider.GetSuggestionsAsync(default);
            if (!_disposed)
            {
                RefreshNow();
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"建議資料載入失敗：{exception.Message}");
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
