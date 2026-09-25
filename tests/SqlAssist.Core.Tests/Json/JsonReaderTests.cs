using SqlAssist.Core.Json;
using SqlAssist.Core.Localization;
using Xunit;

namespace SqlAssist.Core.Tests.Json;

public sealed class JsonReaderTests
{
    [Fact]
    public void 剖析錯誤帶著原因與位置()
    {
        var exception = Assert.Throws<JsonParseException>(() => JsonReader.Parse("[1] x"));

        Assert.Equal(JsonParseError.TrailingContent, exception.Error);
        Assert.Equal(4, exception.Position);
        Assert.Equal("文件結尾之後還有內容（位置 4）", exception.Message);
    }

    [Fact]
    public void 剖析錯誤跟著介面語言()
    {
        // 片段檔寫壞時這一句會出現在片段管理員的狀態列。
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            var exception = Assert.Throws<JsonParseException>(() => JsonReader.Parse("[1e]"));

            Assert.Equal(JsonParseError.UnknownValue, exception.Error);
            Assert.Equal("Unknown value 1e (position 1)", exception.Message);
        }
    }
}
