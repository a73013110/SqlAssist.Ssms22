using System.Collections.Generic;
using System.Text;
using SqlAssist.Core.Localization;
using SqlAssist.Metadata.Dictionary;

namespace SqlAssist.Ssms22.Dictionary;

/// <summary>由幾個鍵組出來的那幾句；鍵本身在 <c>DictionaryText.*.resjson</c>。</summary>
/// <remarks>
/// 產生器的方法只收佔位符的值，而這幾句要先看旗標或同名群組決定用哪幾個鍵——那個判斷寫在這裡，
/// 列物件與測試讀的是同一份。
/// </remarks>
public static partial class DictionaryText
{
    /// <summary>主索引鍵、識別、計算資料行，照這個順序用清單分隔符號串起來；都不是時留空。</summary>
    internal static string Traits(bool isPrimaryKey, bool isIdentity, bool isComputed)
    {
        var traits = new List<string>(3);
        if (isPrimaryKey) traits.Add(PrimaryKeyTrait);
        if (isIdentity) traits.Add(IdentityTrait);
        if (isComputed) traits.Add(ComputedTrait);
        return string.Join(CommonText.ListSeparator, traits);
    }

    /// <summary>同名那一格：只有自己一個時留空，一致時只寫個數，不一致時加上哪裡不一樣。</summary>
    internal static string SameNameSummary(SqlColumnNameGroup? group)
    {
        if (group is null || group.Count < 2) return "";
        if (!group.IsInconsistent) return SameNameCount(group.Count);

        var issue = group.HasMixedTypes && group.HasMixedDescriptions ? MixedBoth
            : group.HasMixedTypes ? MixedTypes
            : MixedDescriptions;
        return SameNameIssue(group.Count, issue);
    }

    /// <summary>停在同名那一格時的說明：個數、型別分布、每一種寫法，以及幾個沒寫。</summary>
    /// <remarks>型別與說明都照出現次數排，最常見的在前面：那一句通常就是要照抄的版本。</remarks>
    internal static string SameNameDetail(SqlColumnNameGroup? group)
    {
        if (group is null || group.Count < 2) return "";

        var text = new StringBuilder(SameNameDetailTitle(group.Name, group.Count));
        text.Append('\n').Append(SameNameDetailTypes(string.Join(CommonText.ListSeparator, group.Types)));

        if (group.Descriptions.Count > 0)
        {
            text.Append('\n').Append(SameNameDetailDescriptions);
            foreach (var description in group.Descriptions) text.Append("\n· ").Append(description);
        }

        if (group.MissingDescriptions > 0) text.Append('\n').Append(SameNameDetailMissing(group.MissingDescriptions));
        return text.ToString();
    }
}
