using System.Collections.Generic;
using System.Text;

namespace SqlAssist.TextGenerator;

/// <summary>一句 .resjson 文字剖析後的樣子：具名佔位符依出現順序排列。</summary>
/// <remarks>
/// 文字檔只收具名佔位符（<c>{count}</c>、<c>{size:N0}</c>），不收 <c>{0}</c>：
/// 譯者看得到每個洞填的是什麼，也能自由調換語序；參數順序由來源語言那一句決定，
/// 產生時才轉成 <see cref="string.Format(string, object[])"/> 的索引。
/// </remarks>
internal sealed class SqlTextTemplate
{
    private readonly List<Segment> _segments;

    private SqlTextTemplate(List<Segment> segments, List<string> names)
    {
        _segments = segments;
        Names = names;
    }

    /// <summary>佔位符名稱，依第一次出現的順序，不重複。</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>剖析一句文字；格式錯誤時回傳 <see langword="null"/> 並給出原因。</summary>
    public static SqlTextTemplate? Parse(string text, out string? error)
    {
        var segments = new List<Segment>();
        var names = new List<string>();
        var literal = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var c = text[index];
            if (c == '{' && index + 1 < text.Length && text[index + 1] == '{')
            {
                literal.Append('{');
                index += 2;
                continue;
            }

            if (c == '}' && index + 1 < text.Length && text[index + 1] == '}')
            {
                literal.Append('}');
                index += 2;
                continue;
            }

            if (c == '}')
            {
                error = $"第 {index} 個字元的 '}}' 沒有對應的 '{{'；要印出大括號請寫成 '}}}}'";
                return null;
            }

            if (c != '{')
            {
                literal.Append(c);
                index++;
                continue;
            }

            var close = text.IndexOf('}', index + 1);
            if (close < 0)
            {
                error = $"第 {index} 個字元的 '{{' 沒有關上；要印出大括號請寫成 '{{{{'";
                return null;
            }

            var hole = text.Substring(index + 1, close - index - 1);
            var nameEnd = 0;
            while (nameEnd < hole.Length && hole[nameEnd] != ',' && hole[nameEnd] != ':')
            {
                nameEnd++;
            }

            var name = hole.Substring(0, nameEnd);
            if (!IsPlaceholderName(name))
            {
                error = $"佔位符 '{{{hole}}}' 的名稱必須是小寫開頭的識別字（例如 {{count}}），不收 {{0}} 這類位置編號";
                return null;
            }

            if (literal.Length > 0)
            {
                segments.Add(new Segment(literal.ToString(), null, null));
                literal.Clear();
            }

            segments.Add(new Segment(null, name, hole.Substring(nameEnd)));
            if (!names.Contains(name))
            {
                names.Add(name);
            }

            index = close + 1;
        }

        if (literal.Length > 0)
        {
            segments.Add(new Segment(literal.ToString(), null, null));
        }

        error = null;
        return new SqlTextTemplate(segments, names);
    }

    /// <summary>沒有佔位符時直接回傳的文字（大括號已還原）。</summary>
    public string ToPlainText()
    {
        var builder = new StringBuilder();
        foreach (var segment in _segments)
        {
            builder.Append(segment.Literal);
        }

        return builder.ToString();
    }

    /// <summary>轉成複合格式字串；<paramref name="order"/> 是來源語言決定的參數順序。</summary>
    public string ToCompositeFormat(IReadOnlyList<string> order)
    {
        var builder = new StringBuilder();
        foreach (var segment in _segments)
        {
            if (segment.Name is null)
            {
                builder.Append(segment.Literal!.Replace("{", "{{").Replace("}", "}}"));
                continue;
            }

            builder.Append('{');
            builder.Append(IndexOf(order, segment.Name));
            builder.Append(segment.Suffix);
            builder.Append('}');
        }

        return builder.ToString();
    }

    public static bool IsPlaceholderName(string name) => IsIdentifier(name, upperFirst: false);

    public static bool IsKeyName(string name) => IsIdentifier(name, upperFirst: true);

    private static bool IsIdentifier(string name, bool upperFirst)
    {
        if (name.Length == 0)
        {
            return false;
        }

        var first = name[0];
        if (upperFirst ? first < 'A' || first > 'Z' : first < 'a' || first > 'z')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(IReadOnlyList<string> order, string name)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == name)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Segment
    {
        public Segment(string? literal, string? name, string? suffix)
        {
            Literal = literal;
            Name = name;
            Suffix = suffix;
        }

        public string? Literal { get; }

        public string? Name { get; }

        public string? Suffix { get; }
    }
}
