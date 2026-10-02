using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SqlAssist.Core.Json;

namespace SqlAssist.KeywordGenerator;

/// <summary>語句說明（Core 的 statements.json）裡當證據用的部分：多字的語句名稱，以及 DBCC 命令括號裡的字。</summary>
/// <remarks>只讀文件、不問剖析器，探測之前就讀完驗完：寫法與命令名單對不上時不必等片語探完才中止。</remarks>
internal sealed class StatementDocs
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    private StatementDocs(List<string> statementNames, List<KeyValuePair<string, List<string>>> dbccArguments)
    {
        StatementNames = statementNames;
        DbccArguments = dbccArguments;
    }

    /// <summary>語句的名稱與別名中含空白的，照文件順序。</summary>
    public IReadOnlyList<string> StatementNames { get; }

    /// <summary>DBCC 每個命令括號裡的關鍵字，照第一次寫到的順序。</summary>
    public IReadOnlyList<KeyValuePair<string, List<string>>> DbccArguments { get; }

    public static StatementDocs Load(string path)
    {
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static StatementDocs Parse(string json)
    {
        var statements = JsonReader.Parse(json)["docs"].Items
            .Where(doc => IgnoreCase.Equals(doc["kind"].AsString(), "statement"))
            .ToList();

        var names = statements
            .SelectMany(doc => new[] { doc["name"].AsString() }.Concat(doc["aliases"].Items.Select(alias => alias.AsString())))
            .Where(name => name.Contains(' '))
            .ToList();

        var dbcc = statements.FirstOrDefault(doc => IgnoreCase.Equals(doc["name"].AsString(), "DBCC"));
        var arguments = dbcc == null ? [] : ReadDbccArguments(dbcc);
        return new StatementDocs(names, arguments);
    }

    // DBCC 命令括號裡的關鍵字（CHECKIDENT 的 RESEED、CHECKDB 的 REPAIR_REBUILD）也只有說明列得出來：剖析器在括號裡
    // 什麼名稱都收。語法照 T-SQL 的語法慣例，關鍵字大寫、要填的值小寫；說明裡以「DBCC 命令」開頭的每一格
    // （簽章的每一行、對照表每一列的每一格）都是一種寫法，第一組括號裡的大寫字就是名單，引號裡的字不算。
    // 預覽的「命令」對照表一列寫一個命令，所以每個命令都要有一種寫法：少了就是預覽漏了那個命令。
    // 片語「DBCC 命令 (*」給這些字；括號裡照樣可以寫名稱與數值，不封閉。
    // 字不分是第幾個引數：每一格都不封閉，多出來的只是幾個字，分格的話每個命令的引數順序都要另外寫一份。
    private static List<KeyValuePair<string, List<string>>> ReadDbccArguments(JsonValue dbcc)
    {
        var aliases = dbcc["aliases"].Items.Select(alias => alias.AsString()).ToList();
        var cells = dbcc["references"].Items
            .SelectMany(reference => reference["rows"].Items)
            .SelectMany(row => row.Kind == JsonKind.Array ? row.Items : [row])
            .Select(cell => cell.AsString());
        var syntax = dbcc["signature"].AsString().Split('\n').Concat(cells).Where(line => Regex.IsMatch(line, "^DBCC [A-Z]"));

        var arguments = new List<KeyValuePair<string, List<string>>>();
        var byCommand = new Dictionary<string, List<string>>(IgnoreCase);

        foreach (var line in syntax)
        {
            var command = Regex.Match(line, "^DBCC (?<command>[A-Z][A-Z0-9_]*)").Groups["command"].Value;

            if (!aliases.Contains("DBCC " + command, IgnoreCase))
            {
                throw new InvalidOperationException($"DBCC 說明寫了 {command}，別名卻沒有 DBCC {command}：寫法與命令名單要一致。");
            }

            if (!byCommand.TryGetValue(command, out var words))
            {
                byCommand.Add(command, words = []);
                arguments.Add(new KeyValuePair<string, List<string>>(command, words));
            }

            if (!Regex.IsMatch(line, @"^DBCC [A-Z0-9_]+ [\[ ]*\("))
            {
                continue;
            }

            // 從第一個左括號走到配對的右括號。
            var open = line.IndexOf('(');
            var close = -1;

            for (int index = open, depth = 0; index < line.Length && close < 0; index++)
            {
                if (line[index] == '(')
                {
                    depth++;
                }
                else if (line[index] == ')' && --depth == 0)
                {
                    close = index;
                }
            }

            if (close < 0)
            {
                throw new InvalidOperationException($"DBCC {command} 的語法括號沒有關上：{line}");
            }

            var inside = Regex.Replace(line.Substring(open + 1, close - open - 1), "'[^']*'", string.Empty);

            foreach (Match argument in Regex.Matches(inside, @"\b[A-Z][A-Z0-9_]*\b"))
            {
                if (!words.Contains(argument.Value))
                {
                    words.Add(argument.Value);
                }
            }
        }

        var undocumented = aliases.Where(alias => !byCommand.ContainsKey(Regex.Replace(alias, "^DBCC ", string.Empty, RegexOptions.IgnoreCase))).ToList();

        if (undocumented.Count > 0)
        {
            throw new InvalidOperationException($"DBCC 說明沒有寫出這些命令的語法，預覽的命令對照表漏了它們：{string.Join(", ", undocumented)}");
        }

        return arguments;
    }
}
