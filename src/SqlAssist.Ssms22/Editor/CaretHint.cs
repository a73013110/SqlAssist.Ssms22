using System;
using System.ComponentModel;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Editor;

/// <summary>提示要貼在哪一段文字旁邊、寫什麼。</summary>
internal readonly struct CaretHintContent
{
    public CaretHintContent(SnapshotSpan anchor, string text)
    {
        Anchor = anchor;
        Text = text;
    }

    public SnapshotSpan Anchor { get; }

    public string Text { get; }
}

/// <summary>
/// 跟著游標出現與收起的提示：顯示與否只由「游標現在所在的情境」決定。
/// </summary>
/// <remarks>
/// 呼叫端只回答「游標在這裡該不該提示、提示什麼」；什麼時候要重新問、怎麼顯示與收起，
/// 全部在這裡。重新問的時機是游標情境可能變了的每一種來源：
/// <list type="bullet">
/// <item>游標被明確移動。</item>
/// <item>文字變了。<see cref="ITextCaret.PositionChanged"/> 在游標跟著編輯位移時<b>不發</b>
/// ——打字、刪字、復原、背景展開寫回都屬於這一種。只聽游標事件的提示在按 Tab 展開之後
/// 會一直留著，而打出 <c>*</c> 時又不會出現。</item>
/// <item>建議清單開或關：清單開著時讓位，兩個小視窗貼在同一行旁邊只會互相擋住。</item>
/// <item>設定變了、編輯器取得或失去焦點。</item>
/// </list>
///
/// 每一種來源都只排一次判斷到本輪命令之後，同一次按鍵觸發的多個事件併成一次：
/// 當下的文字、游標與 session 都還沒定案，原地判斷看到的是上一個狀態。
///
/// 提示用編輯器自己的 <see cref="IToolTipPresenter"/>，與滑鼠停留提示同一套：
/// 定位、螢幕邊界、佈景主題與字型都由編輯器負責，也不會搶走鍵盤焦點。
/// 平台的自動關閉規則是為滑鼠停留寫的，這裡全部關掉，收起的時機只由上面那份判斷決定。
/// </remarks>
internal sealed class CaretHint
{
    private readonly IWpfTextView _textView;
    private readonly IAsyncCompletionBroker? _broker;
    private readonly IToolTipPresenterFactory _presenterFactory;
    private readonly string _operation;
    private readonly Func<SnapshotPoint, CaretHintContent?> _evaluate;

    private IToolTipPresenter? _presenter;
    private int _scheduled;

    private CaretHint(
        IWpfTextView textView,
        IAsyncCompletionBroker? broker,
        IToolTipPresenterFactory presenterFactory,
        string operation,
        Func<SnapshotPoint, CaretHintContent?> evaluate)
    {
        _textView = textView;
        _broker = broker;
        _presenterFactory = presenterFactory;
        _operation = operation;
        _evaluate = evaluate;
    }

    /// <param name="operation">失敗時寫進紀錄的操作名稱，例如「更新萬用字元提示」。</param>
    /// <param name="evaluate">
    /// 游標在這個位置該顯示什麼；回傳 null 代表不提示。每次游標移動或按鍵都會呼叫，
    /// 必須先用便宜的檢查擋掉絕大多數位置，而且不得查詢資料庫。
    /// </param>
    /// <remarks>
    /// 取不到提示視窗的工廠就整個不接：功能本身仍然可用，只是沒有提示。
    /// 為了一個提示讓編輯器初始化失敗不值得。
    /// </remarks>
    public static void Attach(
        IWpfTextView textView,
        IAsyncCompletionBroker? broker,
        IToolTipPresenterFactory? presenterFactory,
        [Localizable(false)] string operation,
        Func<SnapshotPoint, CaretHintContent?> evaluate)
    {
        if (textView is null || presenterFactory is null || evaluate is null)
        {
            return;
        }

        var hint = new CaretHint(textView, broker, presenterFactory, operation, evaluate);
        textView.Caret.PositionChanged += hint.OnChanged;
        textView.TextBuffer.Changed += hint.OnChanged;
        textView.GotAggregateFocus += hint.OnChanged;
        textView.LostAggregateFocus += hint.OnChanged;
        textView.Closed += hint.OnTextViewClosed;
        SqlAssistSettingsStore.Changed += hint.OnChanged;

        if (broker is not null)
        {
            broker.CompletionTriggered += hint.OnCompletionTriggered;
        }
    }

    private void OnChanged(object? sender, EventArgs eventArgs) => Schedule();

    private void OnCompletionTriggered(object sender, CompletionTriggeredEventArgs eventArgs)
    {
        // broker 層級的事件，其他編輯器的清單也會通知到這裡。
        if (!ReferenceEquals(eventArgs.TextView, _textView))
        {
            return;
        }

        // 收起時也要重問一次：清單關掉之後游標常常還停在原處，不會有游標事件。
        eventArgs.CompletionSession.Dismissed += OnSessionDismissed;
        Schedule();
    }

    private void OnSessionDismissed(object sender, EventArgs eventArgs)
    {
        ((IAsyncCompletionSession)sender).Dismissed -= OnSessionDismissed;
        Schedule();
    }

    /// <remarks>設定變更可能從背景執行緒通知，旗標要能跨執行緒併掉重複的排程。</remarks>
    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
        {
            return;
        }

        TextViewDispatch.AfterCurrentCommand(_textView, _operation, _ =>
        {
            Interlocked.Exchange(ref _scheduled, 0);
            Update();
        });
    }

    private void Update()
    {
        if (!_textView.HasAggregateFocus || _broker?.GetSession(_textView) is not null)
        {
            Hide();
            return;
        }

        if (_evaluate(_textView.Caret.Position.BufferPosition) is not { } content)
        {
            Hide();
            return;
        }

        Show(content);
    }

    private void Show(CaretHintContent content)
    {
        if (_presenter is null)
        {
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
            content.Anchor.Snapshot.CreateTrackingSpan(content.Anchor, SpanTrackingMode.EdgeExclusive),
            new object[]
            {
                new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, content.Text))
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

    private void OnTextViewClosed(object sender, EventArgs eventArgs)
    {
        Hide();
        _textView.Caret.PositionChanged -= OnChanged;
        _textView.TextBuffer.Changed -= OnChanged;
        _textView.GotAggregateFocus -= OnChanged;
        _textView.LostAggregateFocus -= OnChanged;
        _textView.Closed -= OnTextViewClosed;
        SqlAssistSettingsStore.Changed -= OnChanged;

        if (_broker is not null)
        {
            _broker.CompletionTriggered -= OnCompletionTriggered;
        }
    }
}
