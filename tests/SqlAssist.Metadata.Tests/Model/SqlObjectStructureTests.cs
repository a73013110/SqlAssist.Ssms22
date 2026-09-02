using System.Collections.Generic;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

public sealed class SqlObjectStructureTests
{
    private static SqlObjectInfo Table() => new(1, "dbo", "Lib_Reader", SqlObjectKind.Table);

    private static SqlColumnInfo Column(
        int ordinal,
        string name,
        string type,
        bool nullable,
        bool identity = false,
        bool primaryKey = false,
        string? defaultDefinition = null,
        bool computed = false,
        string? computedDefinition = null)
    {
        return new SqlColumnInfo(
            ordinal,
            name,
            type,
            nullable,
            identity,
            computed,
            primaryKey,
            defaultDefinition,
            computedDefinition);
    }

    [Fact]
    public void 索引的多列結果依索引合併()
    {
        var rows = new List<SqlIndexRow>
        {
            new(1, "PK_Lib_Reader", true, true, false, "CLUSTERED", null, "Id", false, false),
            new(2, "IX_Name", false, false, false, "NONCLUSTERED", null, "Last", false, false),
            new(2, "IX_Name", false, false, false, "NONCLUSTERED", null, "First", true, false),
            new(2, "IX_Name", false, false, false, "NONCLUSTERED", null, "Email", false, true)
        };

        var indexes = SqlIndexInfo.FromRows(rows);

        Assert.Equal(2, indexes.Count);
        Assert.Equal("PK_Lib_Reader", indexes[0].Name);
        Assert.True(indexes[0].IsPrimaryKey);
        Assert.Equal("Last ASC, First DESC", indexes[1].DescribeKeyColumns());
        Assert.Equal("Email", indexes[1].DescribeIncludedColumns());
    }

    [Fact]
    public void 以index_id分界而不是名稱()
    {
        // 查詢是依 index_id 排序的，合併也必須以它為準；
        // 改用名稱分界的話，只要有兩個索引恰好同名就會被併成一個。
        var rows = new List<SqlIndexRow>
        {
            new(1, "IX_A", false, false, false, "CLUSTERED", null, "Id", false, false),
            new(2, "IX_A", false, false, false, "NONCLUSTERED", null, "Id", false, false)
        };

        Assert.Equal(2, SqlIndexInfo.FromRows(rows).Count);
    }

    [Fact]
    public void 索引寫成可執行的建立語句()
    {
        var index = new SqlIndexInfo(
            2,
            "IX_Name",
            new[]
            {
                new SqlIndexColumn("Last"),
                new SqlIndexColumn("First", isDescending: true),
                new SqlIndexColumn("Email", isIncluded: true)
            },
            isUnique: true,
            typeDescription: "NONCLUSTERED",
            filterDefinition: "([IsDeleted]=(0))");

        Assert.Equal(
            "CREATE UNIQUE NONCLUSTERED INDEX [IX_Name] ON [dbo].[Lib_Reader] " +
            "([Last] ASC, [First] DESC) INCLUDE ([Email]) WHERE ([IsDeleted]=(0));",
            index.ToScript("[dbo].[Lib_Reader]"));
    }

    [Fact]
    public void 主索引鍵與唯一條件約束寫成ALTER_TABLE()
    {
        // 兩者在 sys.indexes 裡與一般索引長得一樣，但用 CREATE INDEX 寫出來不能執行。
        var primaryKey = new SqlIndexInfo(
            1,
            "PK_Lib_Reader",
            new[] { new SqlIndexColumn("Id") },
            isPrimaryKey: true,
            isUnique: true,
            typeDescription: "CLUSTERED");

        var unique = new SqlIndexInfo(
            3,
            "UQ_Lib_Reader_Email",
            new[] { new SqlIndexColumn("Email") },
            isUnique: true,
            isUniqueConstraint: true,
            typeDescription: "NONCLUSTERED");

        Assert.Equal(
            "ALTER TABLE [dbo].[Lib_Reader] ADD CONSTRAINT [PK_Lib_Reader] PRIMARY KEY CLUSTERED ([Id] ASC);",
            primaryKey.ToScript("[dbo].[Lib_Reader]"));
        Assert.Equal(
            "ALTER TABLE [dbo].[Lib_Reader] ADD CONSTRAINT [UQ_Lib_Reader_Email] UNIQUE NONCLUSTERED ([Email] ASC);",
            unique.ToScript("[dbo].[Lib_Reader]"));
    }

    [Fact]
    public void 外來鍵的多列結果依名稱合併()
    {
        var rows = new List<SqlForeignKeyRow>
        {
            new("FK_Loan_Reader", "dbo", "Lib_Reader", "UserId", "Id", "CASCADE", "NO_ACTION"),
            new("FK_Loan_Copy", "dbo", "Copy", "CopyId", "Id", "NO_ACTION", "NO_ACTION"),
            new("FK_Loan_Copy", "dbo", "Copy", "CopyKind", "Kind", "NO_ACTION", "NO_ACTION")
        };

        var keys = SqlForeignKeyInfo.FromRows(rows);

        Assert.Equal(2, keys.Count);
        Assert.Equal("ON DELETE CASCADE", keys[0].DescribeActions());
        Assert.Equal(2, keys[1].Columns.Count);
        Assert.Equal("CopyId, CopyKind → [dbo].[Copy].Id, Kind", keys[1].DescribeColumns());
    }

    [Fact]
    public void 沒有參考動作時不顯示動作()
    {
        var key = new SqlForeignKeyInfo(
            "FK_Loan_Copy",
            "dbo",
            "Copy",
            new[] { new SqlForeignKeyColumn("CopyId", "Id") });

        Assert.Equal(string.Empty, key.DescribeActions());
        Assert.Equal(
            "ALTER TABLE [dbo].[Loans] ADD CONSTRAINT [FK_Loan_Copy] " +
            "FOREIGN KEY ([CopyId]) REFERENCES [dbo].[Copy] ([Id]);",
            key.ToScript("[dbo].[Loans]"));
    }

    [Fact]
    public void 資料表的指令碼含主索引鍵條件約束與其餘索引()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                Table(),
                new[]
                {
                    Column(1, "Id", "int", nullable: false, identity: true, primaryKey: true),
                    Column(2, "Name", "nvarchar(50)", nullable: true, defaultDefinition: "('')")
                }),
            new[]
            {
                new SqlIndexInfo(1, "PK_Lib_Reader", new[] { new SqlIndexColumn("Id") },
                    isPrimaryKey: true, isUnique: true, typeDescription: "CLUSTERED"),
                new SqlIndexInfo(2, "IX_Name", new[] { new SqlIndexColumn("Name") },
                    typeDescription: "NONCLUSTERED")
            },
            new[]
            {
                new SqlForeignKeyInfo(
                    "FK_Lib_Reader_Branch",
                    "dbo",
                    "Branch",
                    new[] { new SqlForeignKeyColumn("BranchId", "Id") })
            });

        var script = structure.BuildScript();

        Assert.Contains("CREATE TABLE [dbo].[Lib_Reader]", script);
        Assert.Contains("    [Id] int IDENTITY NOT NULL,", script);
        Assert.Contains("    [Name] nvarchar(50) NULL DEFAULT (''),", script);
        Assert.Contains("    CONSTRAINT [PK_Lib_Reader] PRIMARY KEY CLUSTERED ([Id] ASC)", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Name]", script);
        Assert.Contains("ADD CONSTRAINT [FK_Lib_Reader_Branch] FOREIGN KEY ([BranchId])", script);

        // 主索引鍵已經寫進 CREATE TABLE，不可以再單獨輸出一次。
        Assert.DoesNotContain("ADD CONSTRAINT [PK_Lib_Reader]", script);

        // 欄位定義不加 -- PK 註解，否則整段貼上去會被註解吃掉後面的逗號。
        Assert.DoesNotContain("-- PK", script);
    }

    [Fact]
    public void 計算欄位寫成AS運算式而不是型別()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                Table(),
                new[]
                {
                    Column(1, "Id", "int", nullable: false),
                    Column(2, "FullName", "nvarchar(200)", nullable: true,
                        computed: true, computedDefinition: "([First]+' '+[Last])")
                }));

        var script = structure.BuildScript();

        Assert.Contains("    [FullName] AS ([First]+' '+[Last])", script);
        Assert.DoesNotContain("[FullName] nvarchar(200)", script);
    }

    [Fact]
    public void 模組類物件的指令碼就是定義本文()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(2, "dbo", "usp_GetBook", SqlObjectKind.Procedure),
                parameters: new[] { new SqlParameterInfo(1, "@Id", "int", false) },
                definition: "CREATE PROCEDURE dbo.usp_GetBook @Id int AS SELECT 1;"));

        Assert.Equal("CREATE PROCEDURE dbo.usp_GetBook @Id int AS SELECT 1;", structure.BuildScript());
    }

    /// <summary>
    /// 檢視同時是模組也有欄位。定義取不到時原本會掉進 CREATE TABLE 那一支，
    /// 於是一個檢視被寫成一張同名的資料表——照著執行就真的多出一張表。
    /// </summary>
    /// <remarks>
    /// OBJECT_DEFINITION 傳回 NULL 的兩個原因（WITH ENCRYPTION、沒有
    /// VIEW DEFINITION 權限）要寫在輸出裡，否則使用者查不出為什麼沒有指令碼。
    /// </remarks>
    [Fact]
    public void 取不到定義的檢視不會被寫成資料表()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(3, "dbo", "v_Loan", SqlObjectKind.View),
                new[]
                {
                    Column(1, "LoanId", "int", nullable: false),
                    Column(2, "CopyNo", "varchar(10)", nullable: true)
                }));

        var script = structure.BuildScript();

        Assert.DoesNotContain("CREATE TABLE", script);
        Assert.Contains("取不到 [dbo].[v_Loan] 的定義", script);
        Assert.Contains("VIEW DEFINITION", script);

        // 查得到的欄位仍然要看得到，只是整段都是註解——這裡沒有一行執行得動。
        Assert.Contains("--     [LoanId] int NOT NULL", script);

        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim();
            Assert.True(trimmed.Length == 0 || trimmed.StartsWith("--"), line);
        }
    }

    /// <summary>
    /// 查詢成功卻一個欄位都沒有回來：組出來的 CREATE TABLE 只剩一對空括號，
    /// 而那仍然貼得上去——執行下去建出一張沒有欄位的資料表。
    /// </summary>
    /// <remarks>
    /// 這一格與模組取不到定義是同一類問題，因此走同一份輸出：整段註解、
    /// 寫明缺什麼與兩個可能的原因。原因不寫進去的話，使用者查不出該去看權限、
    /// 看物件還在不在，還是看連線。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Table)]
    [InlineData(SqlObjectKind.TableType)]
    public void 取不到欄位時整段註解(SqlObjectKind kind)
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(7, "dbo", "Lib_Tag", kind)));

        var script = structure.BuildScript();

        Assert.False(structure.CanBuildExecutableScript);
        Assert.Contains("取不到 [dbo].[Lib_Tag] 的欄位", script);
        Assert.Contains("sys.columns", script);
        Assert.DoesNotContain("CREATE TABLE", script);
        Assert.DoesNotContain("CREATE TYPE", script);

        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim();
            Assert.True(trimmed.Length == 0 || trimmed.StartsWith("--"), line);
        }
    }

    /// <summary>
    /// 「這一次的資料夠不夠」只有一份判斷，組指令碼與問這個屬性走的是同一條。
    /// </summary>
    /// <remarks>
    /// 分成兩份的症狀是屬性說寫得出來、組出來的卻是一段註解——新的表面要問
    /// 「這份結構能不能給使用者一段可以執行的東西」時，問的就是這個屬性。
    /// </remarks>
    [Fact]
    public void 資料夠不夠與組出來的東西一致()
    {
        var ready = new SqlObjectStructure(
            new SqlObjectDetail(
                Table(),
                new[] { Column(1, "Id", "int", nullable: false) }));

        var missingColumns = new SqlObjectStructure(new SqlObjectDetail(Table()));

        var missingDefinition = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(8, "dbo", "v_Loan", SqlObjectKind.View),
                new[] { Column(1, "LoanId", "int", nullable: false) }));

        // 同義字寫不出指令碼是種類的事，與這一輪查到多少資料無關。
        var synonym = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(9, "dbo", "syn_Loan", SqlObjectKind.Synonym)));

        Assert.True(ready.CanBuildExecutableScript);
        Assert.False(missingColumns.CanBuildExecutableScript);
        Assert.False(missingDefinition.CanBuildExecutableScript);
        Assert.False(synonym.CanBuildExecutableScript);

        Assert.StartsWith("CREATE TABLE", ready.BuildScript());
        Assert.StartsWith("-- 取不到", missingColumns.BuildScript());
        Assert.StartsWith("-- 取不到", missingDefinition.BuildScript());
    }
    /// <summary>取不到定義的程序列出參數，理由與檢視列出欄位相同。</summary>
    [Fact]
    public void 取不到定義的程序列出參數()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(4, "dbo", "usp_Renew", SqlObjectKind.Procedure),
                parameters: new[] { new SqlParameterInfo(1, "@LoanId", "int", false) }));

        var script = structure.BuildScript();

        Assert.Contains("取不到 [dbo].[usp_Renew] 的定義", script);
        Assert.Contains("--     @LoanId int", script);
    }

    /// <summary>
    /// 資料表型別有欄位，落到 CREATE TABLE 那一支就是指令碼在說謊：指令碼分頁的
    /// 文字文件上明說可以直接執行，照著執行卻會多出一張同名的資料表。
    /// </summary>
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
                    Column(1, "LoanId", "int", nullable: false, primaryKey: true),
                    Column(2, "CopyNo", "varchar(10)", nullable: true)
                }),
            new[]
            {
                // 型別的條件約束一律命名不得，這個名字是引擎自己配的。
                new SqlIndexInfo(1, "PK__LoanIdLi__6E1F6D1A", new[] { new SqlIndexColumn("LoanId") },
                    isPrimaryKey: true, isUnique: true, typeDescription: "CLUSTERED"),
                new SqlIndexInfo(2, "IX_CopyNo", new[] { new SqlIndexColumn("CopyNo") },
                    typeDescription: "NONCLUSTERED")
            });

        var script = structure.BuildScript();

        Assert.Contains("CREATE TYPE [dbo].[LoanIdList] AS TABLE", script);
        Assert.Contains("    [LoanId] int NOT NULL,", script);
        Assert.Contains("    [CopyNo] varchar(10) NULL,", script);
        Assert.Contains("    PRIMARY KEY CLUSTERED ([LoanId] ASC)", script);
        Assert.DoesNotContain("CREATE TABLE", script);
        Assert.DoesNotContain("CONSTRAINT", script);

        // 這兩個寫法對型別都不合法；跟在 CREATE TYPE 後面就是一段執行到一半才失敗的指令碼。
        // 比對整句而不是只比關鍵字：結尾那一行交代用的註解裡就有這兩個字。
        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX [IX_CopyNo]", script);
        Assert.DoesNotContain("ALTER TABLE [dbo].[LoanIdList]", script);

        // 省略掉的索引要留一行交代，否則這份文字看起來就像那個型別只有主索引鍵。
        Assert.Contains("-- 另有 1 個索引沒有寫進來", script);
    }

    /// <summary>沒有索引的資料表型別不留逗號，也不多那一行交代。</summary>
    [Fact]
    public void 沒有索引的資料表型別不留逗號也不加註解()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(6, "dbo", "TagIdList", SqlObjectKind.TableType),
                new[] { Column(1, "TagId", "int", nullable: false) }));

        var script = structure.BuildScript();

        Assert.Contains(
            "    [TagId] int NOT NULL" + System.Environment.NewLine + ");",
            script);
        Assert.DoesNotContain("另有", script);
    }

    [Fact]
    public void 沒有主索引鍵時最後一個欄位不留逗號()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                Table(),
                new[] { Column(1, "Id", "int", nullable: false) }));

        Assert.Contains(
            "    [Id] int NOT NULL" + System.Environment.NewLine + ");",
            structure.BuildScript());
    }
}
