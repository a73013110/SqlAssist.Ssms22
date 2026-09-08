using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 只看一份樣板就能判斷的片段規則。
/// </summary>
/// <remarks>
/// 這些規則原本只在管理介面存檔時跑，而 <c>snippets.json</c> 是使用者可以直接
/// 手改的：撞關鍵字的捷徑會安靜地吃掉那個關鍵字的補全，重複的包夾錨點要等到
/// 某一次 Ctrl+K, Ctrl+S 才把選取的內容複製兩份。<see cref="SqlSnippetMerger.Merge"/>
/// 因此在載入時重跑同一份規則，但<b>只回報不修正</b>——安靜地丟掉那一筆，使用者
/// 只會看到片段消失而沒有任何說明。
///
/// 規則因此只有這一份：載入時走這裡標示，管理介面存檔前也走這裡擋下，兩邊的判斷
/// 不會分岔——分岔的症狀是清單上沒有標記的那一筆，按下儲存時突然被退回。
/// </remarks>
public static class SqlSnippetValidation
{
    /// <summary>
    /// 捷徑的格式與關鍵字撞名。
    /// </summary>
    /// <remarks>
    /// 「這個捷徑已經有人用了」需要一整份清單，留在
    /// <see cref="SqlSnippetLibrary.ValidateShortcut"/>：載入路徑的撞名已經由
    /// <see cref="SqlSnippetMerger"/> 算成「被遮住」，那是計算結果不是錯誤。
    /// </remarks>
    public static bool ValidateShortcut(string? shortcut, out string error)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            error = "捷徑不能空白。";
            return false;
        }

        // 展開器是在「游標前方的那一個詞元」上比對的，含空白或標點的捷徑
        // 永遠不會被切成同一個詞元，也就永遠展不開。與其存進去再讓使用者
        // 納悶為什麼沒反應，不如當場擋下來。
        foreach (var character in shortcut!)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                error = $"捷徑只能用字母、數字與底線，不能有「{character}」。";
                return false;
            }
        }

        // 與 T-SQL 關鍵字撞名的捷徑會把那個字本身吃掉。展開器比對的是游標前方的
        // 那一個詞元，而使用者打 SELECT 時要的多半是關鍵字本身——分不出來的位置
        // 不該由片段贏走。內建片段用 cs、be、ifb 讓開這一條。
        if (SqlKeywordCatalog.TryGetCanonical(shortcut!, out var keyword))
        {
            error = $"捷徑不能與 T-SQL 關鍵字「{keyword}」同名。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 載入與存檔共用的整筆檢查。
    /// </summary>
    /// <param name="shortcut">這一筆的捷徑。</param>
    /// <param name="code">這一筆的樣板。</param>
    /// <param name="shortcutShadowed">
    /// 捷徑這一輪被另一筆優先的片段佔走時不再追究捷徑那一條：那一筆本來就不進
    /// 建議清單，擋在這裡只會讓使用者連別的欄位都存不回去。
    /// </param>
    /// <param name="error">第一條不符的原因；全部通過時是空字串。</param>
    public static bool Validate(string? shortcut, string? code, bool shortcutShadowed, out string error)
    {
        if (!shortcutShadowed && !ValidateShortcut(shortcut, out error))
        {
            return false;
        }

        return SqlSnippetPlaceholders.ValidateSurroundAnchor(code, out error);
    }
}
