using System;
using Xunit;

namespace SqlAssist.Metadata.Tests;

public sealed class SqlTypeFormatterTests
{
    [Theory]
    // Unicode 型別的 max_length 以位元組計，顯示時要折半。
    [InlineData("nvarchar", (short)100, (byte)0, (byte)0, "nvarchar(50)")]
    [InlineData("nchar", (short)20, (byte)0, (byte)0, "nchar(10)")]
    [InlineData("nvarchar", (short)-1, (byte)0, (byte)0, "nvarchar(max)")]
    // 非 Unicode 型別直接使用位元組長度。
    [InlineData("varchar", (short)50, (byte)0, (byte)0, "varchar(50)")]
    [InlineData("char", (short)10, (byte)0, (byte)0, "char(10)")]
    [InlineData("varbinary", (short)-1, (byte)0, (byte)0, "varbinary(max)")]
    [InlineData("binary", (short)16, (byte)0, (byte)0, "binary(16)")]
    // 精確數值顯示精確度與小數位數。
    [InlineData("decimal", (short)9, (byte)18, (byte)4, "decimal(18,4)")]
    [InlineData("numeric", (short)5, (byte)10, (byte)0, "numeric(10,0)")]
    // 時間型別只顯示小數秒位數。
    [InlineData("datetime2", (short)8, (byte)27, (byte)7, "datetime2(7)")]
    [InlineData("time", (short)5, (byte)16, (byte)3, "time(3)")]
    [InlineData("datetimeoffset", (short)10, (byte)34, (byte)0, "datetimeoffset(0)")]
    // float 只在非預設精確度時顯示括號。
    [InlineData("float", (short)8, (byte)53, (byte)0, "float")]
    [InlineData("float", (short)4, (byte)24, (byte)0, "float(24)")]
    // 其餘型別直接輸出名稱。
    [InlineData("int", (short)4, (byte)10, (byte)0, "int")]
    [InlineData("bit", (short)1, (byte)1, (byte)0, "bit")]
    [InlineData("uniqueidentifier", (short)16, (byte)0, (byte)0, "uniqueidentifier")]
    [InlineData("datetime", (short)8, (byte)23, (byte)3, "datetime")]
    public void 格式化型別(
        string typeName,
        short maxLength,
        byte precision,
        byte scale,
        string expected)
    {
        Assert.Equal(expected, SqlTypeFormatter.Format(typeName, maxLength, precision, scale));
    }

    [Fact]
    public void 型別名稱大小寫不影響判斷()
    {
        Assert.Equal("NVARCHAR(50)", SqlTypeFormatter.Format("NVARCHAR", 100, 0, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 型別名稱為空時擲回(string? typeName)
    {
        Assert.Throws<ArgumentException>(() => SqlTypeFormatter.Format(typeName!, 0, 0, 0));
    }
}
