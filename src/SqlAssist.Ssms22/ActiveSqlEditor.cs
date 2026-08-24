using System;
using Microsoft.VisualStudio.Text.Editor;

namespace SqlAssist.Ssms22;

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
        textView.GotAggregateFocus += OnGotAggregateFocus;
        textView.Closed += OnClosed;
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
