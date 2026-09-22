using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
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
        CanActivate = SqlSearchActivation.CanActivate(hit);
        CanSelectInExplorer = SqlSearchActivation.CanSelectInExplorer(hit);

        var matches = hit.Matches;
        var body = FirstOf(matches, SearchMatchTarget.Text);
        IReadOnlyList<MatchSpan> spans = Array.Empty<MatchSpan>();

        // 片段那一列要的是本文命中的那一行，而它不一定是這一列的代表：一張資料表可能先被
        // 三個資料行命中（排名在前），定義本文那一筆才是唯一有程式碼可看的。讀代表那一筆的
        // 症狀是片段列上出現一個資料行名稱，用等寬字排成一行看起來像一段截斷的 SQL。
        Snippet = body is null ? "" : Flatten(body.Snippet, body.SnippetSpans, out spans);
        SnippetSpans = spans;
        TitleSpans = ProjectOntoTitle(hit);
        Badges = Project(hit.Badges);
        TargetLabels = ProjectTargets(matches);
        MatchCount = matches.Count;
        Columns = JoinColumns(matches, out var columnSpans);
        ColumnSpans = columnSpans;
    }

    public SearchHit Hit { get; }

    /// <summary>這一列開得出定義嗎。</summary>
    /// <remarks>
    /// 問 <see cref="SqlSearchActivation"/> 而不是自己看 <see cref="SearchHit.ActivatePayload"/>：
    /// 辨識酬載型別只允許發生在那一支，而清單、列上那顆按鈕與右鍵選單要的是<b>同一個</b>答案。
    /// 三處各問各的症狀是停駐時那顆是亮的，按下去卻說這一筆沒有東西可開。
    ///
    /// 建構時算一次：<see cref="SearchHit"/> 不可變，而這兩個值每一次停駐與每一次開選單
    /// 都會被讀到。
    /// </remarks>
    public bool CanActivate { get; }

    /// <summary>這一列在物件總管上指得到節點嗎；與 <see cref="CanActivate"/> 是兩個問題。</summary>
    public bool CanSelectInExplorer { get; }

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

    /// <summary>這一列代表幾處命中；併成一列之前的筆數。</summary>
    public int MatchCount { get; }

    /// <summary>
    /// 命中次數的膠囊字（<c>×7</c>）；只有一處時是空字串，樣板收起那一顆。
    /// </summary>
    /// <remarks>
    /// 每一列都掛一顆 <c>×1</c> 的那一版等於在整份清單上加一欄沒有資訊的字。
    /// 中性膠囊而不是狀態色：它是一個數量，不是一種狀態，與通知那邊的 <c>×N</c> 同一個語言。
    /// </remarks>
    public string MatchCountLabel => MatchCount > 1 ? "×" + MatchCount : "";

    /// <summary>
    /// 這一列對上的幾種部位，依分組先後；同一種只出現一次。
    /// </summary>
    /// <remarks>
    /// 取代原本的上下分組。分組把同一批結果切成兩疊，使用者要找的那一筆可能在第二疊的底下；
    /// 每一列掛徽章一樣分得出來，而且排序可以換成他真正要的那一種。用字與分段開關完全相同
    /// （同走 <see cref="SqlSearchTargets.LabelFor"/>）——兩邊各叫各的，使用者會以為它們是兩件事。
    ///
    /// 併成一列之後「命中部位」不再是一個值：一張表可以同時被名稱、三個資料行與定義本文
    /// 命中，而那正是使用者要從這一列讀到的事。只留代表那一筆的部位等於說
    /// 「它是靠名稱進來的」，而他勾掉名稱那一段之後這一列還在。
    /// </remarks>
    public IReadOnlyList<string> TargetLabels { get; }

    /// <summary>
    /// 命中的資料行名稱，以頓號串起來；沒有資料行命中時是空字串，樣板收起那一列。
    /// </summary>
    /// <remarks>
    /// 併成一列之後，「是哪幾行」的答案只剩這裡說得出來——標題已經收回物件本身。
    /// 在停靠面板裡放不下時由省略號收尾，全文在 Tooltip 與預覽。
    /// </remarks>
    public string Columns { get; }

    /// <summary>資料行那一列要高亮的區段，索引落在 <see cref="Columns"/> 上。</summary>
    public IReadOnlyList<MatchSpan> ColumnSpans { get; }

    /// <summary>整列唸出來是什麼；螢幕閱讀器與預覽的摘要共用同一句。</summary>
    /// <remarks>
    /// 圖示取代了列上的種類文字，所以種類必須在別的地方讀得到：列的自動化名稱、圖示的
    /// Tooltip 與預覽的摘要。少了這一句，只用鍵盤與螢幕閱讀器的人聽到的只有一個名字。
    /// </remarks>
    public string Description =>
        CategoryLabel + " · " + string.Join("、", TargetLabels) +
        (MatchCount > 1 ? " · " + MatchCount + " 處命中" : "") +
        (Columns.Length == 0 ? "" : " · " + Columns) +
        (Path.Length == 0 ? "" : " · " + Path);

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

    /// <summary>第一筆打在這個部位上的命中；沒有就是 null。</summary>
    private static SearchHit? FirstOf(IReadOnlyList<SearchHit> matches, SearchMatchTarget target)
    {
        foreach (var match in matches)
        {
            if (match.MatchTarget == target) return match;
        }

        return null;
    }

    /// <summary>
    /// 幾種部位的顯示字，依 <see cref="SearchMatchTargets.GroupOrder"/>，同一種只留一次。
    /// </summary>
    /// <remarks>
    /// 排序照分組先後而不是命中順序：同一組結果在不同次搜尋裡的膠囊順序要一樣，
    /// 否則使用者掃過去的時候每一列的第二顆膠囊都在換位置。
    /// </remarks>
    private static IReadOnlyList<string> ProjectTargets(IReadOnlyList<SearchHit> matches)
    {
        var seen = new List<SearchMatchTarget>(3);

        foreach (var match in matches)
        {
            if (!seen.Contains(match.MatchTarget)) seen.Add(match.MatchTarget);
        }

        seen.Sort(static (left, right) => left.GroupOrder().CompareTo(right.GroupOrder()));

        var labels = new string[seen.Count];
        for (var index = 0; index < seen.Count; index++) labels[index] = SqlSearchTargets.LabelFor(seen[index]);
        return labels;
    }

    /// <summary>
    /// 資料行命中的名稱串成一行，高亮區段跟著平移。
    /// </summary>
    /// <remarks>
    /// 名稱取自每一筆的<b>片段</b>而不是酬載：片段本來就是「區段索引落在它上面」的那一段文字，
    /// 而向下轉型酬載來畫畫面是這一層明文禁止的——那一刻起清單就只畫得出目錄物件。
    ///
    /// 重複的名稱去掉：同一個資料行不會被回報兩次，但兩個 provider 各報一次是做得到的，
    /// 而畫面上同一行出現兩次看起來像是這張表真的有兩個同名欄位。
    /// </remarks>
    private static string JoinColumns(IReadOnlyList<SearchHit> matches, out IReadOnlyList<MatchSpan> spans)
    {
        const string Separator = "、";
        var text = new StringBuilder();
        var kept = new List<MatchSpan>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            if (match.MatchTarget != SearchMatchTarget.Column || match.Snippet.Length == 0) continue;
            if (!seen.Add(match.Snippet)) continue;

            if (text.Length != 0) text.Append(Separator);
            var offset = text.Length;
            text.Append(match.Snippet);

            foreach (var span in match.SnippetSpans)
            {
                if (span.End <= match.Snippet.Length) kept.Add(new MatchSpan(span.Start + offset, span.Length));
            }
        }

        spans = kept;
        return text.ToString();
    }

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
    /// <b>最後</b>一次出現的位置再平移：結構描述與物件同名（<c>[Loan].[Loan]</c>）時，
    /// 使用者要看的是最後那一段。
    ///
    /// 對不上就整組放棄，不猜：畫錯位置的高亮看起來像是比對錯了，比不畫更難解釋。
    /// 本文命中不做這件事——它的片段來自定義本文，與標題沒有關係。
    ///
    /// 換算本身走 <see cref="MatchProjection"/>，與預覽把命中對到完整定義上是同一份：
    /// 兩邊各寫一次的症狀是其中一邊的邊界條件改了，而同一筆結果在清單與預覽高亮在不同的字上。
    ///
    /// 資料行命中的標題已經是<b>物件</b>的限定名稱（聚合器要把同一張表的幾個資料行命中併成
    /// 一列），所以那一種對不上是常態，不是異常——命中的是哪幾行由資料行那一列自己說。
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
