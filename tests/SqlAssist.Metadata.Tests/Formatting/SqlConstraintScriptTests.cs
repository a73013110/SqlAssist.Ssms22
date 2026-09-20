using System;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

/// <summary>
/// 單獨一個條件約束的指令碼。
/// </summary>
/// <remarks>
/// 搜尋清單上點得到條件約束，而它身上沒有自己的定義——每一個字都來自父物件。
/// 這裡固定三件事：四種寫法都是可以執行的 <c>ALTER TABLE</c>、那一句與整張表的
/// 指令碼逐字相同，以及父物件缺席時<b>不</b>猜一句出來。
/// </remarks>
public sealed class SqlConstraintScriptTests
{
    private static SqlScriptContext Context(SqlScriptOptions? options = null) =>
        new(options ?? SqlScriptOptions.Fidelity, newLine: "\r\n");

    /// <summary>父物件用共用的那張 Loan；條件約束自己只有識別欄位。</summary>
    private static SqlObjectStructure Constraint(string name, SqlObjectStructure? parent)
    {
        var loan = parent ?? LoanTableFixture.Create();
        return new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(99, "dbo", name, SqlObjectKind.Constraint)),
            parent: loan);
    }

    private static string Render(string name, SqlObjectStructure? parent = null, SqlScriptOptions? options = null) =>
        TSqlScriptRenderer.Default.Render(Constraint(name, parent), Context(options));

    [Fact]
    public void 預設值寫成掛在資料行上的ALTER_TABLE()
    {
        Assert.Equal(
            "ALTER TABLE [dbo].[Loan] ADD CONSTRAINT [DF_Loan_CreateTime] DEFAULT (getdate()) FOR [CreateTime]\r\nGO\r\n",
            Render("DF_Loan_CreateTime"));
    }

    [Fact]
    public void 主索引鍵與CHECK與整張表的指令碼逐字相同()
    {
        // 兩個表面各組一份的症狀是同一條規則在「整張表」與「只要這一條」長得不一樣，
        // 而使用者會以為其中一邊壞了。
        var table = TSqlScriptRenderer.Default.Render(LoanTableFixture.Create(), Context());

        foreach (var name in new[] { "PK_Loan", "CK_Loan_RenewCount" })
        {
            var single = Render(name).TrimEnd('\r', '\n');
            Assert.Contains(single, table, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 外來鍵帶著參照與動作()
    {
        var loan = LoanTableFixture.Create();
        var parent = new SqlObjectStructure(
            loan.Detail,
            loan.Indexes,
            new[]
            {
                new SqlForeignKeyInfo(
                    "FK_Loan_Branch",
                    "dbo",
                    "Branch",
                    new[] { new SqlForeignKeyColumn("BranchNo", "BranchNo") },
                    deleteAction: "CASCADE")
            },
            checkConstraints: loan.CheckConstraints,
            storage: loan.Storage);

        Assert.Equal(
            "ALTER TABLE [dbo].[Loan] ADD CONSTRAINT [FK_Loan_Branch] FOREIGN KEY ([BranchNo]) " +
            "REFERENCES [dbo].[Branch] ([BranchNo]) ON DELETE CASCADE\r\nGO\r\n",
            Render("FK_Loan_Branch", parent));
    }

    /// <remarks>
    /// 使用者要的就是這一個名字的定義。省略之後交出去的是一段「某個沒有名字的條件約束」，
    /// 貼上去會由引擎再配一個新名字，而那一份與來源上的不是同一條。
    /// </remarks>
    [Fact]
    public void 省略系統名稱的風格仍然寫出這一個條件約束的名稱()
    {
        var script = Render("CK_Loan_RenewCount", SystemNamed(), SqlScriptOptions.Minimal);

        Assert.Contains("CONSTRAINT CK_Loan_RenewCount CHECK", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 父物件讀不到時整段是註解()
    {
        var orphan = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(99, "dbo", "DF_Loan_CreateTime", SqlObjectKind.Constraint)));

        Assert.False(orphan.CanBuildExecutableScript);
        var script = TSqlScriptRenderer.Default.Render(orphan, Context());
        Assert.StartsWith("-- 取不到 [dbo].[DF_Loan_CreateTime] 的所屬資料表結構。", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 父物件上沒有這個名稱時說得出是哪一種失敗()
    {
        var missing = Constraint("DF_Loan_Dropped", null);

        Assert.False(missing.CanBuildExecutableScript);
        var script = TSqlScriptRenderer.Default.Render(missing, Context());
        Assert.Contains("上面卻沒有這個名稱的條件約束", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.Ordinal);
    }

    /// <remarks>
    /// F12 那一端曾經回「SqlAssist 認不得這個物件的種類」——而它認得，只是寫不出來。
    /// </remarks>
    [Fact]
    public void F12不再把條件約束整段註解掉()
    {
        var script = SqlObjectScript.BuildEditable(Constraint("DF_Loan_IsActive", null), Context()).Text;

        Assert.DoesNotContain("認不得這個物件的種類", script, StringComparison.Ordinal);
        Assert.StartsWith("ALTER TABLE [dbo].[Loan] ADD CONSTRAINT [DF_Loan_IsActive] DEFAULT ((1)) FOR [IsActive]",
            script, StringComparison.Ordinal);
    }

    /// <summary>名稱是引擎配的那一種 CHECK；風格說要省略，而這條路徑仍然要寫出來。</summary>
    private static SqlObjectStructure SystemNamed()
    {
        var loan = LoanTableFixture.Create();
        return new SqlObjectStructure(
            loan.Detail,
            loan.Indexes,
            checkConstraints: new[]
            {
                new SqlCheckConstraint("CK_Loan_RenewCount", "([RenewCount]>=(0))", isSystemNamed: true)
            },
            storage: loan.Storage);
    }
}
