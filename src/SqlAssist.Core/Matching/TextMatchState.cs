using System;

namespace SqlAssist.Core.Matching;

/// <summary>
/// <see cref="TextMatchOptions"/> 的驗證與「記住上次設定」的字串格式；兩個工具窗共用一份。
/// </summary>
/// <remarks>
/// 格式是 <c>大小寫|全字</c>，兩段都是 <c>0</c> 或 <c>1</c>。SQL Search 在前面多接自己的
/// 比對位置，所以這一段的樣子不能改：改了之後既有使用者記住的那一份整組認不得，
/// 下次開窗就回到預設，而畫面上看不出為什麼。
/// </remarks>
public static class TextMatchState
{
    private const TextMatchOptions Known = TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord;

    /// <summary>認不得的位元直接拒絕：照「沒開」處理等於默默吞掉使用者的條件。</summary>
    public static TextMatchOptions Require(TextMatchOptions options, string parameterName) =>
        (options & ~Known) == 0 ? options : throw new ArgumentOutOfRangeException(parameterName);

    public static string Format(TextMatchOptions options) =>
        ((options & TextMatchOptions.MatchCasing) != 0 ? "1" : "0") + "|" +
        ((options & TextMatchOptions.WholeWord) != 0 ? "1" : "0");

    /// <summary>讀回 <see cref="Format"/> 寫的那一段；任何一處認不得就整組不算。</summary>
    /// <remarks>
    /// 不逐項盡量還原：半套的狀態與「使用者上次真的這樣設」在畫面上一模一樣，
    /// 而預設值至少是一個說得出來的起點。「不是 1 就當成 0」同理，會把記壞的字串讀成正常的狀態。
    /// </remarks>
    public static bool TryParse(string? token, out TextMatchOptions options)
    {
        options = TextMatchOptions.None;
        if (token is not { Length: 3 } || token[1] != '|') return false;
        if (!TryFlag(token[0], TextMatchOptions.MatchCasing, ref options)) return false;
        return TryFlag(token[2], TextMatchOptions.WholeWord, ref options);
    }

    private static bool TryFlag(char value, TextMatchOptions flag, ref TextMatchOptions options)
    {
        if (value == '1') options |= flag;
        return value is '0' or '1';
    }
}
