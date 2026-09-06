using System;
using System.Linq;
using System.Reflection;
using SqlAssist.Core.Scripting;
using Xunit;

namespace SqlAssist.Core.Tests.Scripting;

public sealed class SqlScriptOptionsSerializerTests
{
    /// <summary>
    /// 反射清單與序列化器共用同一份來源，因此新增一個選項時這個測試會自動涵蓋它。
    /// 手抄一份屬性清單的話，漏掉的那一項會安靜地存不進去也讀不回來。
    /// </summary>
    public static TheoryData<string> 每一個可存放的屬性()
    {
        var data = new TheoryData<string>();

        foreach (var property in typeof(SqlScriptOptions)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanWrite &&
                                        property.Name != nameof(SqlScriptOptions.Style)))
        {
            data.Add(property.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(每一個可存放的屬性))]
    public void 每一個屬性改過之後都存得進去也讀得回來(string propertyName)
    {
        var property = typeof(SqlScriptOptions).GetProperty(propertyName)!;
        var options = Mutate(SqlScriptOptions.Fidelity, property);
        var expected = property.GetValue(options);

        var restored = SqlScriptOptionsSerializer.Read(SqlScriptOptionsSerializer.Write(options));

        Assert.Equal(expected, property.GetValue(restored));
    }

    [Theory]
    [InlineData(SqlScriptStyle.Fidelity)]
    [InlineData(SqlScriptStyle.SsmsNative)]
    [InlineData(SqlScriptStyle.Minimal)]
    public void 沒有覆寫的風格往返之後完全相同(SqlScriptStyle style)
    {
        var options = SqlScriptOptions.ForStyle(style);

        Assert.Equal(options, SqlScriptOptionsSerializer.Read(SqlScriptOptionsSerializer.Write(options)));
    }

    [Fact]
    public void 只寫出與風格預設值不同的項目()
    {
        var json = SqlScriptOptionsSerializer.Write(
            SqlScriptOptions.SsmsNative with { IncludeTriggers = true });

        Assert.Contains("\"includeTriggers\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"indent\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 讀取不會污染共用的風格預設值()
    {
        SqlScriptOptionsSerializer.Read(
            SqlScriptOptionsSerializer.Write(SqlScriptOptions.Fidelity with { IncludeIndexes = false }));

        Assert.True(SqlScriptOptions.Fidelity.IncludeIndexes);
    }

    [Fact]
    public void 壞掉的設定退回預設風格而不是擲出例外()
    {
        Assert.Equal(SqlScriptOptions.Fidelity, SqlScriptOptionsSerializer.Read("{ not json"));
        Assert.Equal(SqlScriptOptions.Fidelity, SqlScriptOptionsSerializer.Read(null));
    }

    [Fact]
    public void 單獨一項壞掉時只有那一項退回預設值()
    {
        var restored = SqlScriptOptionsSerializer.Read(
            "{\"style\":\"Minimal\",\"overrides\":{\"collation\":\"Nonsense\",\"includeTriggers\":true}}");

        Assert.Equal(SqlScriptOptions.Minimal.Collation, restored.Collation);
        Assert.True(restored.IncludeTriggers);
    }

    /// <summary>把一個屬性改成與目前不同的值，型別不支援時測試失敗而不是安靜跳過。</summary>
    private static SqlScriptOptions Mutate(SqlScriptOptions source, PropertyInfo property)
    {
        var result = SqlScriptOptionsSerializer.Read(SqlScriptOptionsSerializer.Write(source));
        var current = property.GetValue(result);
        var type = property.PropertyType;

        if (type == typeof(bool))
        {
            property.SetValue(result, !(bool)current!);
            return result;
        }

        if (type == typeof(string))
        {
            property.SetValue(result, (string)current! + "!");
            return result;
        }

        if (type == typeof(int))
        {
            property.SetValue(result, (int)current! + 1);
            return result;
        }

        if (type.IsEnum)
        {
            var other = Enum.GetValues(type).Cast<object>().First(value => !Equals(value, current));
            property.SetValue(result, other);
            return result;
        }

        throw new InvalidOperationException(
            $"{property.Name} 的型別 {type.Name} 還沒有序列化支援，請一併補進 SqlScriptOptionsSerializer。");
    }
}
