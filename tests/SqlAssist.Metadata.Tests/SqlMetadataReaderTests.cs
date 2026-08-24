using Xunit;

namespace SqlAssist.Metadata.Tests;

public sealed class SqlMetadataReaderTests
{
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
        // column_id, name, type, max_length, precision, scale,
        // nullable, identity, computed, primary_key, default
        var record = new FakeDataRecord(
            1, "UserId", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null);

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
            true, false, false, false, "('')");

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
            false, true, false, true, null);

        var line = SqlMetadataReader.ReadColumn(record).ToScriptLine();

        Assert.Equal("[UserId] int IDENTITY NOT NULL -- PK", line);
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
