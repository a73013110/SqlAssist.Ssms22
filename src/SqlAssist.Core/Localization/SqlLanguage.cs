using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace SqlAssist.Core.Localization;

/// <summary>SqlAssist 自製介面的一種語言。</summary>
/// <remarks>
/// 語言表來自產生器寫進本組件的 <see cref="SqlTextLanguagesAttribute"/>，與每一句文字陣列的
/// 索引順序同一個來源。第一個是撰寫用的來源語言，也是設定生效前的預設。
/// </remarks>
public sealed class SqlLanguage
{
    private const string FallbackName = "en";

    private SqlLanguage(string name, int index)
    {
        Name = name;
        Index = index;
        Culture = CultureInfo.GetCultureInfo(name);
    }

    public static IReadOnlyList<SqlLanguage> All { get; } = Load();

    /// <summary>撰寫用的來源語言，決定 .resjson 有哪些鍵與佔位符。</summary>
    public static SqlLanguage Source => All[0];

    /// <summary>BCP 47 名稱，例如 <c>zh-Hant</c>。</summary>
    public string Name { get; }

    /// <summary>在每一句文字陣列裡的位置。</summary>
    public int Index { get; }

    /// <summary>格式化數字與日期用的文化。</summary>
    public CultureInfo Culture { get; }

    public static SqlLanguage? Find(string name) =>
        All.FirstOrDefault(language => string.Equals(language.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>「跟隨 SSMS」時，由宿主的介面文化挑出最接近的語言。</summary>
    /// <remarks>
    /// 先沿文化的父系往上找（zh-TW → zh-Hant、en-GB → en），再比兩字母語言碼
    /// （zh-CN 讀繁體仍比讀英文近），都沒有就用英文。
    /// </remarks>
    public static SqlLanguage Match(CultureInfo uiCulture)
    {
        for (var culture = uiCulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            if (Find(culture.Name) is { } exact)
            {
                return exact;
            }
        }

        return All.FirstOrDefault(language => string.Equals(
                   language.Culture.TwoLetterISOLanguageName,
                   uiCulture.TwoLetterISOLanguageName,
                   StringComparison.OrdinalIgnoreCase))
            ?? Find(FallbackName)
            ?? Source;
    }

    public override string ToString() => Name;

    [Localizable(false)]
    private static IReadOnlyList<SqlLanguage> Load()
    {
        var names = typeof(SqlLanguage).Assembly.GetCustomAttribute<SqlTextLanguagesAttribute>()?.Names;
        if (names is null || names.Length == 0)
        {
            throw new InvalidOperationException("SqlAssist.Core 缺少產生器寫入的語言表，檢查 SqlAssist.TextGenerator 是否有接上。");
        }

        return names.Select((name, index) => new SqlLanguage(name, index)).ToArray();
    }
}
