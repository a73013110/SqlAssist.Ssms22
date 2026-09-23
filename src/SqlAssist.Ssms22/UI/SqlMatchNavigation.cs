using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using SqlAssist.Core.Matching;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 一份 <see cref="SqlReadOnlyViewer"/> 配一組「上一個／下一個命中」：預覽上標出命中、停在第一處、
/// 按一下走一處的唯一接法。
/// </summary>
/// <remarks>
/// SQL Search 與 SQL Memory 的預覽都經過這裡，各自只決定要標哪幾處（<see cref="MatchHighlightSet"/>）
/// 與一處都沒有時怎麼說。接線散到每一個預覽各寫一次的症狀是其中一個換一列之後忘了清掉游標，
/// 或少捲了第一處，而調整導覽的行為要改兩個地方。
///
/// <see cref="SqlMatchNavigator"/> 本身仍與檢視無關；接到 SQL 檢視、決定何時捲動是這一層的事。
/// </remarks>
internal sealed class SqlMatchNavigation
{
    private readonly SqlReadOnlyViewer _viewer;
    private readonly SqlMatchNavigator _navigator = new();

    public SqlMatchNavigation(SqlReadOnlyViewer viewer)
    {
        _viewer = viewer;
        _navigator.CurrentChanged += (_, _) =>
            SqlAssistPlatformGuard.Run("移到下一處命中", () => _viewer.ShowMatch(_navigator.Matches.Index));

        // 導覽與後面的命令是兩群（「走到哪一處」與「拿這一份 SQL 做什麼」），中間是工具列那一條
        // 共用的群界線。沒有命中時導覽整組收起，界線綁著它的 Visibility 一起收——在每一條換命中的
        // 路徑上各設一次，漏掉其中一條的症狀是工具列從一條孤線開始。
        var divider = SqlAssistChrome.CreateGroupDivider();
        divider.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = _navigator });
        ToolbarItems = new UIElement[] { _navigator, divider };
    }

    /// <summary>
    /// 放在預覽工具列最前面的那一組：導覽與它後面的群界線。
    /// </summary>
    /// <remarks>排在最前面：這一列上它是唯一會被連按好幾次的東西，而複製與換行是各按一次的。</remarks>
    public IReadOnlyList<UIElement> ToolbarItems { get; }

    /// <summary>換上一份 SQL 並標出命中；有命中就停在第一處並捲到可見。</summary>
    /// <remarks>
    /// 自動捲動只發生在這一次，之後一律由導覽接手：一份幾百行的 SQL 從頭顯示而命中在底下時，
    /// 使用者看不出這一筆為什麼在清單上；換一列以外的捲動都是他自己按的。
    /// </remarks>
    public void Show(string sql, MatchHighlightSet highlights)
    {
        _viewer.SetSql(sql, highlights.Spans);
        _navigator.SetCursor(new MatchCursor(highlights.Spans));
        if (highlights.Count != 0) _viewer.ShowMatch(0);
    }

    /// <summary>清掉內容與命中；換一列、載入中或讀不到時用。</summary>
    public void Clear()
    {
        _navigator.Clear();
        _viewer.SetSql("");
    }
}
