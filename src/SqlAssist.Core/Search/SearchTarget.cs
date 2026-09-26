using System;
using System.ComponentModel;

namespace SqlAssist.Core.Search;

/// <summary>一個搜尋目標是哪一種；畫面用它把「幾個資料庫」與其他來源分開數。</summary>
public enum SearchTargetKind
{
    /// <summary>一個資料庫；完整度的句子數成「N 個資料庫」。</summary>
    Database,

    /// <summary>伺服器層級的一個來源（SQL Agent 作業）；句子裡寫它自己的名字。</summary>
    Source,
}

/// <summary>一個搜尋目標這一輪走到哪裡。</summary>
public enum SearchTargetState
{
    /// <summary>還在建索引或比對。</summary>
    Running,

    /// <summary>每一個要比的部位都比過了；本文仍可能有讀不到的物件，見 <see cref="SearchTargetStatus.UnreadableText"/>。</summary>
    Complete,

    /// <summary>一個字都沒比到：連不上、沒有權限，或資料庫不在線上。</summary>
    Unavailable,

    /// <summary>還沒比完就停了：使用者又打了字、按了停止，或來源擲了例外。</summary>
    Canceled,
}

/// <summary>
/// provider 回報一個目標的把手：進度、結局與沒比到的部分。
/// </summary>
/// <remarks>
/// <b>「沒找到」只有在每一個目標都說得出自己比完了才成立。</b>預算與截斷那一套被這一份取代：
/// 那一套在「全部資料庫 × 全部種類」時把候選數花在名稱與資料行上，本文命中一筆都收不到，
/// 而畫面只說「部分結果」——使用者據此判斷「不存在」而估錯工時。現在每一個目標都要明說
/// 自己的結局；provider 忘了說的那一個在收尾時記成 <see cref="SearchTargetState.Canceled"/>，
/// 不會被當成完整。
///
/// 可以被多執行緒呼叫：目錄那一邊每個資料庫一條執行緒，而畫面的計時器同時在讀進度。
/// 結局只收第一次：一個目標先說讀不到、之後又說完成，是 provider 的錯，而留後到的那一句
/// 會把一個沒比過的資料庫說成比完了。
/// </remarks>
public sealed class SearchTarget
{
    private readonly object _gate = new();
    private double _progress;
    private SearchTargetState _state = SearchTargetState.Running;
    private SearchUnavailableKind _unavailableKind;
    private string? _detail;
    private int _unreadableText;
    private bool _textIncomplete;

    /// <remarks>
    /// 公開是給自己實作 <see cref="ISearchSink"/> 的呼叫端（測試用的記錄 sink）；provider 一律向
    /// <see cref="ISearchSink.AddTarget"/> 要，自己建的目標沒有人會收。
    /// </remarks>
    public SearchTarget(string providerId, string name, SearchTargetKind kind)
    {
        ProviderId = SearchArgument.Identifier(providerId, nameof(providerId));
        Name = SearchArgument.Identifier(name, nameof(name));
        Kind = kind;
    }

    public string ProviderId { get; }

    /// <summary>給人看的名稱：資料庫名稱，或來源的顯示名稱。</summary>
    public string Name { get; }

    public SearchTargetKind Kind { get; }

    /// <summary>
    /// 慢的那一段（建索引）走到哪裡，0 到 1。
    /// </summary>
    /// <remarks>
    /// 只給畫面估進度用，不影響結局；超出範圍的值夾回去，而不是擲例外——進度是估的，
    /// 讀了比預期多的列是常態（兩條查詢之間有人建了新物件）。
    /// </remarks>
    public void ReportProgress(double fraction)
    {
        if (double.IsNaN(fraction)) return;
        var clamped = fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
        lock (_gate) if (_state == SearchTargetState.Running) _progress = clamped;
    }

    /// <summary>
    /// 有幾個物件的本文讀不到（加密，或這個登入沒有 VIEW DEFINITION）。
    /// </summary>
    /// <remarks>
    /// 累加而不是覆寫：同一個目標可以分幾段回報（記憶體裡的那一份加上伺服器端比對的那一份）。
    /// 讀不到的本文與「本文裡沒有這個字」在清單上一模一樣，所以要數出來說。
    /// </remarks>
    public void AddUnreadableText(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate) _unreadableText += count;
    }

    /// <summary>本文有一部分沒比到（例如伺服器端比對失敗）；物件數說不出來時用它。</summary>
    public void MarkTextIncomplete()
    {
        lock (_gate) _textIncomplete = true;
    }

    /// <summary>每一個要比的部位都比過了。</summary>
    public void Complete() => Finish(SearchTargetState.Complete, SearchUnavailableKind.Unknown, null);

    /// <summary>
    /// 這一輪一個字都沒比到。
    /// </summary>
    /// <param name="kind">只有伺服器給了權限錯誤碼才是 <see cref="SearchUnavailableKind.Denied"/>。</param>
    /// <param name="detail">
    /// provider 自己寫、給人看的補充（資料庫狀態 <c>OFFLINE</c>、「問不到資料庫清單」）；
    /// 沒有時畫面只說讀不到或沒有權限。
    /// </param>
    public void Unavailable(SearchUnavailableKind kind = SearchUnavailableKind.Unknown, string? detail = null) =>
        Finish(SearchTargetState.Unavailable, kind, string.IsNullOrWhiteSpace(detail) ? null : detail);

    /// <summary>這一刻的樣子。</summary>
    public SearchTargetStatus Status => Snapshot();

    /// <summary>這一刻的樣子；收尾時還沒說結局的目標記成已取消。</summary>
    internal SearchTargetStatus Snapshot(bool final = false)
    {
        lock (_gate)
        {
            var state = final && _state == SearchTargetState.Running ? SearchTargetState.Canceled : _state;
            var progress = state == SearchTargetState.Running ? _progress : 1;
            return new SearchTargetStatus(
                ProviderId, Name, Kind, state, progress, _unavailableKind, _detail, _unreadableText, _textIncomplete);
        }
    }

    private void Finish(SearchTargetState state, SearchUnavailableKind kind, string? detail)
    {
        lock (_gate)
        {
            if (_state != SearchTargetState.Running) return;
            _state = state;
            _unavailableKind = kind;
            _detail = detail;
        }
    }
}

/// <summary>一個搜尋目標在某一刻的樣子；不可變，可以交給畫面。</summary>
public sealed class SearchTargetStatus
{
    internal SearchTargetStatus(
        string providerId,
        string name,
        SearchTargetKind kind,
        SearchTargetState state,
        double progress,
        SearchUnavailableKind unavailableKind,
        string? detail,
        int unreadableText,
        bool isTextIncomplete)
    {
        ProviderId = providerId;
        Name = name;
        Kind = kind;
        State = state;
        Progress = progress;
        UnavailableKind = unavailableKind;
        Detail = detail;
        UnreadableText = unreadableText;
        IsTextIncomplete = isTextIncomplete;
    }

    public string ProviderId { get; }

    public string Name { get; }

    public SearchTargetKind Kind { get; }

    public SearchTargetState State { get; }

    /// <summary>0 到 1；已經有結局的目標一律是 1。</summary>
    public double Progress { get; }

    /// <summary><see cref="State"/> 是 <see cref="SearchTargetState.Unavailable"/> 時才有意義。</summary>
    public SearchUnavailableKind UnavailableKind { get; }

    /// <summary>provider 寫的補充說明；沒有時為 null。</summary>
    public string? Detail { get; }

    /// <summary>本文讀不到的物件數。</summary>
    public int UnreadableText { get; }

    /// <summary>本文有一部分沒比到，而且說不出是幾個物件。</summary>
    public bool IsTextIncomplete { get; }

    /// <summary>比完了，而且沒有任何一部分漏掉；「沒找到」只有在這裡成立。</summary>
    public bool IsComplete => State == SearchTargetState.Complete && UnreadableText == 0 && !IsTextIncomplete;

    [Localizable(false)]
    public override string ToString() => $"{ProviderId}/{Name}: {State} {Progress:P0}";
}
