using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Updates;
using Xunit;

namespace SqlAssist.Core.Tests.Localization;

/// <summary>
/// 語言表由產生器依 MSBuild 屬性寫進組件；這裡鎖住「來源語言在第一個」與「跟隨 SSMS」的挑法。
/// 測試一律用 <see cref="SqlText.Use"/> 固定語言，不動全域狀態，平行執行的其他測試仍看到來源語言。
/// </summary>
public sealed class SqlLanguageTests
{
    private static SqlLanguage English => SqlLanguage.Find("en")!;

    private static SqlLanguage Chinese => SqlLanguage.Find("zh-Hant")!;

    [Fact]
    public void 語言表以繁體中文為來源語言()
    {
        Assert.Equal(new[] { "zh-Hant", "en" }, SqlLanguage.All.Select(language => language.Name));
        Assert.Same(Chinese, SqlLanguage.Source);
        Assert.Equal(new[] { 0, 1 }, SqlLanguage.All.Select(language => language.Index));
    }

    [Theory]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hant")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("de-DE", "en")]
    public void 跟隨SSMS時挑出最接近的語言(string uiCulture, string expected)
    {
        Assert.Equal(expected, SqlLanguage.Match(CultureInfo.GetCultureInfo(uiCulture)).Name);
    }

    [Fact]
    public void 範圍內改用指定語言離開後還原()
    {
        Assert.Same(SqlLanguage.Source, SqlText.Current);

        using (SqlText.Use(English))
        {
            Assert.Same(English, SqlText.Current);
            Assert.Equal("You're on the latest version, 1.2.3.", UpdateText.UpToDate("1.2.3"));
        }

        Assert.Same(SqlLanguage.Source, SqlText.Current);
        Assert.Equal("已是最新版 1.2.3。", UpdateText.UpToDate("1.2.3"));
    }

    [Fact]
    public async Task 範圍跟著await走()
    {
        using (SqlText.Use(English))
        {
            await Task.Yield();
            Assert.Same(English, SqlText.Current);
        }
    }

    [Fact]
    public void 佔位符依目前語言的文化格式化()
    {
        var values = new[] { "{0:N1}", "{0:N1}" };
        using (SqlText.Use(English))
        {
            Assert.Equal(1234.5.ToString("N1", English.Culture), SqlText.Format(values, 1234.5));
        }
    }

    [Fact]
    public void 找不到的語言名稱回傳空值()
    {
        Assert.Null(SqlLanguage.Find("fr"));
        Assert.Throws<ArgumentNullException>(() => SqlText.Use(null!));
    }
}

/// <summary>切換全域語言會影響同時執行的其他測試，所以獨立成不平行的集合。</summary>
[CollectionDefinition(nameof(SqlTextGlobalLanguageTests), DisableParallelization = true)]
[Collection(nameof(SqlTextGlobalLanguageTests))]
public sealed class SqlTextGlobalLanguageTests
{
    [Fact]
    public void 真的換了語言才發出變更事件()
    {
        var english = SqlLanguage.Find("en")!;
        var raised = 0;
        void OnChanged(object? sender, EventArgs e) => raised++;

        SqlText.Changed += OnChanged;
        try
        {
            Assert.True(SqlText.SetLanguage(english));
            Assert.False(SqlText.SetLanguage(english));
            Assert.Equal(1, raised);
            Assert.Equal("Couldn't check for the latest version. Try again later, or visit the GitHub releases page.", UpdateText.Unknown);
        }
        finally
        {
            SqlText.Changed -= OnChanged;
            SqlText.SetLanguage(SqlLanguage.Source);
        }
    }
}
