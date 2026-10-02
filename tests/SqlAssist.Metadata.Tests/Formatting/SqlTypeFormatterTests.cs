using System;
using SqlAssist.Metadata.Formatting;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

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

    [Theory]
    [InlineData("nvarchar", (short)400, (byte)0, (byte)0, "[nvarchar] (200)")]
    [InlineData("nvarchar", (short)-1, (byte)0, (byte)0, "[nvarchar] (max)")]
    [InlineData("int", (short)4, (byte)10, (byte)0, "[int]")]
    [InlineData("decimal", (short)9, (byte)18, (byte)2, "[decimal] (18, 2)")]
    [InlineData("float", (short)8, (byte)53, (byte)0, "[float]")]
    public void Fidelity風格加方括號並在括號前後留空格(
        string typeName, short maxLength, byte precision, byte scale, string expected)
    {
        var formatted = SqlTypeFormatter.Format(
            typeName, maxLength, precision, scale,
            quoteTypeName: true, spaceBeforeArguments: true, spaceAfterComma: true);

        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData("nvarchar", (short)400, (byte)0, (byte)0, "[nvarchar](200)")]
    [InlineData("decimal", (short)9, (byte)18, (byte)2, "[decimal](18,2)")]
    public void SsmsNative風格加方括號但不留空格(
        string typeName, short maxLength, byte precision, byte scale, string expected)
    {
        var formatted = SqlTypeFormatter.Format(
            typeName, maxLength, precision, scale,
            quoteTypeName: true, spaceBeforeArguments: false, spaceAfterComma: false);

        Assert.Equal(expected, formatted);
    }

    /// <remarks>
    /// vector 不寫維度是語法錯誤，而維度與基底型別只在目錄檢視的兩欄上，
    /// max_length 反推不回來（同一個位元組數 float32 與 float16 各對到一種維度）。
    /// 預設的 float32 不寫，與 float 的預設精確度同一條。
    /// </remarks>
    [Theory]
    [InlineData(3, "float32", "vector(3)")]
    [InlineData(3, "float16", "vector(3,float16)")]
    [InlineData(1998, null, "vector(1998)")]
    [InlineData(3, "FLOAT32", "vector(3)")]
    public void vector依維度與基底型別寫出括號(int dimensions, string? baseType, string expected)
    {
        Assert.Equal(expected, SqlTypeFormatter.Format("vector", 20, 0, 0, dimensions, baseType));
    }

    [Theory]
    [InlineData("float32", "[vector] (3)")]
    [InlineData("float16", "[vector] (3, float16)")]
    public void vector的基底型別跟著逗號排版(string baseType, string expected)
    {
        var formatted = SqlTypeFormatter.Format(
            "vector", 20, 0, 0,
            quoteTypeName: true, spaceBeforeArguments: true, spaceAfterComma: true,
            vectorDimensions: 3, vectorBaseType: baseType);

        Assert.Equal(expected, formatted);
    }

    /// <remarks>
    /// 讀不到維度只會發生在 SQL Server 2025 之前，而那一版根本建不出 vector 欄位；
    /// 沒有數字可寫時不猜一個。
    /// </remarks>
    [Fact]
    public void 讀不到維度的vector只寫型別名稱()
    {
        Assert.Equal("vector", SqlTypeFormatter.Format("vector", 20, 0, 0));
    }

    [Fact]
    public void 維度只套在vector上()
    {
        Assert.Equal("int", SqlTypeFormatter.Format("int", 4, 10, 0, 3, "float16"));
    }

    /// <remarks>
    /// 不加括號的呼叫端（建議清單、滑鼠停留提示）與原本的單參數多載必須逐字相同，
    /// 否則換一個排版選項會連提示的文字一起改掉。
    /// </remarks>
    [Theory]
    [InlineData("nvarchar", (short)400, (byte)0, (byte)0)]
    [InlineData("decimal", (short)9, (byte)18, (byte)2)]
    [InlineData("datetime2", (short)8, (byte)27, (byte)7)]
    public void 緊湊寫法與原本的多載完全相同(
        string typeName, short maxLength, byte precision, byte scale)
    {
        Assert.Equal(
            SqlTypeFormatter.Format(typeName, maxLength, precision, scale),
            SqlTypeFormatter.Format(typeName, maxLength, precision, scale, false, false, false));
    }
}
