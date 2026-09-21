using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.Preview;

namespace SqlAssist.Ssms22.UI;

/// <summary>可編輯的 SQL 表面，語法著色與唯讀預覽同源；不持有收藏、版本或儲存服務。</summary>
/// <remarks>
/// 輸入、選取、復原與 IME 全部交給原生 <see cref="TextBox"/>，它的文字設成透明，著色另畫在上面一層。
/// 不內嵌真正的編輯器（焦點會被搬進另一個呈現來源），也不用 RichTextBox 邊打邊重建文件——
/// 每次按鍵重排上萬個 Run 會明顯卡頓，原文與游標位置還得反覆映射。
/// 位置一律向 TextBox 要，Tab、捲動與字型不自行推算；每次只畫看得見的行，成本跟視窗高度有關，與 SQL 長度無關。
/// 插入點與選取由視窗的裝飾層畫在最上面，不會被著色層蓋住。
/// 字型與顏色只讀 <see cref="ScriptResource"/> 動態資源，由呼叫端掛上 <see cref="SqlScriptTheme"/>；
/// 這裡不碰 SSMS 服務，外觀更新後呼叫 <see cref="RefreshColors"/>。
/// </remarks>
internal sealed class SqlTextEditor : UserControl
{
    private readonly TextBox _text = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SyntaxLayer _layer = new();
    private readonly SqlTextEditState _state;
    private IReadOnlyList<SqlToken> _tokens = Array.Empty<SqlToken>();
    private string? _tokenizedText;
    private ScrollViewer? _viewport;
    private bool? _colorize;
    private bool _dirty = true;

    public SqlTextEditor(string sql)
    {
        _state = new SqlTextEditState(sql);
        _text.AcceptsReturn = true;
        _text.AcceptsTab = true;
        _text.IsUndoEnabled = true;
        _text.TextWrapping = TextWrapping.NoWrap;
        _text.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.Text = sql;
        _text.SetResourceReference(BackgroundProperty, ScriptResource.Background);
        _text.SetResourceReference(FontFamilyProperty, ScriptResource.FontFamily);
        _text.SetResourceReference(FontSizeProperty, ScriptResource.FontSize);
        _text.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, ScriptResource.Foreground);
        // SQL 不換行，所以長的那幾行一定會長出水平捲軸；與唯讀預覽同一份規矩，Shift＋滾輪左右捲。
        SqlAssistChrome.ApplyShiftWheelPan(_text);
        UpdateColorMode();

        // 只留內容：主題會替宿主設底色，預設樣板的方形底會露出 TextBox 圓角外。
        Template = new ControlTemplate(typeof(UserControl)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        var root = new Grid();
        root.Children.Add(_text);
        root.Children.Add(_layer);
        Content = root;

        _text.TextChanged += (_, _) =>
        {
            _state.Text = _text.Text;
            UpdateColorMode();
            Invalidate();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _text.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Invalidate()));
        _text.SizeChanged += (_, _) => Invalidate();
        _text.Loaded += (_, _) => { _viewport = null; Invalidate(); };
        _text.LayoutUpdated += (_, _) => { if (_dirty) Redraw(); };
        _layer.DpiChanged += (_, _) => Invalidate();
    }

    public string Text => _state.Text;
    public bool IsModified => _state.IsModified;
    public bool IsReadOnly { get => _text.IsReadOnly; set => _text.IsReadOnly = value; }
    public event EventHandler? Changed;

    /// <summary>行數與字元數；對話框用它寫淡色摘要，不必自己再掃一次原文。</summary>
    public string Summary => string.Format(CultureInfo.CurrentCulture, "{0:N0} 行 · {1:N0} 字元",
        _text.LineCount < 1 ? 1 : _text.LineCount, _state.Text.Length);

    public void FocusText()
    {
        _text.Focus();
        _text.CaretIndex = 0;
    }

    /// <summary>著色資源換過之後重畫；文字本身與游標、捲動位置不動。</summary>
    public void RefreshColors() => Invalidate();

    /// <summary>太長就交回 TextBox 自己畫單色文字；與唯讀預覽同一條界線。</summary>
    private void UpdateColorMode()
    {
        var colorize = _state.Text.Length <= SqlScriptDocument.MaximumColorizedLength;
        if (colorize == _colorize) return;
        _colorize = colorize;
        if (colorize) _text.Foreground = Brushes.Transparent;
        else _text.SetResourceReference(ForegroundProperty, ScriptResource.Foreground);
    }

    /// <summary>標記需要重畫並保證會有下一次版面；捲動事件可能在本輪版面通知之後才到。</summary>
    private void Invalidate()
    {
        _dirty = true;
        _layer.InvalidateArrange();
    }

    private void Redraw()
    {
        _dirty = false;
        using var context = _layer.Drawing.Open();
        if (_colorize != true || _state.Text.Length == 0) return;
        var first = _text.GetFirstVisibleLineIndex();
        var last = _text.GetLastVisibleLineIndex();
        if (first < 0 || last < first) return;
        _viewport ??= _text.Template?.FindName("PART_ContentHost", _text) as ScrollViewer;
        if (_viewport is null) return;

        var text = _state.Text;
        if (!ReferenceEquals(_tokenizedText, text))
        {
            _tokens = SqlTokenizer.TokenizeWithComments(text);
            _tokenizedText = text;
        }
        var origin = _viewport.TranslatePoint(new Point(), _text);
        context.PushClip(new RectangleGeometry(new Rect(origin, new Size(_viewport.ViewportWidth, _viewport.ViewportHeight))));
        var style = new TextStyle(new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch),
            _text.FontSize, TextOptions.GetTextFormattingMode(_text), VisualTreeHelper.GetDpi(_text).PixelsPerDip,
            ResourceBrush(ScriptResource.Foreground));
        var token = FirstTokenEndingAfter(_text.GetCharacterIndexFromLineIndex(first));
        for (var line = first; line <= last; line++)
        {
            var start = _text.GetCharacterIndexFromLineIndex(line);
            var end = start + _text.GetLineLength(line);
            while (end > start && (text[end - 1] == '\n' || text[end - 1] == '\r')) end--;
            // Tab 寬度由 TextBox 決定；每段不含 Tab 的文字各自向它要起點，才不會自己算錯欄位。
            for (var run = start; run < end;)
            {
                var tab = text.IndexOf('\t', run, end - run);
                var runEnd = tab < 0 ? end : tab;
                if (runEnd > run) token = DrawRun(context, style, text, run, runEnd, token);
                run = runEnd + 1;
            }
        }
        context.Pop();
    }

    private int DrawRun(DrawingContext context, TextStyle style, string text, int start, int end, int token)
    {
        var formatted = new FormattedText(text.Substring(start, end - start), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, style.Typeface, style.Size, style.Foreground, null, style.Mode, style.PixelsPerDip);
        while (token < _tokens.Count && _tokens[token].End <= start) token++;
        for (var index = token; index < _tokens.Count && _tokens[index].Start < end; index++)
        {
            var kind = SqlScriptDocument.Classify(_tokens[index]);
            if (kind == ScriptResource.Foreground) continue;
            var from = Math.Max(start, _tokens[index].Start);
            formatted.SetForegroundBrush(ResourceBrush(kind), from - start, Math.Min(end, _tokens[index].End) - from);
        }
        var anchor = _text.GetRectFromCharacterIndex(start);
        if (!anchor.IsEmpty) context.DrawText(formatted, anchor.TopLeft);
        return token;
    }

    private int FirstTokenEndingAfter(int offset)
    {
        int low = 0, high = _tokens.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_tokens[middle].End <= offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private Brush ResourceBrush(ScriptResource kind) =>
        TryFindResource(kind) as Brush ?? TryFindResource(ScriptResource.Foreground) as Brush ?? SystemColors.WindowTextBrush;

    private sealed class TextStyle
    {
        public TextStyle(Typeface typeface, double size, TextFormattingMode mode, double pixelsPerDip, Brush foreground)
        {
            Typeface = typeface; Size = size; Mode = mode; PixelsPerDip = pixelsPerDip; Foreground = foreground;
        }

        public Typeface Typeface { get; }
        public double Size { get; }
        public TextFormattingMode Mode { get; }
        public double PixelsPerDip { get; }
        public Brush Foreground { get; }
    }

    /// <summary>著色層：不接收滑鼠，內容換掉只重新算繪、不觸發版面。</summary>
    private sealed class SyntaxLayer : FrameworkElement
    {
        public SyntaxLayer() => IsHitTestVisible = false;

        public DrawingGroup Drawing { get; } = new();

        public event EventHandler? DpiChanged;

        protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawDrawing(Drawing);

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            DpiChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
