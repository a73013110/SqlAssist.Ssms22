using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 一份文字上的幾段命中，外加「現在停在第幾個」。
/// </summary>
/// <remarks>
/// 存在的理由是「上一個／下一個命中」那組操作有三件事會各寫一次就分岔：環繞（走到最後一個
/// 再按下一個要回到第一個）、空清單（按下去不能擲例外，也不能把索引留在 -1 以外）、以及
/// 「位置變了沒有」（沒變就不該重畫，也不該再捲一次）。三件事散到每一個呼叫端去，症狀是
/// 其中一個表面按到最後一個就不動了，而另一個會捲回文件開頭。
///
/// 與領域無關：只認得區段與索引，不知道那是 SQL、JSON 還是別的東西，
/// <c>Core/Matching</c> 的既有規則如此。<b>不可變</b>的是區段，游標本身會動——同一份命中
/// 會同時被清單、預覽與導覽讀到，而只有游標是這三者共用的那一個狀態。
///
/// 顯示的字不在這裡：<see cref="Position"/> 與 <see cref="Count"/> 是兩個數字，
/// 「3 / 7」這種寫法屬於呈現那一層。
/// </remarks>
public sealed class MatchCursor
{
    private static readonly MatchSpan[] NoSpans = Array.Empty<MatchSpan>();

    private readonly IReadOnlyList<MatchSpan> _spans;
    private int _current;

    /// <summary>空的游標；還沒有東西可以導覽時共用同一個實例。</summary>
    public static MatchCursor Empty { get; } = new(NoSpans);

    /// <param name="spans">命中區段，必須由小到大；空的代表沒有東西可以導覽。</param>
    /// <param name="current">起始位置；超出範圍時夾回第一個，不擲例外——區段是算出來的，
    /// 而算出幾段與要停在第幾個是兩輪各自的結果，夾回去比整組放棄誠實。</param>
    public MatchCursor(IReadOnlyList<MatchSpan> spans, int current = 0)
    {
        _spans = spans ?? throw new ArgumentNullException(nameof(spans));
        _current = _spans.Count == 0 ? -1 : Math.Min(Math.Max(current, 0), _spans.Count - 1);
    }

    public IReadOnlyList<MatchSpan> Spans => _spans;

    public int Count => _spans.Count;

    public bool IsEmpty => _spans.Count == 0;

    /// <summary>目前停在第幾個，由 0 起算；空的時候是 -1。</summary>
    public int Index => _current;

    /// <summary>目前停在第幾個，由 1 起算，給人看的；空的時候是 0。</summary>
    public int Position => _current + 1;

    /// <summary>目前那一段；空的時候是 null。</summary>
    public MatchSpan? Current => _current < 0 ? null : _spans[_current];

    /// <summary>往後一個，走到底回到第一個；位置真的變了才回 true。</summary>
    public bool MoveNext() => MoveTo(_current + 1);

    /// <summary>往前一個，走到頭回到最後一個；位置真的變了才回 true。</summary>
    public bool MovePrevious() => MoveTo(_current - 1);

    /// <summary>
    /// 跳到指定的一個；超出範圍會環繞。
    /// </summary>
    /// <remarks>
    /// 環繞而不是夾住：夾住的症狀是使用者按到最後一個之後，「下一個」那顆按鈕看起來壞了，
    /// 而他要的是回到第一個再看一遍。只有一段時環繞等於原地不動，所以回 false，
    /// 呼叫端不會為了同一個位置再捲一次。
    /// </remarks>
    public bool MoveTo(int index)
    {
        if (_spans.Count == 0) return false;

        var wrapped = index % _spans.Count;
        if (wrapped < 0) wrapped += _spans.Count;
        if (wrapped == _current) return false;

        _current = wrapped;
        return true;
    }
}
