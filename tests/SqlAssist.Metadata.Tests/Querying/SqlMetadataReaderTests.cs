using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Querying;

public sealed class SqlMetadataReaderTests
{
    /// <remarks>
    /// <c>object_id</c> 只在自己那個資料庫裡唯一。物件不記得自己從哪個資料庫來的話，
    /// 下游拿它回頭要欄位時會問到目前連線裡剛好同號的那一個——欄位清單看起來
    /// 完全正常，卻屬於另一張表。
    /// </remarks>
    [Fact]
    public void 讀取物件時記下所屬資料庫()
    {
        var record = new FakeDataRecord(42, "dbo", "Loan", "U");

        var scoped = SqlMetadataReader.ReadObject(record, "LibArchive");
        var local = SqlMetadataReader.ReadObject(record);

        Assert.Equal("LibArchive", scoped.DatabaseName);
        Assert.Null(local.DatabaseName);
        Assert.Equal(42, scoped.ObjectId);
        Assert.Equal(SqlObjectKind.Table, scoped.Kind);
    }

    /// <remarks>
    /// 四個界限值在伺服器端就 CONVERT 成字串：用 GetValue 收 sql_variant 拿到的是
    /// 裝箱的原生型別，一個 decimal(38,0) 的序列會讓任何一種整數轉型當場溢位，
    /// 而那會讓整份中繼資料被降級成「這一輪沒有資料」。
    /// </remarks>
    [Fact]
    public void 讀取序列()
    {
        // type_name, precision, scale, start, increment, min, max, cycling, cached, cache_size
        var record = new FakeDataRecord(
            "decimal", (byte)18, (byte)0, "1", "1", "1", "999999999999999999",
            false, true, 50);

        var sequence = SqlMetadataReader.ReadSequence(record);

        Assert.Equal("decimal(18,0)", sequence.DataType);
        Assert.Equal("1", sequence.StartValue);
        Assert.Equal("999999999999999999", sequence.MaximumValue);
        Assert.False(sequence.IsCycling);
        Assert.True(sequence.IsCached);
        Assert.Equal(50, sequence.CacheSize);
    }

    /// <remarks>
    /// <c>cache_size</c> 為 NULL 是「開著快取但大小交給引擎決定」，
    /// 那要寫成不帶數字的 <c>CACHE</c>，寫成 <c>CACHE 0</c> 會被拒絕。
    /// </remarks>
    [Fact]
    public void 讀取沒有指定快取大小的序列()
    {
        var record = new FakeDataRecord(
            "int", (byte)10, (byte)0, "1", "1", "1", "2147483647", true, true, null);

        var sequence = SqlMetadataReader.ReadSequence(record);

        Assert.Equal("int", sequence.DataType);
        Assert.True(sequence.IsCycling);
        Assert.Null(sequence.CacheSize);
    }

    [Fact]
    public void 讀取物件()
    {
        var record = new FakeDataRecord(1234, "dbo", "Lib_Reader", "U");

        var info = SqlMetadataReader.ReadObject(record);

        Assert.Equal(1234, info.ObjectId);
        Assert.Equal("dbo", info.SchemaName);
        Assert.Equal("Lib_Reader", info.Name);
        Assert.Equal(SqlObjectKind.Table, info.Kind);
        Assert.Equal("[dbo].[Lib_Reader]", info.QualifiedName);
    }

    [Fact]
    public void 讀取欄位()
    {
        // column_id, name, type, max_length, precision, scale, nullable, identity,
        // computed, primary_key, default, computed_definition, generated_always
        var record = new FakeDataRecord(
            1, "UserId", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false);

        var column = SqlMetadataReader.ReadColumn(record);

        Assert.Equal(1, column.Ordinal);
        Assert.Equal("UserId", column.Name);
        Assert.Equal("int", column.DataType);
        Assert.False(column.IsNullable);
        Assert.True(column.IsIdentity);
        Assert.False(column.IsComputed);
        Assert.True(column.IsPrimaryKey);
        Assert.Null(column.DefaultDefinition);
    }

    [Fact]
    public void 讀取欄位時套用型別格式化與預設值()
    {
        var record = new FakeDataRecord(
            2, "UserName", "nvarchar", (short)100, (byte)0, (byte)0,
            true, false, false, false, "('')", null, false);

        var column = SqlMetadataReader.ReadColumn(record);

        Assert.Equal("nvarchar(50)", column.DataType);
        Assert.True(column.IsNullable);
        Assert.Equal("('')", column.DefaultDefinition);
    }

    [Fact]
    public void 欄位可組出接近CREATE_TABLE的描述()
    {
        var record = new FakeDataRecord(
            1, "UserId", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false);

        var line = SqlMetadataReader.ReadColumn(record).ToScriptLine();

        Assert.Equal("[UserId] int IDENTITY NOT NULL -- PK", line);
    }

    [Fact]
    public void 讀取計算欄位的運算式()
    {
        var record = new FakeDataRecord(
            3, "FullName", "nvarchar", (short)200, (byte)0, (byte)0,
            true, false, true, false, null, "([First]+' '+[Last])", false);

        var column = SqlMetadataReader.ReadColumn(record);

        Assert.True(column.IsComputed);
        Assert.Equal("([First]+' '+[Last])", column.ComputedDefinition);
    }

    /// <summary>
    /// 插不進去的四種欄位。
    /// </summary>
    /// <remarks>
    /// 展開 INSERT 骨架時漏掉任何一種，症狀不是少幾個欄位，而是整句一執行就錯——
    /// 因此四種各測一次，而不是只測 IDENTITY 與計算欄位這兩個明顯的。
    /// </remarks>
    [Theory]
    [InlineData("CopyNo", "varchar", false, false, false, true)]
    [InlineData("CopyId", "int", true, false, false, false)]
    [InlineData("Barcode", "nvarchar", false, true, false, false)]
    [InlineData("RowVer", "timestamp", false, false, false, false)]
    [InlineData("ValidFrom", "datetime2", false, false, true, false)]
    public void 判斷欄位插不插得進去(
        string name,
        string typeName,
        bool isIdentity,
        bool isComputed,
        bool isGeneratedAlways,
        bool expected)
    {
        var record = new FakeDataRecord(
            1, name, typeName, (short)8, (byte)10, (byte)0,
            false, isIdentity, isComputed, false, null, null, isGeneratedAlways);

        Assert.Equal(expected, SqlMetadataReader.ReadColumn(record).CanInsert);
    }

    [Fact]
    public void 讀取索引列()
    {
        // index_id, name, is_primary_key, is_unique, is_unique_constraint,
        // type_desc, filter_definition, column_name, is_descending, is_included
        var record = new FakeDataRecord(
            2, "IX_Lib_Reader_Name", false, true, false,
            "NONCLUSTERED", "([IsDeleted]=(0))", "UserName", true, false);

        var row = SqlMetadataReader.ReadIndexRow(record);

        Assert.Equal(2, row.IndexId);
        Assert.Equal("IX_Lib_Reader_Name", row.Name);
        Assert.True(row.IsUnique);
        Assert.False(row.IsPrimaryKey);
        Assert.Equal("NONCLUSTERED", row.TypeDescription);
        Assert.Equal("([IsDeleted]=(0))", row.FilterDefinition);
        Assert.Equal("UserName", row.ColumnName);
        Assert.True(row.IsDescending);
        Assert.False(row.IsIncluded);
    }

    [Fact]
    public void 索引的篩選條件為NULL時不擲例外()
    {
        var record = new FakeDataRecord(
            1, "PK_Lib_Reader", true, true, false,
            "CLUSTERED", null, "Id", false, false);

        Assert.Null(SqlMetadataReader.ReadIndexRow(record).FilterDefinition);
    }

    [Fact]
    public void 讀取外來鍵列()
    {
        var record = new FakeDataRecord(
            "FK_Loan_Reader", "dbo", "Lib_Reader", "UserId", "Id", "CASCADE", "NO_ACTION");

        var row = SqlMetadataReader.ReadForeignKeyRow(record);

        Assert.Equal("FK_Loan_Reader", row.Name);
        Assert.Equal("dbo", row.ReferencedSchemaName);
        Assert.Equal("Lib_Reader", row.ReferencedObjectName);
        Assert.Equal("UserId", row.ColumnName);
        Assert.Equal("Id", row.ReferencedColumnName);
        Assert.Equal("CASCADE", row.DeleteAction);
    }

    [Fact]
    public void 讀取參數()
    {
        // parameter_id, name, type, max_length, precision, scale, is_output
        var record = new FakeDataRecord(
            1, "@UserId", "int", (short)4, (byte)10, (byte)0, false);

        var parameter = SqlMetadataReader.ReadParameter(record);

        Assert.Equal(1, parameter.Ordinal);
        Assert.Equal("@UserId", parameter.Name);
        Assert.Equal("int", parameter.DataType);
        Assert.False(parameter.IsOutput);
        Assert.Equal("@UserId int", parameter.ToScriptLine());
    }

    [Fact]
    public void 輸出參數標示OUTPUT()
    {
        var record = new FakeDataRecord(
            2, "@Total", "decimal", (short)9, (byte)18, (byte)2, true);

        Assert.Equal("@Total decimal(18,2) OUTPUT", SqlMetadataReader.ReadParameter(record).ToScriptLine());
    }

    [Fact]
    public void 純量函式的傳回值名稱為空字串時給予替代名稱()
    {
        var record = new FakeDataRecord(
            0, "", "int", (short)4, (byte)10, (byte)0, false);

        Assert.Equal("(傳回值)", SqlMetadataReader.ReadParameter(record).Name);
    }
}
