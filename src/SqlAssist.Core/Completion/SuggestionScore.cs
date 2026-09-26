using System;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 建議清單的排名分數；只由 <see cref="SuggestionList"/> 使用。
/// </summary>
/// <remarks>
/// 分數有四個層級。每一層的倍率都大於它底下所有層的最大總和，因此低層只在高層打平時才說得上話：
///
///   比對品質（8192／分） ＞ 類別（最多 40×128＝5120） ＞ 最近用過（64） ＞ 名稱長度（最多 63）
///
/// 這個關係必須維持。倍率一旦太靠近，低層就會翻過高層——曾經
/// 長度懲罰上限 64、類別加成最多 40，兩者同一量級，於是
/// <c>LIBRARY_LOAN_HISTORY_DETAIL</c>（欄位 35−27＝8）輸給
/// <c>USERS</c>（資料表 20−5＝15），正好違反「欄位優先於資料表」。
///
/// 「最近用過」曾經壓在類別之上，而那條規則在真實的編輯順序下必定反過來咬人：
/// 使用者剛從清單裡挑完 <c>FROM dbo.Loan l, dbo.Copy c</c>，游標一移到
/// <c>SET |</c> 或 <c>WHERE |</c>，那幾張表就全部帶著加成排在敘述自己的欄位
/// 前面——而他要的正是欄位。類別說的是「這個位置文法上要什麼」，
/// 使用紀錄只是跨敘述的猜測，猜測不該翻過眼前這句話的證據。
/// 降到類別之下，它仍然做原本真正有價值的那件事：同一類別內把常用的拉到前面。
/// </remarks>
internal static class SuggestionScore
{
    /// <summary>比對品質的倍率；要大於類別、最近用過與長度三層的總和。</summary>
    private const int FuzzyScoreScale = 8192;

    /// <summary>
    /// 最近提交過的加成。
    /// </summary>
    /// <remarks>
    /// 壓得過名稱長度，壓不過類別偏好：它排的是「同一類別裡先看哪一個」。
    /// </remarks>
    private const int RecentlyUsedBonus = 64;

    /// <summary>類別偏好的倍率；要大於最近用過加成與長度懲罰的總和。</summary>
    private const int KindBonusScale = 128;

    /// <summary>長度懲罰的上限，避免超長物件名稱把分數拉到失真。</summary>
    private const int MaximumLengthPenalty = 63;

    /// <summary>完全相同（忽略大小寫）時的壓倒性加成。</summary>
    private const int ExactMatchBonus = 10_000_000;

    /// <summary>
    /// 把模糊分數與次要調整合成最終排名分數。
    /// </summary>
    /// <param name="displayText">清單上顯示、也是比對的那段文字。</param>
    /// <param name="suggestion">
    /// 不是本擴充來源的項目沒有建議項：它照同一套尺度計分，只是沒有類別與最近用過兩層，
    /// 與開場排序（<see cref="ComposeStanding"/>）給它的零分一致。
    /// </param>
    /// <param name="match">已命中的比對結果。</param>
    /// <param name="pattern">已正規化的輸入。</param>
    public static int Compose(string displayText, SqlSuggestion? suggestion, FuzzyMatchResult match, string pattern)
    {
        var score = match.Score * FuzzyScoreScale;

        if (suggestion is not null)
        {
            score += ComposeStanding(suggestion, pattern, match);
        }

        if (pattern.Length > 0 &&
            string.Equals(displayText, pattern, StringComparison.OrdinalIgnoreCase))
        {
            score += ExactMatchBonus;
        }

        // 分數相同時偏好較短的名稱：使用者通常想要的是最精簡的那個。
        return score - Math.Min(displayText.Length, MaximumLengthPenalty);
    }

    /// <summary>
    /// 與使用者輸入無關的那一段分數：最近用過與類別偏好。
    /// </summary>
    /// <remarks>
    /// 還沒輸入任何字元時，這就是清單的順序——比對品質這一層此時對所有候選項
    /// 都是零，剩下的正好是「在不知道他要打什麼的情況下，最可能要的東西」。
    /// 與 <see cref="Compose"/> 共用同一組層級，兩種情境的偏好才會一致。
    /// </remarks>
    public static int ComposeStanding(SqlSuggestion? suggestion)
    {
        return suggestion is null
            ? 0
            : ComposeStanding(suggestion, string.Empty, FuzzyMatchResult.NoMatch);
    }

    private static int ComposeStanding(
        SqlSuggestion suggestion,
        string pattern,
        FuzzyMatchResult match)
    {
        var kindBonus = KindBonus(suggestion.Kind);
        var isSnippet = suggestion.Kind == SuggestionKind.Snippet;

        if (isSnippet && (pattern.Length == 0 || !IsStrongSnippetMatch(match)))
        {
            // 靠捷徑記憶的片段不該塞滿 Ctrl+Space 首頁；只有從捷徑開頭命中時
            // 才保留最高類別加成，純子序列命中則讓位給真正的欄位與物件。
            kindBonus = 5;
        }

        var score = kindBonus * KindBonusScale;

        // 便宜的條件先問：沒有前綴的片段根本不拿這一層，不必查使用紀錄。
        if (!(isSnippet && pattern.Length == 0) &&
            SqlSuggestionUsage.IsRecent(suggestion))
        {
            score += RecentlyUsedBonus;
        }

        return score;
    }

    private static bool IsStrongSnippetMatch(FuzzyMatchResult match) =>
        match.Spans.Count > 0 && match.Spans[0].Start == 0;

    /// <summary>
    /// 類別偏好；由 <see cref="KindBonusScale"/> 放大成一個層級，
    /// 只在比對品質打平時決定順序，並壓過最近使用與名稱長度。
    /// </summary>
    private static int KindBonus(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Snippet => 40,

            // 欄位只會在敘述真的看得到它們時才進入候選，因此排在資料表之上：
            // 在 SELECT 或 WHERE 位置輸入前綴時，要的幾乎都是欄位。
            SuggestionKind.Column => 35,
            SuggestionKind.Keyword => 30,

            // 內建函式排在關鍵字之下：分數打平時，使用者要的比較可能是
            // 文法上非有不可的那個字。
            SuggestionKind.BuiltInFunction => 28,

            // 同上：只有 @ 之後才會出現，清單裡整批都是同一類，
            // 這兩個值不會與任何別的類別比大小。列出來只是不留白。
            SuggestionKind.GlobalVariable => 25,
            SuggestionKind.DataType => 25,
            SuggestionKind.Trigger => 25,
            SuggestionKind.Sequence => 25,
            SuggestionKind.DatePart => 25,
            SuggestionKind.TableHint => 25,
            SuggestionKind.QueryHint => 25,
            SuggestionKind.Collation => 25,

            // 唯一與同類別比大小的一個：COLLATE 之後那份清單有五千多筆，
            // 而名稱長得幾乎一樣（只差 _CI_AS、_CS_AS 這種尾巴），模糊比對的
            // 順序沒有意義。目前資料庫的定序與這份指令碼已經寫過的那一個
            // 排在前面，其餘照舊——差一個層級就壓得過長度懲罰與最近使用。
            SuggestionKind.CollationInUse => 30,

            // 自訂型別排在內建型別之上：DECLARE @t | 打出前綴時，
            // 使用者要的是自己那一個，內建型別他背得起來。
            SuggestionKind.UserDefinedType => 26,

            // 參數與變數是唯一會同時出現在一份清單裡的兩類（EXEC p @|）。
            // 參數在前：他打出小老鼠是為了具名傳值，而那個名字是被呼叫端定的。
            SuggestionKind.Parameter => 26,
            SuggestionKind.Variable => 25,

            // 排在資料表之上：這個名稱是使用者在同一份指令碼裡剛取的，
            // 他會去 FROM 後面補字，正是因為還沒背起來。
            SuggestionKind.ScriptDataSource => 22,
            SuggestionKind.Table => 20,
            SuggestionKind.View => 18,
            SuggestionKind.Procedure => 16,

            // 資料表值函式排在純量函式之上而在預存程序之下：它與資料表、檢視
            // 競爭同一個位置（FROM 之後），而那個位置使用者要的通常是一張表。
            SuggestionKind.TableFunction => 15,
            SuggestionKind.Function => 14,
            SuggestionKind.Schema => 10,

            // 這兩類排在最底：它們與資料表競爭 FROM 之後那一格，而那裡使用者要的
            // 幾乎都是目前這個資料庫的表。USE 之後沒有別的東西跟資料庫競爭，
            // 所以壓低不影響那個位置——同一類別裡常用的那幾個仍會被使用紀錄拉上來。
            SuggestionKind.Database => 9,
            SuggestionKind.LinkedServer => 8,
            _ => 0
        };
    }
}
