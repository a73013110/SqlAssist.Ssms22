using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Snippets;

/// <summary>%APPDATA% 檔案的內容；v2 的 snippets 陣列只存 override、停用與自訂項目。</summary>
public sealed class SqlSnippetDocument
{
    public SqlSnippetDocument(int version, IReadOnlyList<SqlSnippetOverride>? snippets = null)
    {
        Version = version;
        Snippets = snippets ?? Array.Empty<SqlSnippetOverride>();
    }

    public int Version { get; }

    public IReadOnlyList<SqlSnippetOverride> Snippets { get; }

    public bool IsNewerThanSupported => Version > SqlSnippetLibrary.CurrentVersion;

    public static SqlSnippetDocument Empty { get; } = new(
        SqlSnippetLibrary.CurrentVersion,
        Array.Empty<SqlSnippetOverride>());
}

public sealed class SqlSnippetOverride
{
    public SqlSnippetOverride(string id, bool disabled, SqlSnippet? snippet = null)
    {
        Id = id ?? string.Empty;
        Disabled = disabled;
        Snippet = snippet;
    }

    public string Id { get; }

    public bool Disabled { get; }

    /// <summary>停用紀錄可以沒有完整定義；其餘紀錄必須有值。</summary>
    public SqlSnippet? Snippet { get; }
}
