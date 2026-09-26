using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 建議清單開不開、開了之後選不選。
/// </summary>
/// <remarks>
/// 一列一個位置，同時釘住分類（<see cref="SqlCompletionSlot"/>）、參與與否與軟選。
/// 參與看的是設定裡唯一會改變結果的值——觸發字元數——所以 1 與 2 各問一次。
///
/// 關鍵字位置在名字那一格記的是名字寫完之後的位置；可能是名字的那一格則是
/// 「寫了名字」與「沒寫名字」兩種接法的聯集，兩者在這裡剛好相同。
///
/// 輸入一律是游標前方的文字，游標在尾端。
/// </remarks>
public sealed class SqlCompletionPolicyTests
{
    [Theory]
    [InlineData("SELECT ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList, CompletionTarget.Any, false)]
    [InlineData("SELECT P", true, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList, CompletionTarget.Any, false)]

    // 選取清單一項寫完、同一行、還沒有別名：可能是別名，也可能是打到一半的 FROM。
    [InlineData("SELECT PublCode ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]
    [InlineData("SELECT PublCode a", true, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]
    [InlineData("SELECT a + b ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]
    [InlineData("SELECT COUNT(*) ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]
    [InlineData("SELECT 'x' ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]
    [InlineData("SELECT CASE WHEN a=1 THEN 2 END ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]
    [InlineData("SELECT TOP 10 a ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]
    [InlineData("SELECT a, b ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, true)]

    // 別名已經寫了、根本不能有別名，或者換了行：照常硬選。
    [InlineData("SELECT PublCode a ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, false)]
    [InlineData("SELECT a AS b ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, false)]
    [InlineData("SELECT * ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT DISTINCT ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT a, ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList, CompletionTarget.Any, false)]
    [InlineData("SELECT PublCode\nF", true, false, SqlCompletionSlot.Grammar,
        SqlKeywordPosition.SelectListTail | SqlKeywordPosition.StatementStart, CompletionTarget.Any, false)]
    [InlineData("SELECT COUNT(a ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, false)]

    // TOP 子句之後是選取清單的起點，不是尾端。
    [InlineData("SELECT TOP 10 ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail, CompletionTarget.Any, false)]
    [InlineData("SELECT TOP 10 P", true, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail, CompletionTarget.Any, false)]
    [InlineData("SELECT TOP (10) ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail, CompletionTarget.Any, false)]
    [InlineData("SELECT DISTINCT TOP @n ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail, CompletionTarget.Any, false)]
    [InlineData("SELECT TOP 10 PERCENT ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail, CompletionTarget.Any, false)]
    [InlineData("SELECT TOP (10) WITH TIES ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.SelectList, CompletionTarget.Any, false)]

    // CASE 還沒寫完時是 CASE 的裡面，不是選取清單這一層。
    [InlineData("SELECT CASE WHEN a = 1 ", false, false, SqlCompletionSlot.Grammar,
        SqlKeywordPosition.CaseArm, CompletionTarget.Any, false)]
    [InlineData("SELECT CASE WHEN a = 1 THEN b ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.CaseBody, CompletionTarget.Any, false)]

    // 資料來源同一行的別名位置。
    [InlineData("FROM dbo.T ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, true)]
    [InlineData("FROM dbo.T WHE", true, true, SqlCompletionSlot.MaybeName, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, true)]
    [InlineData("FROM dbo.T a", true, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, true)]
    [InlineData("JOIN t ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, true)]
    [InlineData("CROSS APPLY f() ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, true)]
    [InlineData("FROM @rows ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, true)]
    [InlineData("FROM dbo.T a ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("FROM t WITH (NOLOCK) ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("INSERT INTO t ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("FROM dbo.T\nW", true, false, SqlCompletionSlot.Grammar,
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart, CompletionTarget.Any, false)]

    // 換行藏在區塊註解前面一樣是換行。
    [InlineData("SELECT * FROM dbo.Loan\n/* 接續 */ ssf", true, true, SqlCompletionSlot.Grammar,
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart, CompletionTarget.Any, false)]

    [InlineData("FROM (SELECT 1 x) ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("FROM t AS ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("SELECT CASE WHEN a=1 THEN 2 END AS ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.SelectListTail, CompletionTarget.Any, false)]
    [InlineData("DECLARE @", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("DECLARE @p", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT @", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Variable, false)]
    [InlineData("SELECT @p", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Variable, false)]
    [InlineData("CAST(x AS ", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.DataType, false)]
    [InlineData("CAST(x AS I", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.DataType, false)]

    // CREATE 之後是新名稱；CREATE OR ALTER 可能是既有物件；ALTER 一定是既有物件。
    [InlineData("CREATE PROCEDURE ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("CREATE PROCEDURE u", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("CREATE PROCEDURE dbo.", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("CREATE UNIQUE NONCLUSTERED INDEX ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("CREATE TABLE ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("CREATE OR ALTER PROCEDURE u", true, true, SqlCompletionSlot.MaybeName, SqlKeywordPosition.Any, CompletionTarget.Procedure, true)]
    [InlineData("CREATE OR ALTER VIEW ", true, true, SqlCompletionSlot.MaybeName, SqlKeywordPosition.Any, CompletionTarget.View, true)]
    [InlineData("ALTER PROCEDURE u", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Procedure, false)]

    // 封閉的片語是收斂的目標：那一格只有片語的字，與 CAST(x AS 一樣不必等字元數。
    [InlineData("CREATE ", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.DdlObject, CompletionTarget.ClauseKeyword, false)]
    [InlineData("SET ", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.SetTarget, CompletionTarget.ClauseKeyword, false)]
    [InlineData("SET IDENTITY_INSERT ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Any, false)]

    // CTE 名稱與 SELECT … INTO 的目標是新名稱。
    [InlineData(";WITH ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData(";WITH c", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("WITH c AS (SELECT 1 AS a), ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("CREATE VIEW v AS WITH ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT a INTO ", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("SELECT a INTO #n", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("SELECT * INTO dbo.", false, false, SqlCompletionSlot.Name, SqlKeywordPosition.TableSourceTail, CompletionTarget.Any, false)]
    [InlineData("INSERT INTO ", true, true, SqlCompletionSlot.Grammar, SqlKeywordPosition.DataSource, CompletionTarget.DataSource, false)]

    // WITH 的其他用法不是 CTE。
    [InlineData("SELECT * FROM t WITH ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("CREATE PROCEDURE p WITH ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Any, false)]

    // 資料行定義的起點與 ADD 之後：新資料行名稱，或 CONSTRAINT、PRIMARY KEY。
    [InlineData("CREATE TABLE t (", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.ColumnDefinition, CompletionTarget.Any, true)]
    [InlineData("CREATE TABLE t (c", true, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.ColumnDefinition, CompletionTarget.Any, true)]
    [InlineData("CREATE TABLE t (c INT, ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.ColumnDefinition, CompletionTarget.Any, true)]
    [InlineData("CREATE TABLE t (c DECIMAL(10, ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("DECLARE @t TABLE (", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.ColumnDefinition, CompletionTarget.Any, true)]
    [InlineData("ALTER TABLE t ADD ", false, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.AlterTableAdd, CompletionTarget.Any, true)]
    [InlineData("ALTER TABLE t ADD c", true, false, SqlCompletionSlot.MaybeName, SqlKeywordPosition.AlterTableAdd, CompletionTarget.Any, true)]

    // 數值常值與變數的方法呼叫。
    [InlineData("SELECT 1.", false, false, SqlCompletionSlot.Inert, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT 1.5", false, false, SqlCompletionSlot.Inert, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT 10", false, false, SqlCompletionSlot.Inert, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT @x.", false, false, SqlCompletionSlot.Inert, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("SELECT @x.va", false, false, SqlCompletionSlot.Inert, SqlKeywordPosition.Any, CompletionTarget.Any, false)]

    [InlineData("WHERE a = ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.Any, CompletionTarget.Any, false)]

    // 階段 C：LIKE 'x' 之後的 ESCAPE 由產生器補上位置。
    [InlineData("WHERE a LIKE 'x' ", false, false, SqlCompletionSlot.Grammar, SqlKeywordPosition.ExpressionTail, CompletionTarget.Any, false)]
    [InlineData("'字串 ", false, false, SqlCompletionSlot.Inert, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    [InlineData("-- 註解 ", false, false, SqlCompletionSlot.Inert, SqlKeywordPosition.Any, CompletionTarget.Any, false)]
    public void 分類決定參與與軟選(
        string textBeforeCaret,
        bool afterOneCharacter,
        bool afterTwoCharacters,
        SqlCompletionSlot slot,
        SqlKeywordPosition keywordPosition,
        CompletionTarget target,
        bool softSelection)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(slot, context.Slot);
        Assert.Equal(afterOneCharacter, SqlCompletionPolicy.Participates(context, 1));
        Assert.Equal(afterTwoCharacters, SqlCompletionPolicy.Participates(context, 2));
        Assert.Equal(softSelection, SqlCompletionPolicy.UsesSoftSelection(context));
        Assert.Equal(keywordPosition, context.KeywordPosition);
        Assert.Equal(target, context.Target);
    }

    /// <summary>
    /// 點號前面不是名稱時，那不是限定字。
    /// </summary>
    /// <remarks>
    /// 數值常值的小數點與純量變數的方法呼叫（<c>@x.value(</c>）在文字上都是
    /// 「限定字加點號」。當成限定字的話，平台自己在點號觸發時就以結構描述 <c>1</c>
    /// 或 <c>@x</c> 開出整個資料庫的物件清單；重開清單的判斷也必須給同一個答案。
    ///
    /// 方括號裡的位址是連結伺服器的名稱，宣告過的資料表變數要的是它的資料行，
    /// 兩者照常參與。
    /// </remarks>
    [Theory]
    [InlineData("SELECT 1.", false)]
    [InlineData("SELECT * FROM Loan WHERE Fee > 12.", false)]
    [InlineData("SELECT 1 .", false)]
    [InlineData("SELECT 1.e", false)]
    [InlineData("SELECT @x.", false)]
    [InlineData("SELECT @doc.value('(/a)[1]', 'int'), @doc.", false)]
    [InlineData("DECLARE @rows TABLE (CopyNo INT);\nSELECT @rows.", true)]
    [InlineData("SELECT [@x].", true)]
    [InlineData("SELECT * FROM [192.0.2.10].", true)]
    [InlineData("SELECT * FROM t1.", true)]
    public void 點號前面不是名稱時不是限定字(string textBeforeCaret, bool participates)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(participates, SqlCompletionPolicy.Participates(context, 1));
        Assert.Equal(participates, SqlCompletionTriggers.ShouldReopen(textBeforeCaret));
    }

    /// <summary>
    /// 可能是名字的那一格照常列關鍵字，只是不預先選中。
    /// </summary>
    /// <remarks>
    /// 關鍵字位置是「寫了別名」與「沒寫別名」兩種接法的聯集：前者接 WHERE、JOIN，
    /// 後者就是打到一半的那個字本身。少了這一半，<c>FROM dbo.T WHE</c> 開出來的
    /// 清單裡沒有 WHERE。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.PUBLISHER WHE", "WHERE")]
    [InlineData("SELECT * FROM dbo.PUBLISHER INN", "INNER")]
    [InlineData("SELECT PublCode FR", "FROM")]
    [InlineData("SELECT COUNT(*) FR", "FROM")]
    [InlineData("CREATE TABLE t (CON", "CONSTRAINT")]
    [InlineData("CREATE TABLE t (a INT, PRI", "PRIMARY")]
    public void 可能是名字的位置照常列出關鍵字(string textBeforeCaret, string keyword)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        Assert.Equal(SqlCompletionSlot.MaybeName, context.Slot);
        Assert.True(SqlCompletionPolicy.Participates(context, 1));
        Assert.True(SqlCompletionPolicy.UsesSoftSelection(context));
        Assert.Contains(
            SuggestionContextFilter.Filter(suggestions, context),
            suggestion => suggestion.Kind == SuggestionKind.Keyword && suggestion.DisplayText == keyword);
    }

    /// <summary>全文分析把分類原樣帶到最後，範圍與限定字解析都不改它。</summary>
    [Theory]
    [InlineData("SELECT * FROM dbo.PUBLISHER p|", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT p.PUBL_CODE c| FROM dbo.PUBLISHER p", SqlCompletionSlot.MaybeName)]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.|", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * INTO #n| FROM dbo.PUBLISHER", SqlCompletionSlot.Name)]
    [InlineData("SELECT p.| FROM dbo.PUBLISHER p", SqlCompletionSlot.Grammar)]
    public void 全文分析保留分類(string sqlWithCaret, SqlCompletionSlot expected)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.Equal(expected, SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret).Slot);
    }
}
