using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 這份指令碼裡已經出現過的定序名稱。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlScriptDataSourceSuggestions"/> 同一條理由，只是換一種名稱：
/// 使用者要在第二個 <c>COLLATE</c> 之後打的，幾乎一定是他第一個 <c>COLLATE</c>
/// 已經寫過的那一個——兩邊定序不一樣正是那句 <c>COLLATE</c> 要修的問題。
/// 而伺服器那份名單有五千多筆，模糊比對撈回來的順序幫不上任何忙。
///
/// 這一份不必問伺服器，因此連不上、權限不足或關掉「列出資料庫物件與欄位」時
/// 都還在，見 <see cref="CompletionTarget.Collation"/>。
///
/// 只在游標真的落在 <c>COLLATE</c> 之後才掃：掃描是單趟線性的，但那條路徑
/// 在每一次按鍵上，而絕大多數位置用不到這一份。
/// </remarks>
public static class SqlScriptCollationSuggestions
{
    private const string Description = "這份指令碼已經用過的定序";

    /// <param name="tokens">整份指令碼的詞法單元。</param>
    public static IReadOnlyList<SqlSuggestion> Create(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        List<SqlSuggestion>? suggestions = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < tokens.Count; index++)
        {
            if (!tokens[index - 1].IsKeyword("COLLATE"))
            {
                continue;
            }

            var name = tokens[index];

            // 游標自己那一格是空的，而 DATABASE_DEFAULT 這兩個字已經在封閉清單裡；
            // 再放一份會讓同一個名稱在清單上出現兩次。加引號的識別字不是定序名稱。
            if (name.Kind != SqlTokenKind.Identifier ||
                name.IsQuoted ||
                SqlCollationCatalog.TryGetDescription(name.Value, out _) ||
                !seen.Add(name.Value))
            {
                continue;
            }

            (suggestions ??= new List<SqlSuggestion>()).Add(new SqlSuggestion(
                name.Value,
                name.Value,
                Description,
                Description,
                SuggestionKind.CollationInUse));
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }
}
