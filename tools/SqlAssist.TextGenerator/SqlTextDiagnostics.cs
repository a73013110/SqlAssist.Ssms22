using Microsoft.CodeAnalysis;

namespace SqlAssist.TextGenerator;

/// <summary>產生器與分析器回報的診斷；全部是錯誤，不給「先建置過再說」的空間。</summary>
internal static class SqlTextDiagnostics
{
    private const string Category = "SqlAssist.Localization";

    public static readonly DiagnosticDescriptor NoLanguages = Error(
        "SQLTXT001",
        "沒有設定語言清單",
        "專案有 .resjson 卻沒有 SqlAssistTextLanguages 屬性；語言清單在根目錄的 Directory.Build.props");

    public static readonly DiagnosticDescriptor BadFileName = Error(
        "SQLTXT002",
        ".resjson 檔名不合規則",
        "'{0}' 必須命名為 <類別>.<語言>.resjson，語言是 {1} 其中之一");

    public static readonly DiagnosticDescriptor MissingLanguage = Error(
        "SQLTXT003",
        "缺少一種語言的文字檔",
        "{0} 缺少 {1} 的文字檔 {0}.{1}.resjson");

    public static readonly DiagnosticDescriptor BadNamespace = Error(
        "SQLTXT004",
        "推不出命名空間",
        "'{0}' 不在 src/<專案>/ 底下，推不出產生類別的命名空間");

    public static readonly DiagnosticDescriptor BadJson = Error(
        "SQLTXT005",
        ".resjson 內容不合規則",
        "{0}");

    public static readonly DiagnosticDescriptor BadKey = Error(
        "SQLTXT006",
        "鍵名不是大寫開頭的識別字",
        "鍵 '{0}' 會成為 C# 成員名稱，必須是大寫開頭的英數字");

    public static readonly DiagnosticDescriptor BadTemplate = Error(
        "SQLTXT007",
        "文字的佔位符格式錯誤",
        "{0}：{1}");

    public static readonly DiagnosticDescriptor MissingKey = Error(
        "SQLTXT008",
        "某種語言缺少這個鍵",
        "{0}.{1}.resjson 缺少鍵 '{2}'");

    public static readonly DiagnosticDescriptor ExtraKey = Error(
        "SQLTXT009",
        "某種語言多出來源語言沒有的鍵",
        "{0}.{1}.resjson 多了來源語言沒有的鍵 '{2}'");

    public static readonly DiagnosticDescriptor PlaceholderMismatch = Error(
        "SQLTXT010",
        "各語言的佔位符不一致",
        "{0}.{1}.resjson 的 '{2}' 佔位符是 {3}，來源語言是 {4}");

    public static readonly DiagnosticDescriptor Untranslated = Error(
        "SQLTXT011",
        "譯文裡有中日韓字元",
        "{0}.{1}.resjson 的 '{2}' 含中日韓字元，像是沒翻譯或貼錯語言");

    public static readonly DiagnosticDescriptor LiteralText = new(
        "SQLTXT100",
        "程式碼裡的字面中文",
        "使用者看得到的文字要放進 .resjson（\"{0}\"）；只寫進診斷紀錄的文字改在接收的參數或所在成員標上 [Localizable(false)]",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static DiagnosticDescriptor Error(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
