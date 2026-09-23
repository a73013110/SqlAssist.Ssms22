using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;
using Xunit;

namespace SqlAssist.Core.Tests.Search;

/// <summary>
/// 名稱與資料行怎麼比：一個修飾都沒開走模糊比對，開了任一個就換成字面比對。
/// </summary>
public sealed class SearchIdentifierMatchTests
{
    [Fact]
    public void 沒開修飾時字母可以散在名稱各處()
    {
        var match = SearchIdentifierMatch.Match(new SearchQuery("libr"), "Lib_Reader");

        Assert.True(match.IsMatch);
    }

    /// <remarks>
    /// <c>DF_Lib_Reader_NoticeIsShown</c> 湊得出 <c>finish</c> 的每一個字母，而使用者把兩顆修飾
    /// 都開著、範圍也縮到只剩條件約束時要的不是這一筆。
    /// </remarks>
    [Fact]
    public void 開了修飾就不收湊得出來的那一種命中()
    {
        var query = new SearchQuery("finish", options: TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord);

        Assert.False(SearchIdentifierMatch.Match(query, "DF_Lib_Reader_NoticeIsShown").IsMatch);
        Assert.True(SearchIdentifierMatch.Match(new SearchQuery("finish"), "DF_Lib_Reader_NoticeIsShown").IsMatch);
    }

    [Fact]
    public void 區分大小寫時逐字相同才收()
    {
        var query = new SearchQuery("publisher", options: TextMatchOptions.MatchCasing);

        Assert.False(SearchIdentifierMatch.Match(query, "PUBLISHER").IsMatch);
        Assert.True(SearchIdentifierMatch.Match(query, "Cat_publisher").IsMatch);
    }

    /// <summary>底線是識別字的一部分，與定義本文那一邊同一條詞界規則。</summary>
    [Fact]
    public void 只取整個字時底線黏著的那一段不算整個字()
    {
        var query = new SearchQuery("CopyNo", options: TextMatchOptions.WholeWord);

        Assert.False(SearchIdentifierMatch.Match(query, "DF_Loan_CopyNo").IsMatch);
        Assert.True(SearchIdentifierMatch.Match(query, "CopyNo").IsMatch);
        Assert.True(SearchIdentifierMatch.Match(query, "#CopyNo").IsMatch);
    }

    /// <summary>區段標的是打進去的那個字整段，不是湊得出它的那幾個字母。</summary>
    [Fact]
    public void 字面命中的區段是連續的一整段()
    {
        var query = new SearchQuery("CopyNo", options: TextMatchOptions.MatchCasing);

        var span = Assert.Single(SearchIdentifierMatch.Match(query, "DF_Loan_CopyNo").Spans);

        Assert.Equal(8, span.Start);
        Assert.Equal(6, span.Length);
    }

    /// <summary>同一個字出現好幾次就標好幾段，重疊的併成一段。</summary>
    [Fact]
    public void 重疊的字面命中併成一段()
    {
        var query = new SearchQuery("aa", options: TextMatchOptions.MatchCasing);

        var span = Assert.Single(SearchIdentifierMatch.Match(query, "aaa").Spans);

        Assert.Equal(0, span.Start);
        Assert.Equal(3, span.Length);
    }

    /// <summary>沒有輸入的那一輪是「列出全部」，不是「每一個都字面命中空字串」。</summary>
    [Fact]
    public void 沒有輸入時開著修飾也照樣列得出候選()
    {
        var query = new SearchQuery("", options: TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord);

        Assert.True(SearchIdentifierMatch.Match(query, "Lib_Reader").IsMatch);
    }

    /// <summary>字面命中仍然向模糊比對要分數：打開修飾不該讓剩下那幾筆的相對順序也換一份。</summary>
    [Fact]
    public void 詞首命中的分數仍高於詞中命中()
    {
        var query = new SearchQuery("Copy", options: TextMatchOptions.MatchCasing);

        var head = SearchIdentifierMatch.Match(query, "Copy");
        var middle = SearchIdentifierMatch.Match(query, "Cat_BookCopy");

        Assert.True(head.Score > middle.Score, $"{head.Score} 應大於 {middle.Score}");
    }
}
