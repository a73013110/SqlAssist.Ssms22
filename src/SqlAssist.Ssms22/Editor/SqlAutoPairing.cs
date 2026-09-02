using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Pairing;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 輸入 <c>(</c>、<c>'</c>、<c>[</c>、<c>"</c> 時補上另一半，並在收掉時對稱地拿走。
/// </summary>
/// <remarks>
/// 什麼時候該配對全部在 <see cref="SqlAutoPairAnalyzer"/>（只看文字，測得到）；
/// 這裡只做編輯器那一半——寫緩衝區、擺游標，以及記住<b>哪一個結尾字元是我補的</b>。
///
/// 最後那一件事沒有辦法從文字判斷，卻是這個功能能不能用的關鍵：
/// 少了它，游標停在使用者自己打的 <c>)</c> 前面時就再也插不進一個右括號，
/// 而 <c>'abc|'</c> 想補一個跳脫用的單引號會變成跳過去。因此每補一個結尾字元就
/// 記一個 <see cref="ITrackingPoint"/>，只有記錄裡的那一個才跳過、才一起刪。
///
/// 補結尾字元刻意<b>不</b>連同開頭字元一起插入：只插結尾字元、把游標留在它前面，
/// 開頭字元仍由編輯器自己插入。這樣選取取代、覆寫模式與虛擬空白都還是平台的行為，
/// 不必在這裡重寫一份。代價是 Ctrl+Z 要按兩次才連補上的字元一起收掉——
/// 換成自己插入兩個字元可以合成一次復原，但要接管的東西比省下的那一次多得多。
/// </remarks>
internal static class SqlAutoPairing
{
    /// <summary>
    /// 使用者即將輸入 <paramref name="typedCharacter"/>。
    /// </summary>
    /// <returns>
    /// <c>true</c> 代表這次按鍵已經處理完（跳過結尾字元、或包夾了選取範圍），
    /// 呼叫端要吞掉它；<c>false</c> 代表字元仍由編輯器插入——補上結尾字元也走這一條。
    /// </returns>
    public static bool TryHandleTypedCharacter(
        ITextView textView,
        ITextBuffer buffer,
        char typedCharacter)
    {
        // 第一道篩選只看字元本身：打字時絕大多數按鍵在這裡就結束，
        // 連游標、選取範圍與設定都不必問。
        if (!SqlDelimiterPairs.IsPairCharacter(typedCharacter) ||
            !TryGetCaret(textView, buffer, out var caret))
        {
            return false;
        }

        if (!textView.Selection.IsEmpty)
        {
            return TrySurroundSelection(textView, buffer, typedCharacter);
        }

        var snapshot = caret.Snapshot;
        var source = new SnapshotTextSource(snapshot);
        var tracker = SqlAutoPairTracker.Get(textView);

        if (SqlAutoPairAnalyzer.ShouldOvertype(source, caret.Position, typedCharacter) &&
            tracker.TryTake(snapshot, caret.Position, typedCharacter))
        {
            textView.Caret.MoveTo(new SnapshotPoint(snapshot, caret.Position + 1));
            textView.Caret.EnsureVisible();
            SqlAssistDiagnostics.Write($"跳過自動補上的 {typedCharacter}", textView);
            return true;
        }

        if (SqlAutoPairAnalyzer.AutoCloseFor(source, caret.Position, typedCharacter) is not char close)
        {
            return false;
        }

        InsertClose(textView, buffer, caret.Position, close, tracker);
        return false;
    }

    /// <summary>
    /// Backspace 落在一對空的配對中間時，兩邊一起刪。
    /// </summary>
    /// <returns><c>true</c> 代表已經刪掉整對，呼叫端要吞掉這次按鍵。</returns>
    /// <remarks>
    /// 一次編輯刪掉兩個字元，所以 Ctrl+Z 一次就還原——與補上時要按兩次不對稱，
    /// 但這裡沒有平台的那一半要等，能合就合。
    /// </remarks>
    public static bool TryHandleBackspace(ITextView textView, ITextBuffer buffer)
    {
        if (!TryGetCaret(textView, buffer, out var caret) || !textView.Selection.IsEmpty)
        {
            return false;
        }

        var snapshot = caret.Snapshot;

        if (!SqlAutoPairAnalyzer.IsEmptyPair(new SnapshotTextSource(snapshot), caret.Position))
        {
            return false;
        }

        // 只收自己補的那一個：使用者手打的 () 按 Backspace 應該只刪掉左括號。
        if (!SqlAutoPairTracker.Get(textView).TryTake(snapshot, caret.Position, snapshot[caret.Position]))
        {
            return false;
        }

        using var edit = buffer.CreateEdit();
        edit.Delete(caret.Position - 1, 2);

        if (edit.Apply() is null || edit.Canceled)
        {
            return false;
        }

        SqlAssistDiagnostics.Write("刪掉整對自動補上的分隔字元", textView);
        return true;
    }

    /// <summary>把選取範圍包在一對分隔字元裡，並讓它保持選取。</summary>
    /// <remarks>
    /// 保持選取是為了能連包兩層（先 <c>'</c> 再 <c>(</c>），也讓包錯時一次 Ctrl+Z
    /// 就回到原本的選取狀態。
    /// </remarks>
    private static bool TrySurroundSelection(
        ITextView textView,
        ITextBuffer buffer,
        char typedCharacter)
    {
        // 方塊選取每一行是一段，包夾的語意不明確；多重選取同理。
        if (textView.Selection.Mode != TextSelectionMode.Stream ||
            textView.Selection.SelectedSpans.Count != 1)
        {
            return false;
        }

        var span = textView.Selection.SelectedSpans[0];

        if (span.IsEmpty || !ReferenceEquals(span.Snapshot.TextBuffer, buffer))
        {
            return false;
        }

        var close = SqlAutoPairAnalyzer.SurroundCloseFor(
            new SnapshotTextSource(span.Snapshot),
            span.Start.Position,
            typedCharacter);

        if (close is not char closeCharacter)
        {
            return false;
        }

        using var edit = buffer.CreateEdit();
        edit.Insert(span.Start.Position, typedCharacter.ToString());
        edit.Insert(span.End.Position, closeCharacter.ToString());

        var updated = edit.Apply();

        if (updated is null || edit.Canceled)
        {
            return false;
        }

        var inner = new SnapshotSpan(updated, span.Start.Position + 1, span.Length);
        textView.Selection.Select(inner, isReversed: false);
        textView.Caret.MoveTo(inner.End);
        textView.Caret.EnsureVisible();
        SqlAssistDiagnostics.Write($"以 {typedCharacter}{closeCharacter} 包夾選取範圍", textView);
        return true;
    }

    /// <summary>
    /// 在游標處插入結尾字元，並把游標留在它前面。
    /// </summary>
    /// <remarks>
    /// 插入之後游標會跟著跑到補上的字元後面（游標是正向追蹤的），必須擺回來——
    /// 少了這一步，編輯器接著插入的開頭字元會落在結尾字元的<b>後面</b>，
    /// 打一個左括號得到的是 <c>)(</c>。
    /// </remarks>
    private static void InsertClose(
        ITextView textView,
        ITextBuffer buffer,
        int position,
        char close,
        SqlAutoPairTracker tracker)
    {
        using var edit = buffer.CreateEdit();
        edit.Insert(position, close.ToString());

        var updated = edit.Apply();

        if (updated is null || edit.Canceled)
        {
            return;
        }

        textView.Caret.MoveTo(new SnapshotPoint(updated, position));
        tracker.Push(updated, position, close);
        SqlAssistDiagnostics.Write($"自動補上 {close}", textView);
    }

    /// <summary>
    /// 取得可以動的游標位置。
    /// </summary>
    /// <remarks>
    /// 虛擬空白要擋掉：那時游標的緩衝區位置停在行尾，直接寫進去的話補上的字元
    /// 會出現在使用者看到的位置<b>之前</b>，而中間那幾格空白從來沒有被寫出來。
    /// </remarks>
    private static bool TryGetCaret(ITextView textView, ITextBuffer buffer, out SnapshotPoint caret)
    {
        caret = default;

        var settings = SqlAssistSettingsStore.Current;

        if (!settings.Enabled || !settings.AutoPairDelimiters)
        {
            return false;
        }

        if (textView is null || buffer is null || textView.IsClosed || textView.Caret.InVirtualSpace)
        {
            return false;
        }

        var position = textView.Caret.Position.BufferPosition;

        if (!ReferenceEquals(position.Snapshot.TextBuffer, buffer))
        {
            return false;
        }

        caret = position;
        return true;
    }

    /// <summary>
    /// 這個編輯器裡「由自動配對補出來、而且還沒被收掉」的結尾字元。
    /// </summary>
    /// <remarks>
    /// 以 <see cref="ITrackingPoint"/> 記位置，使用者在配對中間繼續打字時才跟得住；
    /// 正向追蹤是刻意的——游標停在結尾字元前面打字時，那個字元要跟著往後移。
    ///
    /// 每次使用前先清掉已經對不上的記錄（被刪掉、被別的編輯換掉），
    /// 這樣「跳過」與「整對刪掉」永遠只作用在文字上真的還在那裡的字元。
    /// 上限只是保險：真正讓清單短下來的是那道清理，而不是這個數字。
    /// </remarks>
    private sealed class SqlAutoPairTracker
    {
        private const int MaximumEntries = 32;

        private readonly List<(ITrackingPoint Point, char Close)> _entries = new();

        public static SqlAutoPairTracker Get(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new SqlAutoPairTracker());

        public void Push(ITextSnapshot snapshot, int position, char close)
        {
            _entries.Add((snapshot.CreateTrackingPoint(position, PointTrackingMode.Positive), close));

            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveAt(0);
            }
        }

        /// <summary>
        /// 游標右邊的那個結尾字元是不是自動補出來的；是的話一併把記錄取走。
        /// </summary>
        /// <remarks>
        /// 由內往外找，找到就連同它後面的記錄一起丟掉：那些是更內層的配對，
        /// 外層都收掉了，內層不可能還有效。
        /// </remarks>
        public bool TryTake(ITextSnapshot snapshot, int position, char close)
        {
            Prune(snapshot);

            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                if (_entries[index].Close != close ||
                    _entries[index].Point.GetPosition(snapshot) != position)
                {
                    continue;
                }

                _entries.RemoveRange(index, _entries.Count - index);
                return true;
            }

            return false;
        }

        private void Prune(ITextSnapshot snapshot)
        {
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var position = _entries[index].Point.GetPosition(snapshot);

                if (position >= snapshot.Length || snapshot[position] != _entries[index].Close)
                {
                    _entries.RemoveAt(index);
                }
            }
        }
    }
}
