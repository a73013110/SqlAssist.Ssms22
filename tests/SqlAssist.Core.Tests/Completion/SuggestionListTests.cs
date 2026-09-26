using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

[Collection(nameof(SqlSuggestionUsageCollection))]
public sealed class SuggestionListTests
{
    private static SqlSuggestion Table(string name, string schema = "dbo")
    {
        return new SqlSuggestion(
            name,
            $"[{schema}].[{name}]",
            $"Table · {schema}",
            $"Table {name}",
            SuggestionKind.Table,
            schemaName: schema);
    }

    private static SqlSuggestion Procedure(string name, string schema = "dbo")
    {
        return new SqlSuggestion(
            name,
            $"[{schema}].[{name}]",
            $"Procedure · {schema}",
            $"Procedure {name}",
            SuggestionKind.Procedure,
            schemaName: schema);
    }

    private static SqlSuggestion Column(string name)
    {
        return new SqlSuggestion(
            name,
            name,
            "varchar(20) NOT NULL",
            name,
            SuggestionKind.Column);
    }

    /// <summary>
    /// 沒有限定字的位置也要看得到欄位：SELECT | FROM PUBLISHER a 這種情形，
    /// 使用者要的幾乎都是欄位而不是整個資料庫的物件清單。
    /// </summary>
    [Fact]
    public void 沒有限定字時仍建議欄位()
    {
        var names = RankNames(
            new[] { Table("PUBL_Log"), Column("PUBL_CODE") },
            "SELECT publ");

        Assert.Contains("PUBL_CODE", names);
    }

    [Fact]
    public void 分數相同時欄位排在資料表之前()
    {
        var names = RankNames(
            new[] { Table("PUBLCODE"), Column("PUBLCODE") },
            "SELECT publcode");

        Assert.Equal("PUBLCODE", names[0]);
        Assert.Equal(SuggestionKind.Column, SuggestionListProbe
            .Match(new[] { Table("PUBLCODE"), Column("PUBLCODE") },
                SqlCompletionContextAnalyzer.Analyze("SELECT publcode"))[0]
            .Kind);
    }

    /// <summary>
    /// FROM 之後只能是資料來源，欄位不該出現在那裡。
    /// </summary>
    [Fact]
    public void 資料來源位置不建議欄位()
    {
        var names = RankNames(
            new[] { Table("PUBLISHER"), Column("PUBL_CODE") },
            "SELECT * FROM publ");

        Assert.Equal(new[] { "PUBLISHER" }, names);
    }

    private static IReadOnlyList<string> RankNames(
        IEnumerable<SqlSuggestion> suggestions,
        string textBeforeCaret)
    {
        return SuggestionListProbe
            .Match(suggestions, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret))
            .Select(item => item.DisplayText)
            .ToArray();
    }

    /// <summary>
    /// 使用者回報的情境：輸入 libr 時，Lib_Reader 必須是第一順位，
    /// 不必打到 lib_re 才浮上來。
    /// </summary>
    [Fact]
    public void 輸入libr時Lib_Reader排第一()
    {
        var candidates = new[]
        {
            Table("Lib_Reader"),
            Table("MyLibrTable"),
            Table("Lib_ReaderTag"),
            Table("LibBackupRecord"),
            Table("Lib_Tag")
        };

        var ranked = RankNames(candidates, "SELECT * FROM libr");

        Assert.Equal("Lib_Reader", ranked[0]);
        Assert.DoesNotContain("Lib_Tag", ranked);
    }

    [Fact]
    public void 完全相同的輸入一定排第一()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[] { Table("ssf_Archive"), Table("ssfLog") })
            .ToArray();

        var ranked = RankNames(candidates, "ssf");

        Assert.Equal("ssf", ranked[0]);
    }

    [Fact]
    public void 分數相同時較短的名稱排前面()
    {
        var candidates = new[] { Table("LoanDetailHistory"), Table("Loan") };

        var ranked = RankNames(candidates, "SELECT * FROM loan");

        Assert.Equal("Loan", ranked[0]);
    }

    /// <remarks>
    /// 結構描述是名稱的第二段，也在這個位置；預存程序與片段不在。
    /// </remarks>
    [Fact]
    public void FROM之後只顯示資料來源與結構描述()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[] { Table("Publisher"), Procedure("usp_Publisher") })
            .ToArray();

        var ranked = SuggestionListProbe.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM "));

        Assert.NotEmpty(ranked);
        Assert.All(ranked, item => Assert.Contains(
            item.Item.Kind,
            new[] { SuggestionKind.Table, SuggestionKind.Schema }));
    }

    [Fact]
    public void ALTER_PROCEDURE之後只顯示Procedure()
    {
        var candidates = new[] { Table("Publisher"), Procedure("usp_Publisher") };

        var ranked = SuggestionListProbe.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("ALTER PROCEDURE "));

        Assert.Single(ranked);
        Assert.Equal(SuggestionKind.Procedure, ranked[0].Item.Kind);
    }

    [Fact]
    public void Schema限定後只顯示該Schema的物件()
    {
        var candidates = new[] { Table("Publisher", "dbo"), Table("Publisher", "sales") };

        var ranked = SuggestionListProbe.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM [sales]."));

        Assert.Single(ranked);
        Assert.Equal("sales", ranked[0].Item.SchemaName);
    }

    [Fact]
    public void 回傳結果帶有可供高亮的命中區段()
    {
        var ranked = SuggestionListProbe.Rank(
            new[] { Table("Lib_Reader") },
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr"));

        var match = Assert.Single(ranked);
        Assert.Equal(2, match.Spans.Count);
        Assert.Equal(0, match.Spans[0].Start);
        Assert.Equal(3, match.Spans[0].Length);
        Assert.Equal(4, match.Spans[1].Start);
        Assert.Equal(1, match.Spans[1].Length);
    }

    [Fact]
    public void 有前綴時遵守數量上限且保留分數最高的項目()
    {
        var candidates = Enumerable.Range(0, SuggestionList.MaximumItems + 50)
            .Select(index => Table($"Lib_Reader{index:D3}"))
            .Concat(new[] { Table("Lib_Reader") })
            .ToArray();

        var ranked = SuggestionListProbe.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr"));

        Assert.Equal(SuggestionList.MaximumItems, ranked.Count);
        Assert.Equal("Lib_Reader", ranked[0].Item.DisplayText);
    }

    [Fact]
    public void 分數由高到低排列()
    {
        var candidates = new[]
        {
            Table("Lib_Reader"),
            Table("LibBackupRecord"),
            Table("MyLibrTable")
        };

        var ranked = SuggestionListProbe.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr"));

        var scores = ranked.Select(item => item.Score).ToArray();
        Assert.Equal(scores.OrderByDescending(score => score), scores);
    }

    [Fact]
    public void 輸入單一字母時關鍵字與Snippet排在資料表之前()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[] { Table("Lib_Reader") })
            .ToArray();

        var ranked = SuggestionListProbe.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("s"));

        var topKinds = ranked.Take(3).Select(item => item.Item.Kind).ToArray();
        Assert.All(topKinds, kind =>
            Assert.True(kind == SuggestionKind.Keyword || kind == SuggestionKind.Snippet));
    }

    /// <summary>
    /// 類別偏好不該被名稱長度翻掉。
    /// </summary>
    /// <remarks>
    /// 兩者的模糊分數相同（都是首字元詞首命中），差別只在類別與長度。
    /// 曾經長度懲罰與類別加成同一量級，於是長欄位輸給短資料表，
    /// 正好違反「欄位優先於資料表」——而欄位名普遍比資料表名長，那不是特例。
    /// </remarks>
    [Fact]
    public void 長欄位仍排在短資料表之前()
    {
        var names = RankNames(
            new[] { Table("BOOKS"), Column("BOOK_LOAN_HISTORY_DETAIL") },
            "SELECT bo");

        Assert.Equal("BOOK_LOAN_HISTORY_DETAIL", names[0]);
    }

    [Fact]
    public void 名稱長度只在同類別內決定順序()
    {
        var names = RankNames(
            new[] { Column("BOOK_LOAN_HISTORY_DETAIL"), Column("BOOKS") },
            "SELECT bo");

        Assert.Equal("BOOKS", names[0]);
    }

    /// <summary>最近提交過的項目要排到前面。</summary>
    [Fact]
    public void 最近用過的排在同類別的其他項目之前()
    {
        SqlSuggestionUsage.Clear();

        try
        {
            var candidates = new[] { Table("BOOK_A"), Table("BOOK_B") };

            Assert.Equal("BOOK_A", RankNames(candidates, "SELECT * FROM book")[0]);

            SqlSuggestionUsage.Record(Table("BOOK_B"));

            Assert.Equal("BOOK_B", RankNames(candidates, "SELECT * FROM book")[0]);
        }
        finally
        {
            SqlSuggestionUsage.Clear();
        }
    }

    /// <summary>
    /// 最近用過壓不過類別偏好，也壓不過更好的比對品質。
    /// </summary>
    /// <remarks>
    /// 前者：剛提交過的資料表仍然排在敘述自己的欄位之後。這正是編輯的實際順序
    /// ——使用者才剛從清單裡挑完 FROM 後面那幾張表，游標一移到 SET 或 WHERE，
    /// 那幾張表就會帶著加成蓋住他真正要挑的欄位。類別說的是「這個位置文法上要
    /// 什麼」，使用紀錄只是跨敘述的猜測，猜測不該翻過眼前這句話的證據。
    ///
    /// 後者：<c>publ</c> 之下，真正以它開頭的候選項仍要贏過只是包含這幾個字母的
    /// 那一個。
    /// </remarks>
    [Fact]
    public void 最近用過壓不過類別也壓不過比對品質()
    {
        SqlSuggestionUsage.Clear();

        try
        {
            SqlSuggestionUsage.Record(Table("PUBLCODE"));
            Assert.Equal(
                SuggestionKind.Column,
                SuggestionListProbe.Rank(
                    new[] { Table("PUBLCODE"), Column("PUBLCODE") },
                    SqlCompletionContextAnalyzer.Analyze("SELECT publcode"))[0].Item.Kind);

            SqlSuggestionUsage.Clear();
            SqlSuggestionUsage.Record(Table("MyPublisherLog"));
            Assert.Equal(
                "PUBLISHER",
                RankNames(new[] { Table("MyPublisherLog"), Table("PUBLISHER") }, "SELECT * FROM publ")[0]);
        }
        finally
        {
            SqlSuggestionUsage.Clear();
        }
    }

    /// <summary>
    /// 同分時保留候選清單原本的順序。
    /// </summary>
    /// <remarks>
    /// 欄位交進來的順序是資料表的定義順序，那才是使用者對一張表的心智模型；
    /// 以前的字母序 tie-break 會把它打散。
    /// </remarks>
    [Fact]
    public void 同分時保留原本的順序()
    {
        var names = RankNames(
            new[] { Column("N_ZULU"), Column("N_LIMA"), Column("N_MIKE") },
            "SELECT n_");

        Assert.Equal(new[] { "N_ZULU", "N_LIMA", "N_MIKE" }, names);
    }

    private static SqlSuggestion Keyword(string name)
    {
        return new SqlSuggestion(name, name, "Keyword", name, SuggestionKind.Keyword);
    }

    private static SqlSuggestion Snippet(string shortcut, bool isDestructive = false)
    {
        return new SqlSuggestion(shortcut, shortcut, "Snippet", shortcut, SuggestionKind.Snippet, isDestructive: isDestructive);
    }

    private static SqlSuggestion DataType(string name)
    {
        return new SqlSuggestion(name, name, "Type", name, SuggestionKind.DataType);
    }

    private static string[] Names<TItem>(SuggestionListView<TItem> view, Func<TItem, string> nameOf)
    {
        return view.Items.Select(entry => nameOf(entry.Item)).ToArray();
    }

    private static string[] Names(SuggestionListView<SqlSuggestion> view)
    {
        return Names(view, suggestion => suggestion.DisplayText);
    }

    /// <summary>開場排序依類別偏好；同一類別內維持交進來的順序。</summary>
    [Fact]
    public void 開場排序依類別穩定排序()
    {
        SqlSuggestionUsage.Clear();

        var sorted = SuggestionListProbe.Sort(new[]
        {
            Table("Loan"),
            Keyword("SELECT"),
            Column("N_ZULU"),
            Table("Copy"),
            Column("N_LIMA"),
            Snippet("ssf")
        });

        Assert.Equal(
            new[] { "N_ZULU", "N_LIMA", "SELECT", "Loan", "Copy", "ssf" },
            sorted.Select(suggestion => suggestion.DisplayText));
    }

    /// <summary>
    /// 沒有前綴時不評分：再評一次只剩名稱長度會說話，欄位的定義順序就沒了。
    /// </summary>
    [Fact]
    public void 沒有前綴時沿用開場順序()
    {
        var sorted = SuggestionListProbe.Sort(new[]
        {
            Column("LIBRARY_LOAN_HISTORY_DETAIL"),
            Column("ID"),
            Column("CopyNo")
        });

        var view = SuggestionListProbe.Update(sorted, string.Empty);

        Assert.Equal(new[] { "LIBRARY_LOAN_HISTORY_DETAIL", "ID", "CopyNo" }, Names(view));
        Assert.All(view.Items, entry => Assert.Empty(entry.Spans));
    }

    [Fact]
    public void 沒有前綴時沒有數量上限()
    {
        var sorted = SuggestionListProbe.Sort(Enumerable.Range(0, SuggestionList.MaximumItems + 50)
            .Select(index => Table($"T{index:D3}"))
            .ToArray());

        Assert.Equal(SuggestionList.MaximumItems + 50, SuggestionListProbe.Update(sorted, string.Empty).Items.Count);
    }

    [Fact]
    public void 危險片段只在沒有前綴而且沒按分類時隱藏()
    {
        var sorted = SuggestionListProbe.Sort(new[] { Snippet("df", isDestructive: true), Snippet("ssf"), Table("Loan") });

        Assert.Equal(new[] { "Loan", "ssf" }, Names(SuggestionListProbe.Update(sorted, string.Empty)));
        Assert.Equal(
            new[] { "df", "ssf" },
            Names(SuggestionListProbe.Update(sorted, string.Empty, SuggestionCategorySet.Of(SuggestionCategory.Snippet))));
        Assert.Contains("df", Names(SuggestionListProbe.Update(sorted, "df")));
    }

    /// <remarks>
    /// 隱藏是呈現規則，不是比對結果：只剩危險片段的那一類仍算有命中，按下那顆才叫得出來。
    /// </remarks>
    [Fact]
    public void 沒有前綴時每一類都算有命中()
    {
        var sorted = SuggestionListProbe.Sort(new[] { Snippet("df", isDestructive: true), Table("Loan") });

        var view = SuggestionListProbe.Update(sorted, string.Empty);

        Assert.Equal(SuggestionCategorySet.Of(SuggestionCategory.Table, SuggestionCategory.Snippet), view.Matched);
        Assert.True(view.Applied.IsEmpty);
        Assert.Equal(new[] { "Loan" }, Names(view));
    }

    [Fact]
    public void 按著的分類與命中取交集()
    {
        var sorted = SuggestionListProbe.Sort(new[] { Table("Loan"), Column("LoanNo"), Keyword("LOCK"), DataType("LONG") });

        var view = SuggestionListProbe.Update(
            sorted,
            "lo",
            SuggestionCategorySet.Of(SuggestionCategory.Table, SuggestionCategory.View));

        Assert.Equal(SuggestionCategorySet.Of(SuggestionCategory.Table), view.Applied);
        Assert.Equal(
            SuggestionCategorySet.Of(SuggestionCategory.Table, SuggestionCategory.Column, SuggestionCategory.Keyword),
            view.Matched);

        // 不參與分類篩選的種類留下，不因為使用者按了按鈕而無聲消失。
        Assert.Equal(new[] { "LONG", "Loan" }, Names(view).OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>按著的分類打字後沒有命中就跳起來，清單回到全部而不是被篩空。</summary>
    [Fact]
    public void 按著的分類沒有命中時回到全部()
    {
        var sorted = SuggestionListProbe.Sort(new[] { Table("Loan"), Column("CopyNo"), Column("CopyId") });

        var view = SuggestionListProbe.Update(sorted, "copy", SuggestionCategorySet.Of(SuggestionCategory.Table));

        Assert.True(view.Applied.IsEmpty);
        Assert.Equal(SuggestionCategorySet.Of(SuggestionCategory.Column), view.Matched);
        Assert.Equal(2, view.Items.Count);
    }

    [Fact]
    public void 一項都沒命中時清單為空()
    {
        var sorted = SuggestionListProbe.Sort(new[] { Table("Loan"), Column("CopyNo") });

        var view = SuggestionListProbe.Update(sorted, "xyz");

        Assert.True(view.IsEmpty);
        Assert.True(view.Matched.IsEmpty);
    }

    /// <summary>
    /// 別的來源的項目沒有建議項：照同一套尺度排名，只是沒有類別那一層。
    /// </summary>
    /// <remarks>
    /// 比對品質明顯較好的仍排在前面；比對品質打平時，本擴充的項目靠類別加成在前。
    /// 曾經這一類只乘 128，比對品質再好都沉在最底。
    /// </remarks>
    [Fact]
    public void 別的來源的項目與建議項同一尺度排名()
    {
        var items = new[]
        {
            new ForeignItem("MyLibrTable", Table("MyLibrTable")),
            new ForeignItem("Lib_Reader", null),
            new ForeignItem("Loan", Table("Loan")),
            new ForeignItem("Loan", null)
        };
        var sorted = SuggestionList.Sort(items, item => item.Suggestion);

        var libr = ForeignItem.Update(sorted, "libr", SuggestionCategorySet.Empty);
        Assert.Equal(new[] { "Lib_Reader", "MyLibrTable" }, Names(libr, item => item.Text));

        var loan = ForeignItem.Update(sorted, "loan", SuggestionCategorySet.Empty);
        Assert.NotNull(loan.Items[0].Item.Suggestion);
        Assert.Null(loan.Items[1].Item.Suggestion);
    }

    [Fact]
    public void 別的來源的項目不受分類篩選影響()
    {
        var items = new[]
        {
            new ForeignItem("Loan", Table("Loan")),
            new ForeignItem("LoanNo", Column("LoanNo")),
            new ForeignItem("LoanX", null)
        };
        var sorted = SuggestionList.Sort(items, item => item.Suggestion);

        var view = ForeignItem.Update(sorted, "loan", SuggestionCategorySet.Of(SuggestionCategory.Column));

        Assert.Equal(new[] { "LoanNo", "LoanX" }, Names(view, item => item.Text).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void 片段欄位的預設值視為空前綴()
    {
        Assert.Equal(string.Empty, SuggestionList.TypedText("dbo.TargetTable", "dbo.TargetTable"));
        Assert.Equal("dbo.T", SuggestionList.TypedText("dbo.T", "dbo.TargetTable"));
        Assert.Equal("libr", SuggestionList.TypedText("libr", null));
    }

    /// <summary>有前綴時的高亮區段是比對器給的那一份；輸入的大小寫不影響。</summary>
    [Fact]
    public void 命中區段與比對器一致()
    {
        var view = SuggestionListProbe.Update(new[] { Table("Lib_Reader") }, "LIBR");

        Assert.Equal(FuzzyMatcher.Match("libr", "Lib_Reader").Spans, Assert.Single(view.Items).Spans);
    }

    /// <summary>候選把分類與危險旗標擠進位置的高位；每一種分類都要原樣讀得回來。</summary>
    [Fact]
    public void 候選的分類與旗標讀得回來()
    {
        foreach (SuggestionCategory category in Enum.GetValues(typeof(SuggestionCategory)))
        {
            var candidate = new SuggestionList.Candidate(12_345, 7, Array.Empty<MatchSpan>(), category, isDestructive: true);

            Assert.Equal(category, candidate.Category);
            Assert.True(candidate.IsDestructive);
            Assert.Equal(12_345, candidate.Order);
        }

        var plain = new SuggestionList.Candidate(0, 0, Array.Empty<MatchSpan>(), null, isDestructive: false);
        Assert.Null(plain.Category);
        Assert.False(plain.IsDestructive);
    }

    private sealed class ForeignItem
    {
        public ForeignItem(string text, SqlSuggestion? suggestion)
        {
            Text = text;
            Suggestion = suggestion;
        }

        public string Text { get; }

        public SqlSuggestion? Suggestion { get; }

        public static SuggestionListView<ForeignItem> Update(
            IReadOnlyList<ForeignItem> sorted,
            string typedText,
            SuggestionCategorySet selected)
        {
            return SuggestionList.Update(
                sorted,
                item => item.Text,
                item => item.Suggestion,
                typedText,
                selected,
                TestContext.Current.CancellationToken);
        }
    }
}
