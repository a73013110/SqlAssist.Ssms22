using System.Globalization;

namespace SqlAssist.Core.Settings;

/// <summary>使用者只保存不透明基準色；透明度與主題適配由呈現層推導。</summary>
public static class SqlColorPreference
{
    public static bool TryParseRgb(string? value, out int rgb)
    {
        rgb = 0;
        var text = value?.Trim();
        return text is { Length: 7 } && text[0] == '#' &&
            int.TryParse(text.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }
}
