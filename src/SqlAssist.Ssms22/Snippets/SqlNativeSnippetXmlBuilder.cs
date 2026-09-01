using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MSXML;
using SqlAssist.Core.Snippets;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>把 Core 的展開結果翻成 SSMS 原生 CodeSnippet XML。</summary>
internal static class SqlNativeSnippetXmlBuilder
{
    private const string Namespace = "http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet";

    /// <summary>
    /// 每一筆片段的 XML 只組一次。
    /// </summary>
    /// <remarks>
    /// 鍵是不可變的片段本身，所以整份清單換掉時快取跟著回收，不必手動失效。
    /// 刻意在提交時才組：預先替 36 筆各組兩份（CRLF 與 LF）是替使用者這一輩子
    /// 可能只用兩三筆的東西付錢，而組一份 1KB 字串的成本，比接著要做的
    /// MSXML DOM 建立與 COM 呼叫小上好幾個數量級。
    /// </remarks>
    private static readonly ConditionalWeakTable<SqlSnippet, CachedXml> Cache = new();

    private static readonly ConditionalWeakTable<SqlSnippet, CachedXml>.CreateValueCallback CreateCache =
        snippet => new CachedXml(snippet);

    /// <summary>
    /// MSXML 的型別；<c>GetTypeFromProgID</c> 會查登錄，不必每次提交都付。
    /// </summary>
    private static readonly Lazy<Type> DocumentType = new(
        () => Type.GetTypeFromProgID("Msxml2.DOMDocument.6.0", throwOnError: false)
            ?? Type.GetTypeFromProgID("Msxml2.DOMDocument", throwOnError: true)!,
        isThreadSafe: true);

    public static SqlNativeSnippetDom CreateNode(SqlSnippet snippet, string newLine)
    {
        var xml = Cache.GetValue(snippet, CreateCache).For(newLine);
        var documentType = DocumentType.Value;
        var document = Activator.CreateInstance(documentType)
            ?? throw new InvalidOperationException("無法建立 MSXML DOMDocument。");
        var transferred = false;

        try
        {
            var loaded = documentType.InvokeMember(
                "loadXML",
                BindingFlags.InvokeMethod,
                binder: null,
                target: document,
                args: new object[] { xml },
                culture: CultureInfo.InvariantCulture);

            if (loaded is not true)
            {
                throw new InvalidOperationException("原生 Snippet XML 無法由 MSXML 剖析。");
            }

            var root = documentType.InvokeMember(
                "documentElement",
                BindingFlags.GetProperty,
                binder: null,
                target: document,
                args: null,
                culture: CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("原生 Snippet XML 沒有根節點。");

            try
            {
                var codeSnippet = root.GetType().InvokeMember(
                    "firstChild",
                    BindingFlags.GetProperty,
                    binder: null,
                    target: root,
                    args: null,
                    culture: CultureInfo.InvariantCulture)
                    ?? throw new InvalidOperationException("原生 Snippet XML 沒有 CodeSnippet 節點。");

                transferred = true;
                return new SqlNativeSnippetDom(document, (IXMLDOMNode)codeSnippet);
            }
            finally
            {
                ReleaseComObject(root);
            }
        }
        finally
        {
            if (!transferred)
            {
                ReleaseComObject(document);
            }
        }
    }

    internal static string GetXml(SqlSnippet snippet, string newLine = "\r\n") =>
        Cache.GetValue(snippet, CreateCache).For(newLine);

    private static string BuildXml(SqlSnippet snippet, string newLine)
    {
        var expansion = snippet.Expansion;
        var xml = new StringBuilder(snippet.Code.Length + 512);
        xml.Append("<CodeSnippets xmlns=\"").Append(Namespace).Append("\">");
        xml.Append("<CodeSnippet Format=\"1.0.0\"><Header><Title>");
        AppendText(xml, snippet.Title);
        xml.Append("</Title><Shortcut>");
        AppendText(xml, snippet.Shortcut);
        xml.Append("</Shortcut><Description>");
        AppendText(xml, snippet.Description);
        xml.Append("</Description><Author>SqlAssist</Author><SnippetTypes>");
        xml.Append("<SnippetType>Expansion</SnippetType></SnippetTypes></Header><Snippet>");

        if (expansion.Fields.Count > 0)
        {
            xml.Append("<Declarations>");

            foreach (var field in expansion.Fields)
            {
                var placeholder = field.Placeholder;
                xml.Append("<Literal><ID>");
                AppendText(xml, placeholder.Id);
                xml.Append("</ID><ToolTip>");
                AppendText(xml, placeholder.ToolTip);
                xml.Append("</ToolTip><Default>");

                // 空 Default 在不同版本的 Expansion Engine 行為不一致；ID 本身可選取、
                // 可直接覆寫，且不會產生一個看不見卻佔著 Tab 順序的欄位。
                AppendText(
                    xml,
                    string.IsNullOrEmpty(placeholder.DefaultValue)
                        ? placeholder.Id
                        : placeholder.DefaultValue);
                xml.Append("</Default></Literal>");
            }

            xml.Append("</Declarations>");
        }

        xml.Append("<Code Language=\"SQL\">");
        AppendText(xml, expansion.GetNativeCode(newLine));
        xml.Append("</Code></Snippet></CodeSnippet></CodeSnippets>");
        return xml.ToString();
    }

    private static void AppendText(StringBuilder target, string? value)
    {
        foreach (var character in value ?? string.Empty)
        {
            switch (character)
            {
                case '&': target.Append("&amp;"); break;
                case '<': target.Append("&lt;"); break;
                case '>': target.Append("&gt;"); break;
                case '\r': target.Append("&#13;"); break;
                case '\n': target.Append("&#10;"); break;
                default: target.Append(character); break;
            }
        }
    }

    /// <summary>一筆片段的 XML，兩種換行各自在第一次要到時才組。</summary>
    /// <remarks>
    /// 一份指令碼只會用到其中一種，另一種通常一輩子不會被要。沒有加鎖：
    /// 提交都在 UI 執行緒上，就算真的重入，兩邊算出來的也是同一個字串。
    /// </remarks>
    private sealed class CachedXml
    {
        private readonly SqlSnippet _snippet;
        private string? _crLf;
        private string? _lf;

        public CachedXml(SqlSnippet snippet)
        {
            _snippet = snippet;
        }

        public string For(string newLine)
        {
            return string.Equals(newLine, "\n", StringComparison.Ordinal)
                ? _lf ??= BuildXml(_snippet, "\n")
                : _crLf ??= BuildXml(_snippet, "\r\n");
        }
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }
}

/// <summary>
/// 一次提交用的 MSXML 文件。
/// </summary>
/// <remarks>
/// 這裡<b>可以</b>釋放 COM 參考，與 <see cref="SqlSnippetExpansionController"/> 刻意不釋放
/// <c>IVsExpansionSession</c> 是兩回事：這份 DOM 是我們自己 <c>CreateInstance</c> 出來的，
/// 除了引擎在 <c>InsertSpecificExpansion</c> 期間讀過之外沒有別人持有，而那個呼叫返回時
/// 引擎已經把需要的東西複製走了。每按一次 Tab 就留一份 DOM 等 GC 沒有道理。
/// </remarks>
internal sealed class SqlNativeSnippetDom : IDisposable
{
    private object? _document;

    public SqlNativeSnippetDom(object document, IXMLDOMNode node)
    {
        _document = document;
        Node = node;
    }

    public IXMLDOMNode Node { get; private set; }

    public void Dispose()
    {
        if (Marshal.IsComObject(Node))
        {
            _ = Marshal.ReleaseComObject(Node);
        }

        Node = null!;

        if (_document is { } document && Marshal.IsComObject(document))
        {
            _ = Marshal.ReleaseComObject(document);
        }

        _document = null;
    }
}
