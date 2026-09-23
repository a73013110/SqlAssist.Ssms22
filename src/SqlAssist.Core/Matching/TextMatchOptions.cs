using System;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 字面比對的修飾：使用者在搜尋框裡開著哪幾顆。
/// </summary>
/// <remarks>
/// 一份，SQL Search 與 SQL Memory 共用，比對一律交給 <see cref="TextMatcher"/>。
/// 做成旗標而不是一個個布林，是為了之後加選項（例如 regex）只是多一個位元，
/// 請求型別、跨程序邊界與記住狀態的那幾處都不必改簽章。
///
/// 位元說的是「使用者開了什麼」，所以 <see cref="None"/> 就是預設：不分大小寫、不檢查詞界。
/// 反過來以「忽略大小寫」為一位的那一版曾與這一份並存，症狀是其中一處把「沒勾大小寫」
/// 讀成 ordinal，而同一個字串在兩個部位上命中的筆數不一樣。
/// </remarks>
[Flags]
public enum TextMatchOptions
{
    None = 0,

    /// <summary>大小寫要完全一樣。</summary>
    MatchCasing = 1,

    /// <summary>前後都必須是詞界；字母、數字與底線算字的一部分。</summary>
    WholeWord = 2
}
