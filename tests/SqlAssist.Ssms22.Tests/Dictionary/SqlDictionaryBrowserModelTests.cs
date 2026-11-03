using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Metadata.Dictionary;
using SqlAssist.Ssms22.Dictionary;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Dictionary;

/// <summary>資料字典工具窗的狀態：兩種檢視共用過濾與編輯、編輯照資料庫各記一份、重新載入收尾。</summary>
public sealed class SqlDictionaryBrowserModelTests
{
    private const string Server = "LIBSQL01";

    private static readonly SqlDescriptionTarget LoanReaderNo =
        new(SqlDictionaryObjectKind.Table, "dbo", "Loan", "ReaderNo");

    /// <summary>圖書館的三張表：讀者編號在兩張表上型別不一樣，借閱主檔的那一格還沒寫說明。</summary>
    private static SqlDataDictionary Library(string? loanReaderNo = null) => new("LibArchive", new[]
    {
        new SqlDictionaryObject(1, "dbo", "Lib_Reader", SqlDictionaryObjectKind.Table, "讀者", new[]
        {
            new SqlDictionaryColumn(1, "ReaderNo", "varchar(10)", false, isPrimaryKey: true, description: "讀者編號"),
            new SqlDictionaryColumn(2, "ReaderName", "nvarchar(50)", false, description: "讀者姓名"),
        }),
        new SqlDictionaryObject(2, "dbo", "Loan", SqlDictionaryObjectKind.Table, null, new[]
        {
            new SqlDictionaryColumn(1, "LoanNo", "int", false, isPrimaryKey: true, isIdentity: true),
            new SqlDictionaryColumn(2, "ReaderNo", "varchar(12)", false, description: loanReaderNo),
        }),
        new SqlDictionaryObject(3, "dbo", "Copy", SqlDictionaryObjectKind.Table, "館藏複本", new[]
        {
            new SqlDictionaryColumn(1, "CopyNo", "int", false, isPrimaryKey: true, description: "複本編號"),
        }),
    });

    private static SqlDictionaryBrowserModel Loaded(string database = "LibArchive")
    {
        var model = new SqlDictionaryBrowserModel();
        model.Scope.SetServerSelected(Server, selected: true);
        model.Scope.SetDatabaseSelected(database, selected: true);
        model.UseDatabase(Server, database);
        model.Load(Library());
        return model;
    }

    /// <summary>過濾只有一份：主從的主清單、明細與資料字典檢視照同一條規則留列。</summary>
    [Fact]
    public void 兩種檢視照同一份過濾()
    {
        var model = Loaded();
        model.Filter = new SqlDictionaryFilter("readerno");

        Assert.Equal(new[] { "dbo.Lib_Reader", "dbo.Loan" }, model.MatchingObjects().Select(row => row.Name));
        Assert.Equal(new[] { "ReaderNo" }, model.ColumnsOf(model.MatchingObjects()[1].Object).Select(row => row.Name));
        Assert.Equal(
            new[] { "dbo.Lib_Reader", "dbo.Loan" },
            model.MatchingColumns().Select(row => row.ObjectName));
    }

    /// <summary>資料字典檢視照名稱排、不分大小寫，同名的排在一起，看得出型別不一致。</summary>
    [Fact]
    public void 資料字典檢視照名稱排並標出同名不一致()
    {
        var model = Loaded();

        var rows = model.MatchingColumns();
        Assert.Equal(new[] { "CopyNo", "LoanNo", "ReaderName", "ReaderNo", "ReaderNo" }, rows.Select(row => row.Name));

        var loan = rows.Single(row => row.Target.Equals(LoanReaderNo));
        Assert.True(loan.IsInconsistent);
        Assert.Equal(DictionaryText.SameNameIssue(2, DictionaryText.MixedTypes), loan.SameName);
        Assert.Equal("讀者編號", loan.Suggestion);
        Assert.Equal("", rows.Single(row => row.Name == "CopyNo").SameName);
    }

    /// <summary>照抄建議之後同名那幾格一起更新：建議消失、說明一致。</summary>
    [Fact]
    public void 照抄同名說明之後同名的每一列一起更新()
    {
        var model = Loaded();
        var rows = model.MatchingColumns();
        var loan = rows.Single(row => row.Target.Equals(LoanReaderNo));
        var changed = new List<string>();
        loan.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        Assert.Equal(1, model.ApplySuggestions(rows));

        Assert.Equal("讀者編號", loan.Description);
        Assert.True(loan.IsChanged);
        Assert.Null(loan.Suggestion);
        Assert.Contains(nameof(SqlDictionaryColumnRow.Suggestion), changed);
        Assert.Equal(SqlDictionaryColumnRow.ChangedSymbol, loan.ChangeMark);
        Assert.Equal(SqlDictionaryColumnRow.ChangedSymbol, model.MatchingObjects().Single(row => row.Name == "dbo.Loan").ChangeMark);
    }

    /// <summary>換資料庫不必問要不要放棄：編輯照伺服器＋資料庫各記一份，切回來還在。</summary>
    [Fact]
    public void 編輯照資料庫各記一份()
    {
        var model = Loaded();
        model.SetDescription(LoanReaderNo, null, "借閱讀者");
        Assert.Equal(1, model.Edits.Count);

        model.UseDatabase(Server, "LibStaging");
        Assert.Equal(0, model.Edits.Count);
        Assert.Null(model.Dictionary);

        model.UseDatabase(Server, "LibArchive");
        Assert.Equal(1, model.Edits.Count);
    }

    /// <summary>重新載入就是收尾：伺服器上已經是那一句的不再算變更，其他的留著。</summary>
    [Fact]
    public void 重新載入之後已經生效的變更收掉()
    {
        var model = Loaded();
        model.SetDescription(LoanReaderNo, null, "借閱讀者");
        model.SetDescription(new SqlDescriptionTarget(SqlDictionaryObjectKind.Table, "dbo", "Copy"), "館藏複本", "複本");
        var raised = 0;
        model.EditsChanged += (_, _) => raised++;

        model.Load(Library(loanReaderNo: "借閱讀者"));

        Assert.Equal("dbo.Copy", Assert.Single(model.Edits.Changes).Target.ToString());
        Assert.Equal(1, raised);
    }

    /// <summary>被擋下之後換上伺服器現在的值當原值，下一次寫回就是看過之後的覆蓋。</summary>
    [Fact]
    public void 被擋下的變更換上伺服器現在的值當原值()
    {
        var model = Loaded();
        model.SetDescription(LoanReaderNo, null, "借閱讀者");
        var change = Assert.Single(model.Edits.Changes);

        model.Rebase(new[] { new SqlDescriptionConflict(change, "別人寫的") });

        var rebased = Assert.Single(model.Edits.Changes);
        Assert.Equal("別人寫的", rebased.Original);
        Assert.Equal("借閱讀者", rebased.Value);
    }

    /// <summary>從試算表貼兩欄：照名稱填同名的每一列，標頭算略過。</summary>
    [Fact]
    public void 貼上兩欄照名稱填()
    {
        var model = Loaded();
        var rows = model.MatchingColumns();

        var (applied, skipped) = model.Paste(rows, 0, "資料行\t說明\r\nLoanNo\t借閱編號\r\nLoan.ReaderNo\t借閱讀者\r\n");

        Assert.Equal(2, applied);
        Assert.Equal(1, skipped);
        Assert.Equal("借閱讀者", rows.Single(row => row.Target.Equals(LoanReaderNo)).Description);
    }

    [Fact]
    public void 頁尾與狀態表面照範圍與結果決定()
    {
        var model = new SqlDictionaryBrowserModel();
        Assert.Equal(SqlSurfaceState.Empty(DictionaryText.ChooseServer, DictionaryText.ChooseServerDetail, DictionaryText.ChooseServerAction),
            model.Surface(loading: false, failed: false, visibleRows: 0));

        model.Scope.SetServerSelected(Server, selected: true);
        Assert.Equal(DictionaryText.ChooseDatabase, model.Surface(false, false, 0).Title);

        model = Loaded();
        Assert.Equal(SqlSurfaceState.None, model.Surface(false, false, 3));
        Assert.Equal(DictionaryText.NoMatch, model.Surface(false, false, 0).Title);
        Assert.Equal(SqlSurfaceState.UnreadableTitle, model.Surface(false, failed: true, 0).Title);

        Assert.Equal(DictionaryText.Counts(3, 5), model.FooterSummary(3, 5));
        model.SetDescription(LoanReaderNo, null, "借閱讀者");
        Assert.Equal(DictionaryText.CountsWithChanges(3, 5, 1), model.FooterSummary(3, 5));
    }

    [Fact]
    public void 特性與同名說明的文字()
    {
        Assert.Equal("", DictionaryText.Traits(false, false, false));
        Assert.Equal(
            DictionaryText.PrimaryKeyTrait + SqlAssist.Core.Localization.CommonText.ListSeparator + DictionaryText.IdentityTrait,
            DictionaryText.Traits(true, true, false));

        var model = Loaded();
        var detail = model.MatchingColumns().Single(row => row.Target.Equals(LoanReaderNo)).SameNameDetail;
        Assert.Contains("varchar(10)", detail, StringComparison.Ordinal);
        Assert.Contains("varchar(12)", detail, StringComparison.Ordinal);
        Assert.Contains("讀者編號", detail, StringComparison.Ordinal);
        Assert.Contains(DictionaryText.SameNameDetailMissing(1), detail, StringComparison.Ordinal);
    }
}
