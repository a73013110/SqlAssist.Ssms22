using System;

namespace SqlAssist.Core.Localization;

/// <summary>組件內 .resjson 文字陣列的語言順序；由 SqlAssist.TextGenerator 依 MSBuild 屬性產生，不手寫。</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SqlTextLanguagesAttribute : Attribute
{
    public SqlTextLanguagesAttribute(params string[] names)
    {
        Names = names;
    }

    public string[] Names { get; }
}
