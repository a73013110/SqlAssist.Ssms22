using System;
using Microsoft.VisualStudio.Text.Editor;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 記住最後取得焦點的 SQL 編輯器。
/// </summary>
/// <remarks>
/// 工具選單的命令是由套件層執行的，那裡拿不到編輯器。
/// 走 <c>IVsTextManager</c> 加上介面卡服務也可以取得，但那要多依賴一個組件，
/// 而 MEF 端本來就會被通知每個編輯器的建立與焦點變化，直接記下來成本更低。
/// </remarks>
internal static class ActiveSqlEditor
{
    private static readonly object SyncRoot = new();
    private static IWpfTextView? _current;
    private static IWpfTextView? _created;
    private static int _capturing;

    /// <summary>目前的 SQL 編輯器；沒有或已關閉時為 null。</summary>
    public static IWpfTextView? Current
    {
        get
        {
            lock (SyncRoot)
            {
                if (_current is { IsClosed: false })
                {
                    return _current;
                }

                _current = null;
                return null;
            }
        }
    }

    public static void Track(IWpfTextView textView)
    {
        if (textView is null)
        {
            return;
        }

        Set(textView);

        lock (SyncRoot)
        {
            // 只在擷取期間記住，否則這個欄位會一直握著最後建立的那個編輯器。
            if (_capturing > 0)
            {
                _created = textView;
            }
        }

        textView.GotAggregateFocus += OnGotAggregateFocus;
        textView.Closed += OnClosed;
    }

    /// <summary>
    /// 執行 <paramref name="open"/>，並取回它期間建立的 SQL 編輯器。
    /// </summary>
    /// <remarks>
    /// SSMS 的 <c>IScriptFactory</c> 開完新查詢視窗只回傳它自己的文件檢視型別，
    /// 從那裡拿不到 <see cref="IWpfTextView"/>。改用「開完之後誰是目前的編輯器」
    /// 則是猜的：那個值同時被建立與取得焦點兩件事寫入，開窗失敗時它仍然是<b>來源</b>
    /// 視窗，而把指令碼寫進來源視窗就是覆蓋使用者正在編輯的查詢。
    ///
    /// 這裡改成明確擷取：只認「這一次呼叫期間建立的那一個」，沒有就是沒有。
    /// 呼叫端因此不必再自己分辨拿到的是新視窗還是原來那個。
    ///
    /// 只在 UI 執行緒上呼叫——編輯器的建立本來就只發生在那裡，
    /// 巢狀呼叫（開窗的過程中又開一個窗）不存在。
    /// </remarks>
    /// <returns>期間建立的編輯器；沒有建立成功時為 null。</returns>
    public static IWpfTextView? CaptureCreated(Action open)
    {
        IWpfTextView? created;

        lock (SyncRoot)
        {
            _capturing++;
            _created = null;
        }

        try
        {
            open();
        }
        finally
        {
            // 收尾放在 finally 裡：open 丟出例外時也要把欄位清掉，
            // 否則它會一直握著那個編輯器直到下一次擷取。
            lock (SyncRoot)
            {
                _capturing--;
                created = _created;
                _created = null;
            }
        }

        return created is { IsClosed: false } ? created : null;
    }

    private static void OnGotAggregateFocus(object sender, EventArgs eventArgs)
    {
        if (sender is IWpfTextView textView)
        {
            Set(textView);
        }
    }

    private static void OnClosed(object sender, EventArgs eventArgs)
    {
        if (sender is not IWpfTextView textView)
        {
            return;
        }

        textView.GotAggregateFocus -= OnGotAggregateFocus;
        textView.Closed -= OnClosed;

        lock (SyncRoot)
        {
            if (ReferenceEquals(_current, textView))
            {
                _current = null;
            }
        }
    }

    private static void Set(IWpfTextView textView)
    {
        lock (SyncRoot)
        {
            _current = textView;
        }
    }
}
