using System.Collections.Generic;
using Xunit;

namespace SqlAssist.Metadata.Tests;

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
            new("FK_Order_User", "dbo", "Lib_Reader", "UserId", "Id", "CASCADE", "NO_ACTION"),
            new("FK_Order_Item", "dbo", "Item", "ItemId", "Id", "NO_ACTION", "NO_ACTION"),
            new("FK_Order_Item", "dbo", "Item", "ItemKind", "Kind", "NO_ACTION", "NO_ACTION")
        };

        var keys = SqlForeignKeyInfo.FromRows(rows);

        Assert.Equal(2, keys.Count);
        Assert.Equal("ON DELETE CASCADE", keys[0].DescribeActions());
        Assert.Equal(2, keys[1].Columns.Count);
        Assert.Equal("ItemId, ItemKind → [dbo].[Item].Id, Kind", keys[1].DescribeColumns());
    }

    [Fact]
    public void 沒有參考動作時不顯示動作()
    {
        var key = new SqlForeignKeyInfo(
            "FK_Order_Item",
            "dbo",
            "Item",
            new[] { new SqlForeignKeyColumn("ItemId", "Id") });

        Assert.Equal(string.Empty, key.DescribeActions());
        Assert.Equal(
            "ALTER TABLE [dbo].[Orders] ADD CONSTRAINT [FK_Order_Item] " +
            "FOREIGN KEY ([ItemId]) REFERENCES [dbo].[Item] ([Id]);",
            key.ToScript("[dbo].[Orders]"));
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
                    "FK_Lib_Reader_Dept",
                    "dbo",
                    "Dept",
                    new[] { new SqlForeignKeyColumn("DeptId", "Id") })
            });

        var script = structure.BuildScript();

        Assert.Contains("CREATE TABLE [dbo].[Lib_Reader]", script);
        Assert.Contains("    [Id] int IDENTITY NOT NULL,", script);
        Assert.Contains("    [Name] nvarchar(50) NULL DEFAULT (''),", script);
        Assert.Contains("    CONSTRAINT [PK_Lib_Reader] PRIMARY KEY CLUSTERED ([Id] ASC)", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Name]", script);
        Assert.Contains("ADD CONSTRAINT [FK_Lib_Reader_Dept] FOREIGN KEY ([DeptId])", script);

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
                new SqlObjectInfo(2, "dbo", "usp_GetUser", SqlObjectKind.Procedure),
                parameters: new[] { new SqlParameterInfo(1, "@Id", "int", false) },
                definition: "CREATE PROCEDURE dbo.usp_GetUser @Id int AS SELECT 1;"));

        Assert.Equal("CREATE PROCEDURE dbo.usp_GetUser @Id int AS SELECT 1;", structure.BuildScript());
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
