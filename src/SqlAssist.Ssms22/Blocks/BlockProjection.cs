using Microsoft.VisualStudio.Text;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.Blocks;

/// <summary>把上一份解析的座標沿 ITextVersion 平移到指定 snapshot。</summary>
/// <remarks>
/// 文字變更時不清空舊配對。清空的話每個按鍵都會先通知六個呈現層「什麼都沒有」，
/// 背景要等背景解析回來才重現，閃爍週期剛好等於 debounce 設定：調小只縮短空白、
/// 調大更明顯，永遠消不掉。代價是編輯真的破壞配對（例如刪掉 END）時，高亮會多撐
/// 一個 debounce 週期才消失；這比每個字元閃一次好得多。
/// SnapshotSpan 與 SnapshotPoint 是 struct，平移不配置物件，才敢放在每個按鍵都會走的路徑。
/// </remarks>
internal static class BlockProjection
{
    /// <summary>EdgeExclusive：端點緊鄰處的輸入不撐大高亮，只有區塊內部的輸入才算進範圍。</summary>
    public static SnapshotSpan Project(ITextSnapshot source, BlockSpan span, ITextSnapshot target) =>
        new SnapshotSpan(source, span.Start, span.Length).TranslateTo(target, SpanTrackingMode.EdgeExclusive);

    public static SnapshotSpan Project(SnapshotSpan span, ITextSnapshot target) =>
        span.Snapshot.Version == target.Version ? span : span.TranslateTo(target, SpanTrackingMode.EdgeExclusive);

    public static SnapshotPoint Project(ITextSnapshot source, int position, ITextSnapshot target) =>
        new SnapshotPoint(source, position).TranslateTo(target, PointTrackingMode.Negative);

    /// <summary>查詢舊索引前先把目前 snapshot 的位置換回解析當時的位置。</summary>
    public static int ToSource(SnapshotPoint point, ITextSnapshot source) =>
        point.Snapshot.Version == source.Version ? point.Position
            : point.TranslateTo(source, PointTrackingMode.Negative).Position;
}
