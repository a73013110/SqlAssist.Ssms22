using System;
using System.Collections.Generic;
using System.ComponentModel;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 清單上的一列，只從 <see cref="SearchHit"/> 取值。
/// </summary>
/// <remarks>
/// <b>不得</b>向下轉型 <see cref="SearchHit.ActivatePayload"/> 來畫畫面：那一刻起，
/// 清單就只畫得出目錄物件，而加一個 provider 的代價從「多一支啟動器」變成「改整份樣板」。
/// 辨識酬載型別只允許發生在啟動那一步，見 <see cref="SqlSearchActivation"/>。
/// </remarks>
internal sealed class SqlSearchRow : INotifyPropertyChanged
{
    private bool _isNew;

    public SqlSearchRow(SearchHit hit, string categoryLabel)
    {
        Hit = hit ?? throw new ArgumentNullException(nameof(hit));
        CategoryLabel = categoryLabel ?? throw new ArgumentNullException(nameof(categoryLabel));
        Snippet = Flatten(hit.Snippet, hit.SnippetSpans, out var spans);
        SnippetSpans = spans;
        TitleSpans = ProjectOntoTitle(hit);
    }

    public SearchHit Hit { get; }

    /// <summary>與聚合器去重時同一把鍵；重新整理後靠它選回原來那一列。</summary>
    /// <remarks>
    /// 直接就是 <see cref="SearchHit.DedupeKey"/>：聚合器已經把同一個東西的幾種命中併成一列，
    /// 再接一段命中部位上去的話，重新整理之後那一列換成另一種部位命中就選不回來了。
    /// </remarks>
    public string Key => Hit.DedupeKey;

    public string Title => Hit.Title;

    /// <summary>限定名稱；沒有路徑概念的來源是空字串，樣板收起那一段。</summary>
    public string Path => Hit.Path?.ToString() ?? "";

    /// <summary>攤平成單行、去掉縮排的片段；高亮區段的索引已經跟著換算。</summary>
    public string Snippet { get; }

    public IReadOnlyList<MatchSpan> SnippetSpans { get; }

    /// <summary>標題上要高亮的區段；對不上時是空的，標題就照原樣畫。</summary>
    public IReadOnlyList<MatchSpan> TitleSpans { get; }

    /// <summary>pill 上那個分類的顯示字；找不到宣告時退回分類 Id，不留空白。</summary>
    public string CategoryLabel { get; }

    public SearchMatchTarget MatchTarget => Hit.MatchTarget;

    /// <summary>分組標頭的字；三組的意義完全不同，混在一起排會讓表名被註解壓下去。</summary>
    public string GroupLabel =>
        MatchTarget switch
        {
            SearchMatchTarget.Name => "名稱",
            SearchMatchTarget.Column => "資料行",
            _ => "定義本文"
        };

    /// <summary>剛加入清單；卡片以它播一次進場動畫，清單稍後清掉，捲動重用容器時才不會重播。</summary>
    public bool IsNew
    {
        get => _isNew;
        set
        {
            if (_isNew == value) return;
            _isNew = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNew)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 把名稱命中的高亮換算到限定名稱上。
    /// </summary>
    /// <remarks>
    /// 名稱命中的片段就是名稱本體（<c>Loan</c>），而列上顯示的是限定名稱（<c>[dbo].[Loan]</c>）；
    /// 高亮區段的索引落在片段上，直接拿去畫會落在結構描述那幾個字上。這裡找片段在標題裡
    /// <b>最後</b>一次出現的位置再平移——資料行命中的標題是
    /// <c>[dbo].[Loan].[CopyNo]</c>，而使用者要看的是最後那一段。
    ///
    /// 對不上就整組放棄，不猜：畫錯位置的高亮看起來像是比對錯了，比不畫更難解釋。
    /// 本文命中不做這件事——它的片段來自定義本文，與標題沒有關係。
    /// </remarks>
    private static IReadOnlyList<MatchSpan> ProjectOntoTitle(SearchHit hit)
    {
        if (hit.MatchTarget == SearchMatchTarget.Text || hit.Snippet.Length == 0 || hit.SnippetSpans.Count == 0)
        {
            return Array.Empty<MatchSpan>();
        }

        var offset = hit.Title.LastIndexOf(hit.Snippet, StringComparison.Ordinal);
        if (offset < 0) return Array.Empty<MatchSpan>();

        var spans = new List<MatchSpan>(hit.SnippetSpans.Count);

        foreach (var span in hit.SnippetSpans)
        {
            if (span.End > hit.Snippet.Length) return Array.Empty<MatchSpan>();
            spans.Add(new MatchSpan(span.Start + offset, span.Length));
        }

        return spans;
    }

    /// <summary>
    /// 片段攤成一行：換行與定位字元換成空白，並切掉前導空白。
    /// </summary>
    /// <remarks>
    /// 位移<b>一定</b>要跟著切掉的長度換算。高亮是照 <see cref="MatchSpan.Start"/> 畫的，
    /// 少換算這一次的症狀是每一段高亮都畫在縮排那幾格上，而那看起來像是比對錯了。
    /// 長度不變的取代（換行換成空白）不影響位移，只有前導空白要減。
    /// </remarks>
    internal static string Flatten(string snippet, IReadOnlyList<MatchSpan> spans, out IReadOnlyList<MatchSpan> shifted)
    {
        if (snippet.Length == 0)
        {
            shifted = Array.Empty<MatchSpan>();
            return "";
        }

        var flattened = snippet.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        var offset = 0;
        while (offset < flattened.Length && flattened[offset] == ' ') offset++;

        var text = flattened.Substring(offset).TrimEnd();

        if (spans.Count == 0)
        {
            shifted = Array.Empty<MatchSpan>();
            return text;
        }

        var kept = new List<MatchSpan>(spans.Count);

        foreach (var span in spans)
        {
            var start = span.Start - offset;
            // 被切掉的區段整段丟掉，不夾在邊界上：畫一半的高亮比不畫更難讀。
            if (start >= 0 && start + span.Length <= text.Length) kept.Add(new MatchSpan(start, span.Length));
        }

        shifted = kept;
        return text;
    }
}
