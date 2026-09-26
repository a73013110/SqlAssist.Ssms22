using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SqlAssist.Ssms22.Tests;

public sealed class KeyboardDeviceUsageTests
{
    /// <summary>
    /// 例外一：滑鼠事件上沒有隨事件帶的鍵盤裝置。
    /// </summary>
    /// <remarks>
    /// <c>MouseWheelEventArgs</c> 帶的是滑鼠裝置，所以 Shift＋滾輪只問得到目前的鍵盤狀態。
    /// 這條規則防的是<b>合成的按鍵</b>混進實體鍵盤狀態，而滾輪不會被合成。例外只有這兩個
    /// 名字，要再加第三個得連同這一段一起說得出理由。
    /// </remarks>
    private const string ShiftHeldException = "ShiftHeld";

    /// <summary>
    /// 例外二：清單多選的 Ctrl／Shift+點擊（<c>SqlCardListBase.ModifierSource</c>）。
    /// </summary>
    /// <remarks>
    /// 與滾輪同一個理由：<c>MouseButtonEventArgs</c> 也只帶滑鼠裝置。它另外做成可替換的屬性，
    /// 測試換掉它，所以合成的點擊不會讀到實體鍵盤——這正是這條規則要的。按鍵路徑（空白鍵、
    /// Ctrl+A、Ctrl+C）仍然只讀 <c>KeyEventArgs.KeyboardDevice</c>。
    /// 編輯器的 Ctrl＋點擊（<c>SqlClickNavigator.ModifierSource</c>）是同一個例外、同一種做法：
    /// 滑鼠移動與按下讀它，修飾鍵的按下與放開仍由按鍵事件帶進來。
    /// </remarks>
    private const string ModifierSourceException = "ModifierSource";

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
            .Where(entry => pattern.IsMatch(entry.line) && !entry.line.Contains(ShiftHeldException) &&
                !entry.line.Contains(ModifierSourceException))
            .Select(entry => $"{entry.path.Substring(root.Length + 1)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "改用 KeyEventArgs.KeyboardDevice：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void 測試合成的按鍵只用測試指定的鍵盤裝置()
    {
        // Keyboard.PrimaryDevice 的修飾鍵讀自實體鍵盤：整輪測試跑到一半時使用者按著 Shift 或 Ctrl，
        // 合成的 Home／End 就被產品碼當成組合鍵放過，斷言只在那一輪失敗、單獨重跑又通過。
        var pattern = new Regex(@"\bKeyboard\.PrimaryDevice\b");
        var root = FindRepositoryDirectory(Path.Combine("tests", "SqlAssist.Ssms22.Tests"));
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(root, path))
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => pattern.IsMatch(entry.line) && !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(entry => $"{entry.path.Substring(root.Length + 1)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "改用 new TestKeyboardDevice(...)：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var relative = path.Substring(root.Length + 1);
        return relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindProductSourceDirectory() => FindRepositoryDirectory(Path.Combine("src", "SqlAssist.Ssms22"));

    private static string FindRepositoryDirectory(string relativePath)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"找不到 {relativePath}。");
    }
}
