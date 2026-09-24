using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// Ctrl＋點擊：按住修飾鍵時把滑鼠下的名稱標成連結，點下去交給 <see cref="SqlObjectNavigation"/>。
/// </summary>
/// <remarks>
/// 每個編輯器一份。滑鼠與按鍵處理器只負責把事件轉進來，底線由
/// <see cref="SqlClickLinkTagger"/> 依 <see cref="Link"/> 畫；三者共用這一份狀態，
/// 才不會出現「底線還在、點下去卻沒反應」這種兩邊各算一次的分岔。
///
/// 滑鼠移動路徑上只做純文字判斷（<see cref="SqlClickTarget"/>，只看那一行），不查中繼資料；
/// 沒按修飾鍵時連那一行都不讀。
///
/// <b>按下就接手、放開才執行</b>：按下時不吞掉的話，編輯器會先把單字選起來、把游標搬過去；
/// 放開才執行則讓「按下後拖開」能反悔。點在既有的選取範圍裡不接手——那是 Ctrl＋拖曳複製。
/// </remarks>
internal sealed class SqlClickNavigator
{
    private readonly IWpfTextView _view;
    private readonly IServiceProvider _serviceProvider;

    private SnapshotSpan? _link;
    private SqlClickAction _linkAction;

    private Point? _pressAt;
    private SqlClickAction _pressAction;
    private SnapshotPoint _pressPoint;

    private bool _cursorOverridden;
    private object? _previousCursor;

    private SqlClickNavigator(IWpfTextView view, IServiceProvider serviceProvider)
    {
        _view = view;
        _serviceProvider = serviceProvider;
        _view.LayoutChanged += OnLayoutChanged;
        _view.LostAggregateFocus += OnLostAggregateFocus;
        _view.Closed += OnClosed;
    }

    /// <summary>
    /// 滑鼠事件路徑上的修飾鍵。
    /// </summary>
    /// <remarks>
    /// <c>MouseEventArgs</c> 只帶滑鼠裝置，問不到隨事件的鍵盤狀態，只能讀目前的；與清單多選
    /// （<c>SqlCardListBase.ModifierSource</c>）同一個例外，也同樣做成可替換的。
    /// 按鍵路徑一律用 <see cref="TrackModifiers"/> 傳進 <c>KeyEventArgs.KeyboardDevice</c> 的值。
    /// </remarks>
    internal Func<ModifierKeys> ModifierSource { get; set; } = () => Keyboard.Modifiers;

    /// <summary>目前標成連結的範圍（在 <see cref="ITextView.TextBuffer"/> 上）；沒有時為 null。</summary>
    public SnapshotSpan? Link => _link;

    /// <summary>連結換了；參數是新舊兩段的聯集，標記器只重畫那一段。</summary>
    public event EventHandler<SnapshotSpanEventArgs>? LinkChanged;

    public static SqlClickNavigator GetOrCreate(IWpfTextView view, IServiceProvider serviceProvider) =>
        view.Properties.GetOrCreateSingletonProperty(
            typeof(SqlClickNavigator),
            () => new SqlClickNavigator(view, serviceProvider));

    /// <summary>滑鼠移動或修飾鍵換了：依滑鼠位置重算連結。</summary>
    /// <param name="position">相對於 <see cref="IWpfTextView.VisualElement"/> 的座標。</param>
    public void Track(Point position, ModifierKeys modifiers)
    {
        if (_pressAt is { } pressAt)
        {
            // 按下之後拖開了：這一次不算點擊。按下時已經吞掉，所以編輯器也不會開始選取。
            if (IsDrag(pressAt, position)) _pressAt = null;
            return;
        }

        var action = ResolveAction(modifiers);
        if (action == SqlClickAction.None)
        {
            SetLink(null, SqlClickAction.None);
            return;
        }

        // 還在同一段連結上就不重讀那一行：按住 Ctrl 滑過一個長名稱時，每一個像素都會走到這裡。
        var hit = HitTest(position);
        if (hit is { } point && _link is { } current && action == _linkAction &&
            current.Snapshot == point.Snapshot && current.Contains(point))
        {
            return;
        }

        SetLink(hit is { } target ? FindLink(target, action) : null, action);
    }

    /// <summary>修飾鍵換了，但滑鼠沒動。</summary>
    public void TrackModifiers(ModifierKeys modifiers)
    {
        if (_view.IsClosed || !_view.VisualElement.IsMouseOver)
        {
            SetLink(null, SqlClickAction.None);
            return;
        }

        Track(Mouse.GetPosition(_view.VisualElement), modifiers);
    }

    /// <returns>接手了這次按下（呼叫端要標成已處理）時為 true。</returns>
    public bool Press(Point position, ModifierKeys modifiers, int clickCount)
    {
        _pressAt = null;

        // 雙擊仍是編輯器的選取單字；只接單擊。
        var action = ResolveAction(modifiers);
        if (clickCount != 1 || action == SqlClickAction.None || HitTest(position) is not { } point)
        {
            return false;
        }

        if (FindLink(point, action) is null || IsInsideSelection(point))
        {
            return false;
        }

        _pressAt = position;
        _pressAction = action;
        _pressPoint = point;

        // 吞掉按下也吞掉了編輯器取得焦點那一步；不補的話，從物件總管直接 Ctrl＋點擊時
        // 焦點留在原處，打開的預覽一出現就被當成失焦收掉。
        _view.VisualElement.Focus();
        return true;
    }

    /// <returns>這次放開屬於一次接手的按下（呼叫端要標成已處理）時為 true。</returns>
    public bool Release(Point position)
    {
        if (_pressAt is not { } pressAt)
        {
            return false;
        }

        _pressAt = null;
        if (IsDrag(pressAt, position) || _view.IsClosed)
        {
            return true;
        }

        // 以按下那一刻為準：放開前先鬆了 Ctrl 的人，意思還是按下時的那一個。
        var point = _pressPoint.TranslateTo(_view.TextSnapshot, PointTrackingMode.Positive);
        SqlAssistPlatformGuard.Run(
            "Ctrl＋點擊導覽",
            () => SqlObjectNavigation.Run(_pressAction, _view, point, _serviceProvider));
        return true;
    }

    /// <summary>滑鼠離開編輯器：收掉連結。按下中的那一次留著，放開時才知道算不算數。</summary>
    public void Leave() => SetLink(null, SqlClickAction.None);

    private static SqlClickAction ResolveAction(ModifierKeys modifiers)
    {
        var settings = SqlAssistSettingsStore.Current;
        return settings.Enabled && settings.ClickNavigationEnabled
            ? SqlClickGestures.Resolve(modifiers)
            : SqlClickAction.None;
    }

    private static bool IsDrag(Point from, Point to) =>
        Math.Abs(to.X - from.X) > SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(to.Y - from.Y) > SystemParameters.MinimumVerticalDragDistance;

    /// <summary>滑鼠下的文字位置（在 <see cref="ITextView.TextBuffer"/> 上）；不在文字上時為 null。</summary>
    private SnapshotPoint? HitTest(Point position)
    {
        if (_view.IsClosed || _view.InLayout || _view.TextViewLines is not { } lines)
        {
            return null;
        }

        var line = lines.GetTextViewLineContainingYCoordinate(position.Y + _view.ViewportTop);

        // textOnly：行尾之後的空白不算落在最後一個字上。
        if (line?.GetBufferPositionFromXCoordinate(position.X + _view.ViewportLeft, textOnly: true) is not { } visual)
        {
            return null;
        }

        // 畫面上的位置在視覺緩衝區（摺疊之後的那一層），定位與 F12 用的是編輯緩衝區。
        return _view.BufferGraph.MapDownToBuffer(
            visual,
            PointTrackingMode.Positive,
            _view.TextBuffer,
            PositionAffinity.Successor);
    }

    private static SnapshotSpan? FindLink(SnapshotPoint point, SqlClickAction action)
    {
        var line = point.GetContainingLine();
        var reference = SqlClickTarget.FindAt(
            line.GetText(),
            point.Position - line.Start.Position,
            SqlClickGestures.AcceptsBuiltIns(action));
        return reference is null
            ? null
            : new SnapshotSpan(line.Start + reference.Start, reference.Length);
    }

    private bool IsInsideSelection(SnapshotPoint point)
    {
        var selection = _view.Selection;
        if (selection.IsEmpty) return false;
        foreach (var span in selection.SelectedSpans)
        {
            if (span.Snapshot == point.Snapshot && span.Contains(point)) return true;
        }

        return false;
    }

    private void SetLink(SnapshotSpan? link, SqlClickAction action)
    {
        _linkAction = link is null ? SqlClickAction.None : action;
        var previous = _link;
        if (previous == link) return;

        _link = link;
        SetHandCursor(link is not null);

        // 舊的那一段可能落在更早的快照上（剛打過字）；對到目前的快照再合併。
        var snapshot = _view.TextSnapshot;
        var changed = Union(previous?.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive), link?.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive));
        if (changed is { } span) LinkChanged?.Invoke(this, new SnapshotSpanEventArgs(span));
    }

    private static SnapshotSpan? Union(SnapshotSpan? first, SnapshotSpan? second)
    {
        if (first is not { } a) return second;
        if (second is not { } b) return a;
        var start = Math.Min(a.Start.Position, b.Start.Position);
        var end = Math.Max(a.End.Position, b.End.Position);
        return new SnapshotSpan(a.Snapshot, start, end - start);
    }

    /// <summary>
    /// 連結上換成手指游標，離開時還原成編輯器原本的設定。
    /// </summary>
    /// <remarks>
    /// 還原的是<b>本地值</b>而不是寫回 IBeam：編輯器沒設過本地值時，游標是由內層元素決定的，
    /// 寫死 IBeam 會把邊欄、捲軸上的箭頭也蓋掉。
    /// </remarks>
    private void SetHandCursor(bool hand)
    {
        var element = _view.VisualElement;
        if (hand && !_cursorOverridden)
        {
            _previousCursor = element.ReadLocalValue(FrameworkElement.CursorProperty);
            _cursorOverridden = true;
            element.Cursor = Cursors.Hand;
        }
        else if (!hand && _cursorOverridden)
        {
            _cursorOverridden = false;
            if (_previousCursor == DependencyProperty.UnsetValue) element.ClearValue(FrameworkElement.CursorProperty);
            else element.Cursor = _previousCursor as Cursor;
            _previousCursor = null;
        }
    }

    // 捲動、縮放、打字都會讓滑鼠底下換一個字；以目前的修飾鍵重算一次。
    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("Ctrl＋點擊：版面變更後重算連結", () =>
        {
            if (_link is not null) TrackModifiers(ModifierSource());
        });

    // 切到別的程式時放開 Ctrl 收不到按鍵事件，底線與手指游標會一直留著。
    private void OnLostAggregateFocus(object sender, EventArgs args) =>
        SqlAssistPlatformGuard.Run("Ctrl＋點擊：失焦時收掉連結", () =>
        {
            _pressAt = null;
            SetLink(null, SqlClickAction.None);
        });

    private void OnClosed(object sender, EventArgs args)
    {
        _view.LayoutChanged -= OnLayoutChanged;
        _view.LostAggregateFocus -= OnLostAggregateFocus;
        _view.Closed -= OnClosed;
    }
}
