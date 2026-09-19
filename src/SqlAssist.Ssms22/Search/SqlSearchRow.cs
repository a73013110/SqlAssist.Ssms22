using System;
using System.Collections.Generic;
using System.ComponentModel;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 結果列右下角的一顆脈絡膠囊，由 provider 掛在命中上。
/// </summary>
/// <remarks>
/// 只把 <see cref="SearchBadge.IconToken"/> 那個中性代號換成自製 UI 的語意圖示；認不得的代號
/// 不畫圖示，膠囊上的字照常出現。對照寫在這裡而不是 Core，理由與分層一樣——
/// Core 是 netstandard2.0，認識 <c>SqlIcon</c> 底下那顆 <c>ImageMoniker</c> 等於把 VS 組件
/// 拉進那一層。
/// </remarks>
internal sealed class SqlSearchBadge
{
    internal SqlSearchBadge(SearchBadge badge)
    {
        if (badge is null) throw new ArgumentNullException(nameof(badge));
        Text = badge.Text;
        Icon = badge.IconToken switch
        {
            SearchBadge.ServerIcon => SqlIcon.Server,
            SearchBadge.DatabaseIcon => SqlIcon.Database,
            _ => null
        };
    }

    public string Text { get; }

    /// <summary>沒有對應圖示時 null；插槽留空，膠囊的字不受影響。</summary>
    public SqlIcon? Icon { get; }
}

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
        Badges = Project(hit.Badges);
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

    /// <summary>分類的顯示字；找不到宣告時退回分類 Id，不留空白。</summary>
    public string CategoryLabel { get; }

    /// <summary>物件種類圖示要查的分類識別字；與補全、QuickInfo 與預覽同一顆原生目錄圖示。</summary>
    /// <remarks>
    /// 交出去的是 <see cref="SearchHit.CategoryId"/> 這個字串，不是酬載。清單只認得分類，
    /// 所以加一個 provider 時這一列與樣板一個字都不必改。
    /// </remarks>
    public string CategoryId => Hit.CategoryId;

    /// <summary>provider 掛的脈絡膠囊（伺服器、資料庫）；沒有時是空的，那一段收起。</summary>
    public IReadOnlyList<SqlSearchBadge> Badges { get; }

    public SearchMatchTarget MatchTarget => Hit.MatchTarget;

    /// <summary>
    /// 命中部位徽章的字；對應工具列上那三段開關。
    /// </summary>
    /// <remarks>
    /// 取代原本的上下分組。分組把同一批結果切成兩疊，使用者要找的那一筆可能在第二疊的底下；
    /// 每一列掛一顆徽章一樣分得出來，而且排序可以換成他真正要的那一種。
    /// 用字與分段開關完全相同——兩邊各叫各的，使用者會以為它們是兩件事。
    /// </remarks>
    public string TargetLabel => SqlSearchTargets.LabelFor(MatchTarget);

    /// <summary>整列唸出來是什麼；螢幕閱讀器與預覽的摘要共用同一句。</summary>
    /// <remarks>
    /// 圖示取代了列上的種類文字，所以種類必須在別的地方讀得到：列的自動化名稱、圖示的
    /// Tooltip 與預覽的摘要。少了這一句，只用鍵盤與螢幕閱讀器的人聽到的只有一個名字。
    /// </remarks>
    public string Description =>
        CategoryLabel + " · " + TargetLabel + (Path.Length == 0 ? "" : " · " + Path);

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

    private static IReadOnlyList<SqlSearchBadge> Project(IReadOnlyList<SearchBadge> badges)
    {
        if (badges.Count == 0) return Array.Empty<SqlSearchBadge>();

        var projected = new SqlSearchBadge[badges.Count];
        for (var index = 0; index < badges.Count; index++) projected[index] = new SqlSearchBadge(badges[index]);
        return projected;
    }

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
    ///
    /// 換算本身走 <see cref="MatchProjection"/>，與預覽把命中對到完整定義上是同一份：
    /// 兩邊各寫一次的症狀是其中一邊的邊界條件改了，而同一筆結果在清單與預覽高亮在不同的字上。
    /// </remarks>
    private static IReadOnlyList<MatchSpan> ProjectOntoTitle(SearchHit hit)
    {
        if (hit.MatchTarget == SearchMatchTarget.Text || hit.Snippet.Length == 0 || hit.SnippetSpans.Count == 0)
        {
            return Array.Empty<MatchSpan>();
        }

        var offset = MatchProjection.Find(hit.Title, hit.Snippet, 0, MatchProjectionMode.FromEnd);
        return offset < 0
            ? Array.Empty<MatchSpan>()
            : MatchProjection.Shift(hit.SnippetSpans, offset, hit.Snippet.Length, hit.Title.Length);
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
