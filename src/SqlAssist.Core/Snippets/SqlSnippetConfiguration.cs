using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Snippets;

/// <summary>合併內建值與使用者紀錄後的穩定快照。</summary>
public sealed class SqlSnippetConfiguration
{
    public SqlSnippetConfiguration(
        SqlSnippetLibrary library,
        IReadOnlyList<SqlSnippetConfigurationEntry> entries,
        SqlSnippetDocument document)
    {
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Entries = entries ?? Array.Empty<SqlSnippetConfigurationEntry>();
        Document = document ?? SqlSnippetDocument.Empty;
    }

    /// <summary>建議清單使用的啟用項目。</summary>
    public SqlSnippetLibrary Library { get; }

    /// <summary>管理介面使用的全部項目，包含被停用的內建片段。</summary>
    public IReadOnlyList<SqlSnippetConfigurationEntry> Entries { get; }

    public SqlSnippetDocument Document { get; }
}

public sealed class SqlSnippetConfigurationEntry
{
    public SqlSnippetConfigurationEntry(
        SqlSnippet snippet,
        bool isBuiltIn,
        bool isCustomized,
        bool isDisabled)
    {
        Snippet = snippet;
        IsBuiltIn = isBuiltIn;
        IsCustomized = isCustomized;
        IsDisabled = isDisabled;
    }

    public SqlSnippet Snippet { get; }

    public bool IsBuiltIn { get; }

    public bool IsCustomized { get; }

    public bool IsDisabled { get; }
}
