using System;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 游標停在展得開的 <c>*</c> 後方時，在旁邊提示可以按 Tab。
/// </summary>
/// <remarks>
/// 這個功能沒有提示等於不存在：使用者不會憑空去試按 Tab，而按下去之前也看不出
/// 這一次到底展不展得開——衍生資料表少寫一個別名、CTE 裡有個沒有名稱的運算式，
/// 都會讓同一個星號變成展不開的。提示出現與否就是那份判斷的結果。
///
/// 提示用編輯器自己的 <see cref="IToolTipPresenter"/>，與滑鼠停留提示同一套：
/// 定位、螢幕邊界、佈景主題與字型都由編輯器負責，也不會搶走鍵盤焦點。
/// 出現與消失則完全由這裡決定，不交給平台的自動關閉規則——那套規則是為滑鼠停留
/// 寫的，而這個提示跟著的是游標。
/// </remarks>
internal sealed class SqlWildcardHint
{
    private const string HintText = "按 Tab 展開所有欄位";

    private readonly IWpfTextView _textView;
    private readonly IAsyncCompletionBroker? _broker;
    private readonly IToolTipPresenterFactory _presenterFactory;

    private IToolTipPresenter? _presenter;

    private SqlWildcardHint(
        IWpfTextView textView,
        IAsyncCompletionBroker? broker,
        IToolTipPresenterFactory presenterFactory)
    {
        _textView = textView;
        _broker = broker;
        _presenterFactory = presenterFactory;
    }

    /// <remarks>
    /// 取不到提示視窗的工廠就整個不接：展開本身仍然可用，只是沒有提示。
    /// 為了一個提示讓編輯器初始化失敗不值得。
    /// </remarks>
    public static void Attach(
        IWpfTextView textView,
        IAsyncCompletionBroker? broker,
        IToolTipPresenterFactory? presenterFactory)
    {
        if (textView is null || presenterFactory is null)
        {
            return;
        }

        var hint = new SqlWildcardHint(textView, broker, presenterFactory);
        textView.Caret.PositionChanged += hint.OnCaretPositionChanged;
        textView.LostAggregateFocus += hint.OnLostFocus;
        textView.Closed += hint.OnTextViewClosed;
    }

    private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs eventArgs)
    {
        try
        {
            Update(eventArgs.NewPosition.BufferPosition);
        }
        catch (Exception exception)
        {
            // 這是游標移動的事件處理常式，丟出去就是使用者眼前的錯誤對話框。
            SqlAssistDiagnostics.WriteAlways($"更新萬用字元提示失敗：{exception.Message}");
        }
    }

    /// <remarks>
    /// 展開之後不必特別收提示：文字換掉的同時游標也移走了，
    /// 這裡會照常判斷出「游標前面已經不是星號」而收掉。
    /// </remarks>
    private void Update(SnapshotPoint caret)
    {
        // 建議清單開著時讓位：兩個小視窗同時貼在同一行旁邊只會互相擋住。
        if (_textView.IsClosed || _broker?.GetSession(_textView) is not null)
        {
            Hide();
            return;
        }

        var target = SqlWildcardExpander.Find(
            caret.Snapshot,
            caret.Position,
            SqlAssistSettingsStore.Current);

        if (target is null)
        {
            Hide();
            return;
        }

        Show(caret.Snapshot.CreateTrackingSpan(
            new Span(target.Start, target.Length),
            SpanTrackingMode.EdgeExclusive));
    }

    private void Show(ITrackingSpan span)
    {
        if (_presenter is null)
        {
            // 緩衝區變更與游標移動都交給自己管：平台那套規則是為滑鼠停留寫的，
            // 而收掉的時機在 Update 裡已經決定好了。
            _presenter = _presenterFactory.Create(
                _textView,
                new ToolTipParameters(
                    trackMouse: false,
                    ignoreBufferChange: true,
                    keepOpenFunc: () => false,
                    ignoreCaretPositionChange: true,
                    dismissWhenOffscreen: true));

            _presenter.Dismissed += OnPresenterDismissed;
        }

        _presenter.StartOrUpdate(
            span,
            new object[]
            {
                new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, HintText))
            });
    }

    private void Hide()
    {
        _presenter?.Dismiss();
    }

    /// <summary>收掉之後那個 presenter 就不再使用，下一次重新建一個。</summary>
    private void OnPresenterDismissed(object sender, EventArgs eventArgs)
    {
        if (_presenter is { } presenter)
        {
            presenter.Dismissed -= OnPresenterDismissed;
            _presenter = null;
        }
    }

    private void OnLostFocus(object sender, EventArgs eventArgs)
    {
        Hide();
    }

    private void OnTextViewClosed(object sender, EventArgs eventArgs)
    {
        Hide();
        _textView.Caret.PositionChanged -= OnCaretPositionChanged;
        _textView.LostAggregateFocus -= OnLostFocus;
        _textView.Closed -= OnTextViewClosed;
    }
}
