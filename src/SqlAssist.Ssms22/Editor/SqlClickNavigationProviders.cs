using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Editor;

/// <summary>把滑鼠事件轉給 <see cref="SqlClickNavigator"/>。</summary>
/// <remarks>
/// 必須排在編輯器內建的處理器之前：Ctrl＋點擊原本是 <c>WordSelection</c> 的「選取整個單字」，
/// 平台另有一個 <c>GoToDefMouseHandler</c>（SSMS 22 的 SQL 編輯器沒有接上，但不保證以後也沒有），
/// 拖放由 <c>DragDropMouseProcessor</c> 處理。名稱取自 SSMS 22 的
/// <c>Microsoft.VisualStudio.Platform.VSEditor.dll</c>；不存在的名稱在排序裡會被忽略。
/// </remarks>
[Export(typeof(IMouseProcessorProvider))]
[Name("SqlAssist Click Navigation")]
[Order(Before = "WordSelection")]
[Order(Before = "GoToDefMouseHandler")]
[Order(Before = "DragDropMouseProcessor")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlClickMouseProcessorProvider : IMouseProcessorProvider
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    public IMouseProcessor? GetAssociatedProcessor(IWpfTextView wpfTextView) =>
        SqlAssistPlatformGuard.Create("建立 Ctrl＋點擊的滑鼠處理器", () =>
            new SqlClickMouseProcessor(wpfTextView, SqlClickNavigator.GetOrCreate(wpfTextView, ServiceProvider)));
}

internal sealed class SqlClickMouseProcessor : MouseProcessorBase
{
    private readonly IWpfTextView _view;
    private readonly SqlClickNavigator _navigator;

    public SqlClickMouseProcessor(IWpfTextView view, SqlClickNavigator navigator)
    {
        _view = view;
        _navigator = navigator;
    }

    public override void PreprocessMouseMove(MouseEventArgs e) =>
        SqlAssistPlatformGuard.Run("Ctrl＋點擊：滑鼠移動", () =>
            _navigator.Track(e.GetPosition(_view.VisualElement), _navigator.ModifierSource()));

    public override void PreprocessMouseLeftButtonDown(MouseButtonEventArgs e) =>
        SqlAssistPlatformGuard.Run("Ctrl＋點擊：按下", () =>
        {
            if (_navigator.Press(e.GetPosition(_view.VisualElement), _navigator.ModifierSource(), e.ClickCount)) e.Handled = true;
        });

    public override void PreprocessMouseLeftButtonUp(MouseButtonEventArgs e) =>
        SqlAssistPlatformGuard.Run("Ctrl＋點擊：放開", () =>
        {
            if (_navigator.Release(e.GetPosition(_view.VisualElement))) e.Handled = true;
        });

    public override void PreprocessMouseLeave(MouseEventArgs e) =>
        SqlAssistPlatformGuard.Run("Ctrl＋點擊：滑鼠離開", _navigator.Leave);
}

/// <summary>滑鼠不動、只按下或放開修飾鍵時，底線也要跟著出現或消失。</summary>
/// <remarks>只觀察，不標成已處理：Ctrl 與 Shift 還要照常組成其他快捷鍵。</remarks>
[Export(typeof(IKeyProcessorProvider))]
[Name("SqlAssist Click Navigation Keys")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlClickKeyProcessorProvider : IKeyProcessorProvider
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    public KeyProcessor? GetAssociatedProcessor(IWpfTextView wpfTextView) =>
        SqlAssistPlatformGuard.Create("建立 Ctrl＋點擊的按鍵處理器", () =>
            new SqlClickKeyProcessor(SqlClickNavigator.GetOrCreate(wpfTextView, ServiceProvider)));
}

internal sealed class SqlClickKeyProcessor : KeyProcessor
{
    private readonly SqlClickNavigator _navigator;

    public SqlClickKeyProcessor(SqlClickNavigator navigator) => _navigator = navigator;

    public override void PreviewKeyDown(KeyEventArgs args) => Track(args);

    public override void PreviewKeyUp(KeyEventArgs args) => Track(args);

    private void Track(KeyEventArgs args)
    {
        // 只有修飾鍵本身會改變手勢；其他按鍵連看都不必看。
        if (!IsModifier(args.Key == Key.System ? args.SystemKey : args.Key)) return;
        SqlAssistPlatformGuard.Run("Ctrl＋點擊：修飾鍵", () => _navigator.TrackModifiers(args.KeyboardDevice.Modifiers));
    }

    private static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;
}

[Export(typeof(IViewTaggerProvider))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[TagType(typeof(ClassificationTag))]
internal sealed class SqlClickLinkTaggerProvider : IViewTaggerProvider
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    [Import] internal IClassificationTypeRegistryService Types { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag =>
        SqlAssistPlatformGuard.Create("建立 Ctrl＋點擊的連結底線", () =>
            textView is IWpfTextView view && !view.IsClosed && buffer == view.TextBuffer &&
            Types.GetClassificationType(SqlClickLinkFormat.Name) is { } type
                ? new SqlClickLinkTagger(view, SqlClickNavigator.GetOrCreate(view, ServiceProvider), type) as ITagger<T>
                : null);
}

/// <summary>把 <see cref="SqlClickNavigator.Link"/> 畫成底線；狀態只有那一份。</summary>
internal sealed class SqlClickLinkTagger : ITagger<ClassificationTag>
{
    private readonly IWpfTextView _view;
    private readonly SqlClickNavigator _navigator;
    private readonly ClassificationTag _tag;

    public SqlClickLinkTagger(IWpfTextView view, SqlClickNavigator navigator, IClassificationType type)
    {
        _view = view;
        _navigator = navigator;
        _tag = new ClassificationTag(type);
        _navigator.LinkChanged += OnLinkChanged;
        _view.Closed += OnClosed;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0 || _navigator.Link is not { } link) yield break;

        var span = link.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive);
        if (span.IsEmpty || !spans.IntersectsWith(span)) yield break;
        yield return new TagSpan<ClassificationTag>(span, _tag);
    }

    private void OnLinkChanged(object sender, SnapshotSpanEventArgs args) => TagsChanged?.Invoke(this, args);

    private void OnClosed(object sender, EventArgs args)
    {
        _navigator.LinkChanged -= OnLinkChanged;
        _view.Closed -= OnClosed;
    }
}
