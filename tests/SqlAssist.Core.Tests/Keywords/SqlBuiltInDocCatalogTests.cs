using System;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

public sealed class SqlBuiltInDocCatalogTests
{
    /// <summary>
    /// 提示視窗不會自己斷行，說明超過這個長度就會被畫成省略號。
    /// </summary>
    /// <remarks>
    /// 值取自 <c>Metadata/Formatting/SqlDescriptionText.DefaultMaximumLength</c>；
    /// Core 的測試看不到 Metadata，所以在這裡重寫一次。兩邊分歧的代價只是多一個省略號，
    /// 不是錯誤——真正要守的是「我們自己寫的說明不該被自己截斷」。
    /// </remarks>
    private const int MaximumSummaryLength = 60;

    /// <summary>範例是一行程式碼，太寬的提示會被螢幕邊界切掉。</summary>
    private const int MaximumExampleLength = 90;

    [Fact]
    public void 內建說明資源載入成功()
    {
        Assert.Null(SqlBuiltInDocCatalog.LastError);
        Assert.NotEmpty(SqlBuiltInDocCatalog.DocumentedNames);
    }

    [Theory]
    [InlineData("CONVERT")]
    [InlineData("convert")]
    [InlineData("DATEDIFF")]
    public void 函式帶回簽章用途與範例(string name)
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet(name, SqlBuiltInKind.Function, out var doc));

        Assert.Equal(name.ToUpperInvariant(), doc.Name);
        Assert.Equal(SqlBuiltInKind.Function, doc.Kind);
        Assert.NotEqual(string.Empty, doc.Signature);
        Assert.NotEqual(string.Empty, doc.Summary);
        Assert.NotEqual(string.Empty, doc.Example);
        Assert.StartsWith("https://learn.microsoft.com/", doc.DocsUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// 與關鍵字重疊的名稱在建議清單裡讓給了關鍵字目錄，但提示照樣答得出來。
    /// </summary>
    /// <remarks>
    /// 少了這一條，最常被停上去問引數順序的 <c>CONVERT</c> 與 <c>LEFT</c> 正好一個都沒有。
    /// </remarks>
    [Fact]
    public void 讓給關鍵字的名稱仍查得到說明()
    {
        Assert.DoesNotContain(
            SqlFunctionCatalog.All,
            suggestion => suggestion.DisplayText == "CONVERT");

        Assert.True(SqlBuiltInDocCatalog.TryGet("LEFT", SqlBuiltInKind.Function, out var left));
        Assert.Equal("LEFT(value, length)", left.Signature);
    }

    /// <summary>型別的一行說明唯一出處是 SqlDataTypeCatalog；JSON 不再寫一次。</summary>
    [Fact]
    public void 型別的說明沿用型別目錄()
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet("nvarchar", SqlBuiltInKind.DataType, out var doc));

        Assert.Equal(SqlBuiltInKind.DataType, doc.Kind);
        Assert.Equal(string.Empty, doc.Signature);
        Assert.True(SqlDataTypeCatalog.TryGetDescription("NVARCHAR", out var description));
        Assert.Equal(description, doc.Summary);
        Assert.NotEqual(string.Empty, doc.Example);
    }

    /// <summary>沒有寫過說明的名稱至少要有簽章，否則提示等於把那個字再唸一次。</summary>
    [Fact]
    public void 只有簽章的函式照樣命中()
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet("NEWID", SqlBuiltInKind.Function, out var doc));

        Assert.Equal("NEWID()", doc.Signature);
        Assert.Equal(string.Empty, doc.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Lib_Reader")]
    [InlineData("SELECT")]
    public void 不是內建名稱就不命中(string? name)
    {
        Assert.False(SqlBuiltInDocCatalog.TryGet(name, SqlBuiltInKind.Function, out _));
        Assert.False(SqlBuiltInDocCatalog.TryGet(name, SqlBuiltInKind.DataType, out _));
    }

    /// <summary>資源裡的名稱或種類打錯字時，那一筆會安靜地永遠貼不到任何名稱上。</summary>
    /// <remarks>
    /// 種類與名稱一起守：<c>YEAR</c> 在四份目錄裡可以有四個意思，種類寫錯的那一筆
    /// 不會查不到，而是貼到另一個意思上去。
    /// </remarks>
    [Fact]
    public void 資源裡的名稱與種類都對得回既有目錄()
    {
        var documented = SqlBuiltInDocCatalog.DocumentedNames;

        Assert.NotEmpty(documented);
        Assert.All(documented, name =>
        {
            Assert.True(SqlBuiltInDocCatalog.TryGetDocumentedKind(name, out var kind), name);
            Assert.True(SqlBuiltInDocCatalog.TryGet(name, kind, out var doc), name);
            Assert.Equal(kind, doc.Kind);
        });
    }

    /// <summary>
    /// 左括號是「這是不是一次呼叫」唯一分得開的依據，與自動大寫同一條規則。
    /// </summary>
    /// <remarks>
    /// 少了它，<c>SELECT year FROM dbo.Loan</c> 停在 <c>year</c> 上會冒出 <c>YEAR()</c>
    /// 的說明，而 <c>CREATE TABLE</c> 的 <c>TABLE</c> 會被說成資料表變數的型別——
    /// 兩個都是提示自己編出來的答案。
    /// </remarks>
    [Theory]
    [InlineData("SELECT CONVERT(int, '1')", 8, true, "CONVERT")]
    [InlineData("SELECT CONVERT (int, '1')", 8, true, "CONVERT")]
    [InlineData("SELECT year FROM dbo.Loan", 8, false, null)]
    [InlineData("CREATE TABLE dbo.Loan (Id int)", 8, false, null)]
    [InlineData("DECLARE @t TABLE (Id int)", 12, true, "TABLE")]
    [InlineData("DECLARE @x nvarchar(20)", 12, true, "NVARCHAR")]
    [InlineData("DECLARE @x int", 12, true, "INT")]
    [InlineData("SELECT [CONVERT] FROM dbo.Loan", 9, false, null)]
    [InlineData("SELECT dbo.CONVERT(1)", 13, false, null)]
    public void 左括號決定名稱算不算一次呼叫(string text, int position, bool found, string? name)
    {
        var reference = SqlIdentifierScanner.FindAt(text, position);

        Assert.NotNull(reference);
        Assert.Equal(found, SqlBuiltInDocCatalog.TryGetAt(text, reference, out var doc));

        if (found)
        {
            Assert.Equal(name, doc.Name);
        }
    }

    /// <summary>CONVERT 的 style 是使用者每次都要回頭查的東西，一定要在。</summary>
    [Fact]
    public void CONVERT帶得出兩張樣式對照表()
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet("CONVERT", SqlBuiltInKind.Function, out var doc));

        Assert.True(doc.HasReferences);
        Assert.Equal(2, doc.References.Count);

        var dates = doc.References[0];
        Assert.Equal(3, dates.Columns.Count);
        Assert.Contains(dates.Rows, row => row[0] == "120");
        Assert.Contains(dates.Rows, row => row[0] == "126");
    }

    /// <summary>
    /// datepart 的名稱與說明只有 SqlArgumentCatalog 一份，對照表是由它組出來的。
    /// </summary>
    /// <remarks>
    /// 抄進資源的症狀是改了一邊另一邊沒改，而兩邊都看得見。
    /// </remarks>
    [Theory]
    [InlineData("DATEADD")]
    [InlineData("DATEDIFF")]
    [InlineData("DATEPART")]
    [InlineData("DATENAME")]
    public void 日期函式共用同一份datepart清單(string name)
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet(name, SqlBuiltInKind.Function, out var doc));

        var table = Assert.Single(doc.References);
        Assert.Equal(SqlArgumentCatalog.DateParts.Count, table.Rows.Count);
        Assert.Equal(SqlArgumentCatalog.DateParts[0].DisplayText, table.Rows[0][0]);
        Assert.Equal(SqlArgumentCatalog.DateParts[0].Description, table.Rows[0][1]);
    }

    /// <summary>
    /// 對照表的形狀要能畫得出來：欄數有上限，每一列的儲存格數與欄數一致。
    /// </summary>
    /// <remarks>
    /// 資料格的欄繫結到具名屬性（Cell1…Cell4），多一欄的資料畫不出來也複製不到；
    /// 列短於欄數則是資源少打一格，那一格由載入時補成空字串。
    /// </remarks>
    [Fact]
    public void 每張對照表都畫得出來()
    {
        var seen = 0;

        foreach (var name in SqlBuiltInDocCatalog.DocumentedNames)
        {
            if (!SqlBuiltInDocCatalog.TryGetDocumentedKind(name, out var kind) ||
                !SqlBuiltInDocCatalog.TryGet(name, kind, out var doc))
            {
                continue;
            }

            foreach (var table in doc.References)
            {
                seen++;
                Assert.NotEqual(string.Empty, table.Title);
                Assert.InRange(table.Columns.Count, 1, SqlBuiltInReference.MaximumColumns);
                Assert.NotEmpty(table.Rows);
                Assert.All(table.Rows, row => Assert.Equal(table.Columns.Count, row.Count));
            }
        }

        Assert.True(seen > 0);
    }

    /// <summary>引用的編號打錯字時，那一張表會安靜地不見。</summary>
    [Fact]
    public void 沒有引用到不存在的對照表()
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet("FORMAT", SqlBuiltInKind.Function, out var format));
        Assert.Single(format.References);

        // TRY_CONVERT 與 CONVERT 引用同一組編號；其中一個打錯字時數量就會對不上。
        Assert.True(SqlBuiltInDocCatalog.TryGet("TRY_CONVERT", SqlBuiltInKind.Function, out var tryConvert));
        Assert.True(SqlBuiltInDocCatalog.TryGet("CONVERT", SqlBuiltInKind.Function, out var convert));
        Assert.Equal(convert.References.Count, tryConvert.References.Count);
    }

    /// <summary>
    /// 提示與日期部分的一行說明只有 SqlArgumentCatalog 一份，全域變數只有
    /// SqlGlobalVariableCatalog 一份。
    /// </summary>
    /// <remarks>
    /// 抄進資源的症狀是改了一邊另一邊沒改，而清單與提示兩邊都看得見。
    /// </remarks>
    [Theory]
    [InlineData("NOLOCK", SqlBuiltInKind.TableHint)]
    [InlineData("nolock", SqlBuiltInKind.TableHint)]
    [InlineData("TABLOCK", SqlBuiltInKind.TableHint)]
    [InlineData("MAXDOP", SqlBuiltInKind.QueryHint)]
    [InlineData("RECOMPILE", SqlBuiltInKind.QueryHint)]
    [InlineData("ISO_WEEK", SqlBuiltInKind.DatePart)]
    [InlineData("@@ROWCOUNT", SqlBuiltInKind.GlobalVariable)]
    [InlineData("@@error", SqlBuiltInKind.GlobalVariable)]
    public void 提示與全域變數的說明沿用既有目錄(string name, SqlBuiltInKind kind)
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet(name, kind, out var doc));

        Assert.Equal(name.ToUpperInvariant(), doc.Name);
        Assert.Equal(kind, doc.Kind);
        Assert.Equal(string.Empty, doc.Signature);
        Assert.Equal(Description(name, kind), doc.Summary);
    }

    /// <summary>種類不互退：位置已經把話說完了，答得出來只會是提示自己編的。</summary>
    [Theory]
    [InlineData("MAXDOP", SqlBuiltInKind.TableHint)]
    [InlineData("NOLOCK", SqlBuiltInKind.QueryHint)]
    [InlineData("NOLOCK", SqlBuiltInKind.DatePart)]
    [InlineData("@@ROWCOUNT", SqlBuiltInKind.DataType)]
    [InlineData("YEAR", SqlBuiltInKind.GlobalVariable)]
    public void 種類對不上就不命中(string name, SqlBuiltInKind kind)
    {
        Assert.False(SqlBuiltInDocCatalog.TryGet(name, kind, out _));
    }

    /// <summary>
    /// 提示與日期部分只在那幾個括號裡才算，全域變數則到哪裡都算。
    /// </summary>
    /// <remarks>
    /// 少了位置這一關，<c>SELECT NOLOCK FROM dbo.Loan</c> 的欄位會被說成資料表提示；
    /// <c>SELECT year FROM dbo.Loan</c> 則早就有左括號那一關擋著。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan WITH (NOLOCK)", 32, "NOLOCK", SqlBuiltInKind.TableHint)]
    [InlineData("SELECT * FROM dbo.Loan WITH (ROWLOCK, XLOCK)", 38, "XLOCK", SqlBuiltInKind.TableHint)]
    [InlineData("SELECT * FROM dbo.Loan WITH (INDEX(1))", 30, "INDEX", SqlBuiltInKind.TableHint)]
    [InlineData("SELECT 1 OPTION (RECOMPILE)", 18, "RECOMPILE", SqlBuiltInKind.QueryHint)]
    [InlineData("SELECT 1 OPTION (MAXDOP 1)", 18, "MAXDOP", SqlBuiltInKind.QueryHint)]
    [InlineData("SELECT DATEADD(ISO_WEEK, 1, @d)", 16, "ISO_WEEK", SqlBuiltInKind.DatePart)]
    [InlineData("SELECT DATEDIFF(DAY, a, b) FROM dbo.Loan", 17, "DAY", SqlBuiltInKind.DatePart)]
    [InlineData("SELECT @@ROWCOUNT", 9, "@@ROWCOUNT", SqlBuiltInKind.GlobalVariable)]
    [InlineData("IF @@ERROR <> 0 RETURN", 4, "@@ERROR", SqlBuiltInKind.GlobalVariable)]
    public void 位置決定提示與日期部分算不算數(
        string text,
        int position,
        string name,
        SqlBuiltInKind kind)
    {
        var reference = SqlIdentifierScanner.FindAt(text, position);

        Assert.NotNull(reference);
        Assert.True(SqlBuiltInDocCatalog.TryGetAt(text, reference, out var doc));
        Assert.Equal(name, doc.Name);
        Assert.Equal(kind, doc.Kind);
    }

    [Theory]
    [InlineData("SELECT NOLOCK FROM dbo.Loan", 8)]
    [InlineData("SELECT MAXDOP FROM dbo.Loan", 8)]
    [InlineData("SELECT Loan.NOLOCK FROM dbo.Loan", 14)]
    [InlineData("SELECT * FROM dbo.Loan WITH ([NOLOCK])", 32)]
    [InlineData("SELECT DATEADD(DAY, ISO_WEEK, @d)", 22)]
    public void 括號外的提示名稱只是欄位名(string text, int position)
    {
        var reference = SqlIdentifierScanner.FindAt(text, position);

        Assert.NotNull(reference);
        Assert.False(SqlBuiltInDocCatalog.TryGetAt(text, reference, out _));
    }

    /// <summary>
    /// 日期部分本身帶得出那張 datepart 對照表。
    /// </summary>
    /// <remarks>
    /// 停在 <c>ISO_WEEK</c> 上要問的正是「還有哪些值可以填」，而那張表日期函式已經
    /// 在用了——它由 SqlArgumentCatalog 組出來，不是資源裡的第二份。
    /// </remarks>
    [Fact]
    public void 日期部分帶得出同一張對照表()
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet("ISO_WEEK", SqlBuiltInKind.DatePart, out var part));
        Assert.True(SqlBuiltInDocCatalog.TryGet("DATEADD", SqlBuiltInKind.Function, out var function));

        var table = Assert.Single(part.References);
        Assert.Equal(SqlArgumentCatalog.DateParts.Count, table.Rows.Count);
        Assert.Equal(Assert.Single(function.References).Title, table.Title);
    }

    /// <summary>
    /// 提示沒有對照表；還沒寫範例的那些只剩一行說明，開了視窗也只有一個標題。
    /// </summary>
    /// <remarks>
    /// 寫過範例的那幾個仍開得起浮動視窗，裡面只有範例那一頁——Ctrl+F12 與提示裡
    /// 那一行連結問的都是「有沒有值得一個視窗的東西」。
    /// </remarks>
    [Fact]
    public void 提示沒有對照表()
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet("NOLOCK", SqlBuiltInKind.TableHint, out var documented));

        Assert.False(documented.HasReferences);
        Assert.NotEqual(string.Empty, documented.Example);

        Assert.True(SqlBuiltInDocCatalog.TryGet("PAGLOCK", SqlBuiltInKind.TableHint, out var bare));

        Assert.False(bare.HasReferences);
        Assert.Equal(string.Empty, bare.Example);
        Assert.NotEqual(string.Empty, bare.Summary);
    }

    /// <summary>寫過範例的提示與全域變數仍然只有一份說明——資源不寫第二份。</summary>
    /// <remarks>
    /// 這是最容易破的一條：補範例時順手把 summary 也填進去，兩份就從此各說各的。
    /// </remarks>
    [Theory]
    [InlineData("NOLOCK", SqlBuiltInKind.TableHint)]
    [InlineData("MAXDOP", SqlBuiltInKind.QueryHint)]
    [InlineData("@@ROWCOUNT", SqlBuiltInKind.GlobalVariable)]
    public void 補了範例也不會多出第二份說明(string name, SqlBuiltInKind kind)
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet(name, kind, out var doc));

        Assert.NotEqual(string.Empty, doc.Example);
        Assert.Equal(Description(name, kind), doc.Summary);
        Assert.StartsWith("https://learn.microsoft.com/", doc.DocsUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// 多字寫法由長到短試，否則 OPTIMIZE FOR UNKNOWN 會被 OPTIMIZE FOR 接走。
    /// </summary>
    /// <remarks>
    /// 那兩個提示說的是相反的事：一個指定假設的參數值，另一個正是不看實際參數。
    /// </remarks>
    [Theory]
    [InlineData("SELECT 1 OPTION (OPTIMIZE FOR UNKNOWN)", "OPTIMIZE FOR UNKNOWN")]
    [InlineData("SELECT 1 OPTION (OPTIMIZE FOR (@p = 1))", "OPTIMIZE FOR")]
    [InlineData("SELECT 1 OPTION (FORCE ORDER)", "FORCE ORDER")]
    [InlineData("SELECT 1 OPTION (MERGE JOIN)", "MERGE JOIN")]
    public void 多字提示停在第一個詞上認得出來(string text, string name)
    {
        var position = text.IndexOf('(') + 1;
        var reference = SqlIdentifierScanner.FindAt(text, position);

        Assert.NotNull(reference);
        Assert.True(SqlBuiltInDocCatalog.TryGetAt(text, reference, out var doc));
        Assert.Equal(name, doc.Name);
        Assert.Equal(SqlBuiltInKind.QueryHint, doc.Kind);
    }

    /// <summary>我們自己寫的文案不該被自己的截斷邏輯砍掉。</summary>
    [Fact]
    public void 說明與範例都排得下()
    {
        Assert.All(SqlBuiltInDocCatalog.DocumentedNames, name =>
        {
            Assert.True(SqlBuiltInDocCatalog.TryGetDocumentedKind(name, out var kind), name);
            Assert.True(SqlBuiltInDocCatalog.TryGet(name, kind, out var doc), name);
            Assert.True(doc.Summary.Length <= MaximumSummaryLength, $"{name}：{doc.Summary.Length}");
            Assert.True(doc.Example.Length <= MaximumExampleLength, $"{name}：{doc.Example.Length}");
            Assert.DoesNotContain('\n', doc.Summary);
            Assert.DoesNotContain('\n', doc.Example);
        });
    }

    /// <summary>
    /// 併進來的三份目錄同樣不該被截斷——它們的說明本來只出現在清單右側。
    /// </summary>
    /// <remarks>
    /// 提示視窗與清單的可用寬度不同，一行說明搬過來時是這裡先看得出來太長。
    /// </remarks>
    [Fact]
    public void 提示與全域變數的說明也排得下()
    {
        foreach (var suggestion in SqlGlobalVariableCatalog.All)
        {
            Assert.True(
                suggestion.Description.Length <= MaximumSummaryLength,
                $"{suggestion.DisplayText}：{suggestion.Description.Length}");
        }

        foreach (var suggestion in SqlArgumentCatalog.TableHints)
        {
            Assert.True(
                suggestion.Description.Length <= MaximumSummaryLength,
                $"{suggestion.DisplayText}：{suggestion.Description.Length}");
        }

        foreach (var suggestion in SqlArgumentCatalog.QueryHints)
        {
            Assert.True(
                suggestion.Description.Length <= MaximumSummaryLength,
                $"{suggestion.DisplayText}：{suggestion.Description.Length}");
        }

        foreach (var suggestion in SqlArgumentCatalog.DateParts)
        {
            Assert.True(
                suggestion.Description.Length <= MaximumSummaryLength,
                $"{suggestion.DisplayText}：{suggestion.Description.Length}");
        }
    }

    private static string Description(string name, SqlBuiltInKind kind)
    {
        var found = kind == SqlBuiltInKind.GlobalVariable
            ? SqlGlobalVariableCatalog.TryGetDescription(name, out var description)
            : SqlArgumentCatalog.TryGetDescription(name, kind, out description);

        Assert.True(found, name);
        return description;
    }

}
