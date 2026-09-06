using System;
using System.Collections.Generic;
using System.IO;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

/// <summary>
/// 逐項固定每一個選項寫出來的是什麼。
/// </summary>
/// <remarks>
/// 一個選項一條，不靠三組風格的快照涵蓋：快照只說得出「整份變了」，
/// 說不出是哪一個選項變的，而風格是選項的組合，同一個選項在兩組風格底下
/// 可能剛好都是預設值。
/// </remarks>
public sealed class TSqlScriptRendererTests
{
    private static SqlScriptContext Context(SqlScriptOptions options, string? databaseCollation = null) =>
        new(options, databaseCollation, newLine: "\n");

    private static string Render(SqlScriptOptions options, string? databaseCollation = null) =>
        TSqlScriptRenderer.Default.Render(LoanTableFixture.Create(), Context(options, databaseCollation));

    // ── 資料行 ────────────────────────────────────────────────────────

    [Fact]
    public void Fidelity風格的資料行把可否為NULL寫在IDENTITY前面()
    {
        Assert.Contains("[LoanId] [int] NOT NULL IDENTITY(1, 1),", Render(SqlScriptOptions.Fidelity));
    }

    [Fact]
    public void SsmsNative風格的資料行把IDENTITY寫在可否為NULL前面()
    {
        Assert.Contains("[LoanId] [int] IDENTITY(1,1) NOT NULL,", Render(SqlScriptOptions.SsmsNative));
    }

    /// <remarks>種子查不到時只寫關鍵字：猜一組 (1, 1) 出來是指令碼在說謊。</remarks>
    [Fact]
    public void 查不到種子時只寫IDENTITY關鍵字()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Lib_Tag", SqlObjectKind.Table),
                new[] { new SqlColumnInfo(1, "TagId", "int", false, isIdentity: true) }));

        var script = TSqlScriptRenderer.Default.Render(
            structure, Context(SqlScriptOptions.Fidelity));

        Assert.Contains("[TagId] int NOT NULL IDENTITY", script);
        Assert.DoesNotContain("IDENTITY(", script);
    }

    [Fact]
    public void 關掉種子之後只寫IDENTITY關鍵字()
    {
        var script = Render(SqlScriptOptions.Fidelity with { IncludeIdentitySeed = false });

        Assert.Contains("[LoanId] [int] NOT NULL IDENTITY,", script);
    }

    [Theory]
    [InlineData(SqlCollationOutput.Always, true)]
    [InlineData(SqlCollationOutput.Never, false)]
    public void 定序依選項寫出或省略(SqlCollationOutput output, bool expected)
    {
        var script = Render(SqlScriptOptions.Fidelity with { Collation = output });

        Assert.Equal(expected, script.Contains("COLLATE Chinese_Taiwan_Stroke_CI_AS"));
    }

    [Fact]
    public void 與資料庫相同的定序在僅有差異時省略()
    {
        var script = Render(
            SqlScriptOptions.Fidelity with { Collation = SqlCollationOutput.WhenDifferentFromDatabase },
            databaseCollation: "Chinese_Taiwan_Stroke_CI_AS");

        Assert.DoesNotContain("COLLATE", script);
    }

    [Fact]
    public void 與資料庫不同的定序仍然寫出來()
    {
        var script = Render(
            SqlScriptOptions.Fidelity with { Collation = SqlCollationOutput.WhenDifferentFromDatabase },
            databaseCollation: "Latin1_General_CI_AS");

        Assert.Contains("COLLATE Chinese_Taiwan_Stroke_CI_AS", script);
    }

    /// <remarks>
    /// 資料庫定序查不到時一定要寫出來：省略等於讓目的地用自己的資料庫定序，
    /// 而排序、比較與唯一索引的行為會跟著換，畫面上卻看不出差別。
    /// </remarks>
    [Fact]
    public void 查不到資料庫定序時一律寫出資料行定序()
    {
        var script = Render(
            SqlScriptOptions.Fidelity with { Collation = SqlCollationOutput.WhenDifferentFromDatabase },
            databaseCollation: null);

        Assert.Contains("COLLATE Chinese_Taiwan_Stroke_CI_AS", script);
    }

    [Fact]
    public void 預設值條件約束的名稱依選項寫出()
    {
        Assert.Contains(
            "CONSTRAINT [DF_Loan_RenewCount] DEFAULT ((0))",
            Render(SqlScriptOptions.Fidelity));

        Assert.Contains(
            "DEFAULT ((0))",
            Render(SqlScriptOptions.Fidelity with { ConstraintNaming = SqlConstraintNaming.Never }));

        Assert.DoesNotContain(
            "CONSTRAINT [DF_Loan_RenewCount]",
            Render(SqlScriptOptions.Fidelity with { ConstraintNaming = SqlConstraintNaming.Never }));
    }

    /// <remarks>
    /// 系統配的名稱每建一次就換一個，寫進指令碼會讓同一張表在兩個資料庫裡的
    /// 條件約束名稱對不起來。
    /// </remarks>
    [Fact]
    public void 系統配的預設值名稱在僅限使用者命名時省略()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Lib_Tag", SqlObjectKind.Table),
                new[]
                {
                    new SqlColumnInfo(
                        1, "IsActive", "bit", false, defaultDefinition: "((1))",
                        script: new SqlColumnScriptDetail(
                            "bit", 1, 1, 0,
                            defaultConstraintName: "DF__Lib_Tag__IsAct__2A4B",
                            defaultIsSystemNamed: true))
                }));

        var options = SqlScriptOptions.Fidelity with
        {
            ConstraintNaming = SqlConstraintNaming.OnlyUserNamed
        };

        var script = TSqlScriptRenderer.Default.Render(structure, Context(options));

        Assert.Contains("DEFAULT ((1))", script);
        Assert.DoesNotContain("CONSTRAINT", script);
    }

    [Fact]
    public void 計算資料行寫成AS運算式而不是型別()
    {
        var structure = Computed(persisted: false, nullable: true);

        var script = TSqlScriptRenderer.Default.Render(structure, Context(SqlScriptOptions.Fidelity));

        Assert.Contains("[FullName] AS ([First]+' '+[Last])", script);
        Assert.DoesNotContain("nvarchar", script);
        Assert.DoesNotContain("PERSISTED", script);
    }

    /// <remarks>
    /// 可否為 NULL 只有在 PERSISTED 後面才寫得出來：沒有存下來的計算資料行
    /// 根本不接受那個宣告，硬寫是一段跑不動的指令碼。
    /// </remarks>
    [Fact]
    public void 只有存下來的計算資料行才寫PERSISTED與可否為NULL()
    {
        var script = TSqlScriptRenderer.Default.Render(
            Computed(persisted: true, nullable: false), Context(SqlScriptOptions.Fidelity));

        Assert.Contains("[FullName] AS ([First]+' '+[Last]) PERSISTED NOT NULL", script);
    }

    [Fact]
    public void 稀疏與唯一識別資料行寫在可否為NULL前面()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Lib_Tag", SqlObjectKind.Table),
                new[]
                {
                    new SqlColumnInfo(
                        1, "RowId", "uniqueidentifier", false,
                        script: new SqlColumnScriptDetail(
                            "uniqueidentifier", 16, 0, 0, isRowGuidCol: true)),
                    new SqlColumnInfo(
                        2, "Note", "nvarchar(50)", true,
                        script: new SqlColumnScriptDetail("nvarchar", 100, 0, 0, isSparse: true))
                }));

        var script = TSqlScriptRenderer.Default.Render(structure, Context(SqlScriptOptions.Fidelity));

        Assert.Contains("[RowId] [uniqueidentifier] ROWGUIDCOL NOT NULL", script);
        Assert.Contains("[Note] [nvarchar] (50) SPARSE NULL", script);
    }

    // ── 型別 ──────────────────────────────────────────────────────────

    [Fact]
    public void 型別的方括號與空格依風格()
    {
        Assert.Contains("[Title] [nvarchar] (200)", Render(SqlScriptOptions.Fidelity));
        Assert.Contains("[Title] [nvarchar](200)", Render(SqlScriptOptions.SsmsNative));
        Assert.Contains("Title nvarchar(200)", Render(SqlScriptOptions.Minimal));
    }

    [Fact]
    public void 最大長度的型別寫成max()
    {
        Assert.Contains("[Remark] [nvarchar] (max)", Render(SqlScriptOptions.Fidelity));
    }

    // ── 條件約束與索引 ────────────────────────────────────────────────

    [Fact]
    public void 主索引鍵依選項內嵌或獨立成敘述()
    {
        var separate = Render(SqlScriptOptions.Fidelity);
        var inline = Render(SqlScriptOptions.SsmsNative);

        Assert.Contains(
            "ALTER TABLE [dbo].[Loan] ADD CONSTRAINT [PK_Loan] PRIMARY KEY CLUSTERED ([LoanId])",
            separate);
        Assert.DoesNotContain("ALTER TABLE [dbo].[Loan] ADD CONSTRAINT [PK_Loan]", inline);
        Assert.Contains("CONSTRAINT [PK_Loan] PRIMARY KEY CLUSTERED ([LoanId] ASC)", inline);
    }

    /// <remarks>主索引鍵不管放哪裡都只能出現一次，兩份的指令碼執行到第二次就失敗。</remarks>
    [Theory]
    [InlineData(SqlScriptStyle.Fidelity)]
    [InlineData(SqlScriptStyle.SsmsNative)]
    [InlineData(SqlScriptStyle.Minimal)]
    public void 主索引鍵只出現一次(SqlScriptStyle style)
    {
        var script = Render(SqlScriptOptions.ForStyle(style));

        Assert.Equal(1, Occurrences(script, "PRIMARY KEY"));
    }

    [Fact]
    public void 遞增的索引鍵資料行依選項省略ASC()
    {
        Assert.Contains(
            "([Status], [LoanTime] DESC) INCLUDE ([IsActive], [DueTime])",
            Render(SqlScriptOptions.Fidelity));

        Assert.Contains(
            "([Status] ASC, [LoanTime] DESC) INCLUDE ([IsActive], [DueTime])",
            Render(SqlScriptOptions.SsmsNative));
    }

    [Fact]
    public void 唯一索引與篩選條件都寫出來()
    {
        var script = Render(SqlScriptOptions.Fidelity);

        Assert.Contains(
            "CREATE UNIQUE NONCLUSTERED INDEX [IX_Loan_2] ON [dbo].[Loan] ([PublicId])",
            script);
        Assert.Contains("WHERE ([IsActive]=(1))", script);
    }

    [Fact]
    public void 關掉索引之後只剩資料表本身()
    {
        var script = Render(SqlScriptOptions.Fidelity with { IncludeIndexes = false });

        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX", script);
        Assert.Contains("CREATE TABLE", script);
    }

    [Fact]
    public void 外來鍵寫成ALTER_TABLE並帶參考動作()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
                new[] { new SqlColumnInfo(1, "CopyId", "int", false) }),
            foreignKeys: new[]
            {
                new SqlForeignKeyInfo(
                    "FK_Loan_Copy",
                    "dbo",
                    "Copy",
                    new[] { new SqlForeignKeyColumn("CopyId", "Id") },
                    deleteAction: "CASCADE")
            });

        var script = TSqlScriptRenderer.Default.Render(structure, Context(SqlScriptOptions.Fidelity));

        Assert.Contains(
            "ALTER TABLE [dbo].[Loan] ADD CONSTRAINT [FK_Loan_Copy] " +
            "FOREIGN KEY ([CopyId]) REFERENCES [dbo].[Copy] ([Id]) ON DELETE CASCADE",
            script);
    }

    [Fact]
    public void 外來鍵依選項加上WITH_NOCHECK()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
                new[] { new SqlColumnInfo(1, "CopyId", "int", false) }),
            foreignKeys: new[]
            {
                new SqlForeignKeyInfo(
                    "FK_Loan_Copy", "dbo", "Copy",
                    new[] { new SqlForeignKeyColumn("CopyId", "Id") })
            });

        var script = TSqlScriptRenderer.Default.Render(
            structure,
            Context(SqlScriptOptions.Fidelity with { ForeignKeysWithNoCheck = true }));

        Assert.Contains("ALTER TABLE [dbo].[Loan] WITH NOCHECK ADD CONSTRAINT [FK_Loan_Copy]", script);
    }

    // ── 批次與識別字 ──────────────────────────────────────────────────

    [Fact]
    public void 批次分隔依選項寫出GO()
    {
        Assert.Contains("\nGO\n", Render(SqlScriptOptions.Fidelity));
        Assert.DoesNotContain("GO", Render(SqlScriptOptions.Minimal));
    }

    [Fact]
    public void SET選項依選項寫在最前面()
    {
        Assert.StartsWith("SET ANSI_NULLS ON", Render(SqlScriptOptions.SsmsNative));
        Assert.StartsWith("CREATE TABLE", Render(SqlScriptOptions.Fidelity));
    }

    /// <remarks>
    /// 關掉方括號之後仍然要替保留字加括號——那不是風格，少了它指令碼執行不了。
    /// </remarks>
    [Fact]
    public void 關掉方括號之後保留字仍然加括號()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
                new[]
                {
                    new SqlColumnInfo(1, "Id", "int", false),
                    new SqlColumnInfo(2, "Order", "int", true)
                }));

        var script = TSqlScriptRenderer.Default.Render(structure, Context(SqlScriptOptions.Minimal));

        Assert.Contains("Id int NOT NULL", script);
        Assert.Contains("[Order] int NULL", script);
    }

    [Fact]
    public void 多個物件依序寫進同一份輸出()
    {
        var script = TSqlScriptRenderer.Default.Render(
            new List<SqlObjectStructure> { Simple("Lib_Tag"), Simple("Lib_Reader") },
            Context(SqlScriptOptions.Fidelity));

        Assert.True(
            script.IndexOf("Lib_Tag", StringComparison.Ordinal) <
            script.IndexOf("Lib_Reader", StringComparison.Ordinal));
    }

    [Fact]
    public void 沒有物件時輸出是空的()
    {
        Assert.Equal(
            string.Empty,
            TSqlScriptRenderer.Default.Render(
                Array.Empty<SqlObjectStructure>(), Context(SqlScriptOptions.Fidelity)));
    }

    // ── 資料表型別 ────────────────────────────────────────────────────

    /// <remarks>
    /// 主索引鍵要寫成不具名的內嵌條件約束——CREATE TYPE 的括號裡不收
    /// <c>CONSTRAINT 名稱</c>，照資料表那一支搬過來會語法錯誤；而其餘索引的
    /// CREATE INDEX 與 ALTER TABLE 對型別都不合法，整組不能跟在後面。
    /// </remarks>
    [Fact]
    public void 資料表型別寫成CREATE_TYPE()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(5, "dbo", "LoanIdList", SqlObjectKind.TableType),
                new[]
                {
                    new SqlColumnInfo(1, "LoanId", "int", false, isPrimaryKey: true),
                    new SqlColumnInfo(2, "CopyNo", "varchar(10)", true)
                }),
            new[]
            {
                // 型別的條件約束一律命名不得，這個名字是引擎自己配的。
                new SqlIndexInfo(1, "PK__LoanIdLi__6E1F6D1A", new[] { new SqlIndexColumn("LoanId") },
                    isPrimaryKey: true, isUnique: true, typeDescription: "CLUSTERED"),
                new SqlIndexInfo(2, "IX_CopyNo", new[] { new SqlIndexColumn("CopyNo") },
                    typeDescription: "NONCLUSTERED")
            });

        var script = TSqlScriptRenderer.Default.Render(structure, Context(SqlScriptOptions.Fidelity));

        Assert.Contains("CREATE TYPE [dbo].[LoanIdList] AS TABLE", script);
        Assert.Contains("[LoanId] int NOT NULL,", script);
        Assert.Contains("PRIMARY KEY CLUSTERED ([LoanId])", script);
        Assert.DoesNotContain("CREATE TABLE", script);
        Assert.DoesNotContain("CONSTRAINT", script);

        // 這兩個寫法對型別都不合法；跟在 CREATE TYPE 後面就是一段執行到一半才失敗的指令碼。
        // 比對整句而不是只比關鍵字：結尾那一行交代用的註解裡就有這兩個字。
        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX [IX_CopyNo]", script);
        Assert.DoesNotContain("ALTER TABLE [dbo].[LoanIdList]", script);

        // 省略掉的索引要留一行交代，否則這份文字看起來就像那個型別只有主索引鍵。
        Assert.Contains("-- 另有 1 個索引沒有寫進來", script);
    }

    [Fact]
    public void 沒有索引的資料表型別不留逗號也不加註解()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(6, "dbo", "TagIdList", SqlObjectKind.TableType),
                new[] { new SqlColumnInfo(1, "TagId", "int", false) }));

        var script = TSqlScriptRenderer.Default.Render(structure, Context(SqlScriptOptions.Fidelity));

        Assert.Contains("[TagId] int NOT NULL\n)", script);
        Assert.DoesNotContain("另有", script);
    }

    [Fact]
    public void 沒有條件約束時最後一個資料行不留逗號()
    {
        var script = TSqlScriptRenderer.Default.Render(
            Simple("Lib_Tag"), Context(SqlScriptOptions.Fidelity));

        Assert.Contains("[Id] int NOT NULL\n)", script);
    }

    // ── 模組 ──────────────────────────────────────────────────────────

    [Fact]
    public void 模組依選項寫成CREATE或ALTER()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(2, "dbo", "usp_LoanFinish", SqlObjectKind.Procedure),
                definition: "CREATE PROCEDURE dbo.usp_LoanFinish AS SELECT 1;"));

        Assert.StartsWith(
            "CREATE PROCEDURE",
            TSqlScriptRenderer.Default.Render(structure, Context(SqlScriptOptions.Fidelity)));

        Assert.StartsWith(
            "ALTER PROCEDURE",
            TSqlScriptRenderer.Default.Render(
                structure,
                Context(SqlScriptOptions.Fidelity with { ModuleStatement = SqlModuleStatement.Alter })));
    }

    /// <remarks>
    /// 同義字與序列沒有 <c>ALTER</c> 的整體寫法，改寫成 ALTER 得到的是一段
    /// 執行到一半才失敗的指令碼。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Synonym, "CREATE SYNONYM [dbo].[syn_Loan] FOR [Lib].[dbo].[Loan];")]
    [InlineData(SqlObjectKind.Sequence, "CREATE SEQUENCE [dbo].[seq_LoanNo] AS int;")]
    public void 同義字與序列即使要求ALTER也維持CREATE(SqlObjectKind kind, string definition)
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(3, "dbo", "obj", kind), definition: definition));

        var script = TSqlScriptRenderer.Default.Render(
            structure,
            Context(SqlScriptOptions.Fidelity with { ModuleStatement = SqlModuleStatement.Alter }));

        Assert.StartsWith(definition, script);
        Assert.DoesNotContain("ALTER", script);
    }


    /// <summary>
    /// 三組風格各自與存檔的快照逐字相同。
    /// </summary>
    /// <remarks>
    /// 逐字比對而不是「語意等價」：等價沒有辦法自動判定，而排版正是這三組風格
    /// 唯一的差別。快照因此是<b>刻意</b>脆弱的——輸出變了就要有人看過那份 diff，
    /// 確認是想要的改變，再更新檔案。
    ///
    /// 快照只說得出「整份變了」，說不出是哪一個選項變的，所以它不能取代上面
    /// 逐項的那些測試；反過來逐項的測試也蓋不住排版與敘述順序，兩邊都要有。
    /// </remarks>
    [Theory]
    [InlineData(SqlScriptStyle.Fidelity)]
    [InlineData(SqlScriptStyle.SsmsNative)]
    [InlineData(SqlScriptStyle.Minimal)]
    public void 三組風格的輸出與快照逐字相同(SqlScriptStyle style)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "Loan." + style + ".sql");
        var expected = File.ReadAllText(path);

        Assert.Equal(Normalize(expected), Normalize(Render(SqlScriptOptions.ForStyle(style))));
    }

    /// <remarks>
    /// 快照檔在版本庫裡是 LF，但取出來的那一份要看 git 的設定；比對前統一，
    /// 否則這個測試會在別人的機器上因為換行而整份紅掉。
    /// </remarks>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static SqlObjectStructure Simple(string name) =>
        new(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", name, SqlObjectKind.Table),
                new[] { new SqlColumnInfo(1, "Id", "int", false) }));

    private static SqlObjectStructure Computed(bool persisted, bool nullable) =>
        new(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Lib_Reader", SqlObjectKind.Table),
                new[]
                {
                    new SqlColumnInfo(
                        1,
                        "FullName",
                        "nvarchar(200)",
                        nullable,
                        isComputed: true,
                        computedDefinition: "([First]+' '+[Last])",
                        script: new SqlColumnScriptDetail(
                            "nvarchar", 400, 0, 0, isPersisted: persisted))
                }));

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
