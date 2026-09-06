using System.Text;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 把擴充屬性的說明整理成單行文字。
/// </summary>
/// <remarks>
/// 說明是使用者自己打進 <c>sp_addextendedproperty</c> 的字串，裡面什麼都有：
/// 換行、定位字元、貼上來的兩個全形空白。三個表面（滑鼠停留提示、建議清單的
/// 說明面板、結構預覽）都只給它一行的位置，各自處理的症狀是同一段說明在三個
/// 地方換行方式不同，而其中一個會把資料格的列高撐破。
///
/// 只收斂空白，不改字。截斷交給 <see cref="Summarize"/>，而且只有排不下的那個
/// 表面才呼叫——結構預覽的資料格自己會省略並把全文留在 Tooltip 裡，
/// 先截一次等於把那份全文也砍掉。
/// </remarks>
public static class SqlDescriptionText
{
    /// <summary>提示類表面預設的長度上限。</summary>
    /// <remarks>
    /// 提示視窗不會自己斷行，長說明會排成一長行然後被螢幕邊界切掉——那比省略號
    /// 難讀得多，因為看不出來後面還有東西。
    /// </remarks>
    public const int DefaultMaximumLength = 60;

    private const char Ellipsis = '…';

    /// <summary>換行與連續空白收成單一空白；只有空白時回傳 null。</summary>
    public static string? Collapse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var builder = new StringBuilder(description!.Length);
        var pendingSpace = false;

        foreach (var character in description)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>收斂空白之後再截斷；截掉的部分以省略號結尾。</summary>
    public static string? Summarize(string? description, int maximumLength = DefaultMaximumLength)
    {
        var collapsed = Collapse(description);

        if (collapsed is null || collapsed.Length <= maximumLength || maximumLength <= 0)
        {
            return collapsed;
        }

        return collapsed.Substring(0, maximumLength).TrimEnd() + Ellipsis;
    }
}
