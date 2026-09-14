using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SqlAssist.Ssms22.Tests;

public sealed class KeyboardDeviceUsageTests
{
    [Fact]
    public void 產品碼判斷按鍵狀態只讀事件帶的鍵盤裝置()
    {
        // 靜態 Keyboard 讀執行緒的 Win32 按鍵狀態，會混入實體鍵盤；
        // 測試無法替換，push 時按著 Ctrl 就讓合成的 Enter 變成 Ctrl+Enter。
        var pattern = new Regex(@"\bKeyboard\.(Modifiers|IsKeyDown|IsKeyUp|IsKeyToggled|GetKeyStates)\b");
        var root = FindProductSourceDirectory();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => pattern.IsMatch(entry.line))
            .Select(entry => $"{entry.path.Substring(root.Length + 1)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "改用 KeyEventArgs.KeyboardDevice：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string FindProductSourceDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "SqlAssist.Ssms22");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("找不到 src/SqlAssist.Ssms22。");
    }
}
