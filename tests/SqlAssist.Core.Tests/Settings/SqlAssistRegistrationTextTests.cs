using System;
using System.Linq;
using System.Text.RegularExpressions;
using SqlAssist.Core.Localization;
using Xunit;

namespace SqlAssist.Core.Tests.Settings;

/// <summary>
/// 設定頁的顯示文字：註冊檔只放 "@鍵;{packageGuid}"，文字在 <c>SettingsPageText.&lt;語言&gt;.resjson</c>。
/// </summary>
/// <remarks>
/// 引用寫錯時 SSMS 不報錯，只把 "@鍵;{guid}" 原樣畫在設定頁上；建置時的 SQLSET006 擋鍵不存在，
/// 這裡再擋寫成純文字、套件 GUID 不對與兩種語言對不齊。
/// </remarks>
public sealed class SqlAssistRegistrationTextTests
{
    [Fact]
    public void 顯示文字一律是本套件的資源引用()
    {
        var violations = RegistrationManifest.DisplayTexts()
            .Where(text => RegistrationManifest.ParseReference(text.Value) is not { } reference ||
                !string.Equals(reference.Guid, RegistrationManifest.PackageGuid, StringComparison.OrdinalIgnoreCase))
            .Select(text => $"{text.Path}：{text.Value}")
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    /// <summary>每一種語言都剛好有註冊檔引用到的那些鍵：缺的會畫成原始引用，多的是沒人用的譯文。</summary>
    [Fact]
    public void 各語言的鍵與註冊檔引用一致()
    {
        var referenced = RegistrationManifest.DisplayTexts()
            .Select(text => RegistrationManifest.ParseReference(text.Value)?.Key)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var language in SqlLanguage.All)
        {
            var keys = RegistrationManifest.Texts[language.Name].Keys.ToHashSet(StringComparer.Ordinal);

            Assert.True(
                keys.SetEquals(referenced),
                $"{language.Name} 缺少：{string.Join("、", referenced.Except(keys))}；多出：{string.Join("、", keys.Except(referenced))}");
        }
    }

    [Fact]
    public void 各語言的文字都不是空的()
    {
        var violations = SqlLanguage.All
            .SelectMany(language => RegistrationManifest.Texts[language.Name]
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{language.Name}：{pair.Key}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    /// <summary>英文是中性資源，SSMS 不是繁中介面時都退回它；漏翻的中文會直接露在英文設定頁上。</summary>
    [Fact]
    public void 英文文字不含中文()
    {
        var violations = RegistrationManifest.Texts["en"]
            .Where(pair => Regex.IsMatch(pair.Value, @"[　-鿿＀-￯]"))
            .Select(pair => $"{pair.Key}：{pair.Value}")
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    [Theory]
    [InlineData("sqlAssist.general.enabled", "title", "zh-Hant", "啟用 SqlAssist")]
    [InlineData("sqlAssist.general.enabled", "title", "en", "Enable SqlAssist")]
    [InlineData("sqlAssist.general", "title", "en", "General")]
    public void 解析出設定頁上的文字(string moniker, string field, string language, string expected)
    {
        Assert.Equal(expected, RegistrationManifest.Text(moniker, field, language));
    }
}
