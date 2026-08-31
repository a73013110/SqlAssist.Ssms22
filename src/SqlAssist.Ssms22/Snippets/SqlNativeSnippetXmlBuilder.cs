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
    private static readonly ConditionalWeakTable<SqlSnippet, CachedXml> Cache = new();

    /// <summary>在候選目錄重建時先算好字串，提交按鍵只付一次 MSXML DOM 成本。</summary>
    public static void Prepare(SqlSnippet snippet)
    {
        if (snippet.ExpansionMode == SqlSnippetExpansionMode.TabStops)
        {
            _ = Cache.GetValue(snippet, Build).CrLf;
        }
    }

    public static SqlNativeSnippetDom CreateNode(SqlSnippet snippet, string newLine)
    {
        var cached = Cache.GetValue(snippet, Build);
        var xml = string.Equals(newLine, "\n", StringComparison.Ordinal)
            ? cached.Lf
            : cached.CrLf;
        var documentType = Type.GetTypeFromProgID("Msxml2.DOMDocument.6.0", throwOnError: false)
            ?? Type.GetTypeFromProgID("Msxml2.DOMDocument", throwOnError: true)!;
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

    internal static string GetXml(SqlSnippet snippet, string newLine = "\r\n")
    {
        var cached = Cache.GetValue(snippet, Build);
        return string.Equals(newLine, "\n", StringComparison.Ordinal) ? cached.Lf : cached.CrLf;
    }

    private static CachedXml Build(SqlSnippet snippet)
    {
        var expansion = snippet.Expansion;
        return new CachedXml(
            BuildXml(snippet, expansion, "\r\n"),
            BuildXml(snippet, expansion, "\n"));
    }

    private static string BuildXml(SqlSnippet snippet, SqlSnippetExpansion expansion, string newLine)
    {
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
                xml.Append("<Literal><ID>");
                AppendText(xml, field.Id);
                xml.Append("</ID><ToolTip>");
                AppendText(xml, field.ToolTip);
                xml.Append("</ToolTip><Default>");

                // 空 Default 在不同版本的 Expansion Engine 行為不一致；ID 本身可選取、
                // 可直接覆寫，且不會產生一個看不見卻佔著 Tab 順序的欄位。
                AppendText(xml, string.IsNullOrEmpty(field.DefaultValue) ? field.Id : field.DefaultValue);
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

    private sealed class CachedXml
    {
        public CachedXml(string crLf, string lf)
        {
            CrLf = crLf;
            Lf = lf;
        }

        public string CrLf { get; }

        public string Lf { get; }
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }
}

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
