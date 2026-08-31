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
        bool isDisabled,
        bool isShadowed = false)
    {
        Snippet = snippet;
        IsBuiltIn = isBuiltIn;
        IsCustomized = isCustomized;
        IsDisabled = isDisabled;
        IsShadowed = isShadowed;
    }

    public SqlSnippet Snippet { get; }

    public bool IsBuiltIn { get; }

    public bool IsCustomized { get; }

    /// <summary>使用者主動停用；會寫成檔案裡的停用紀錄。</summary>
    public bool IsDisabled { get; }

    /// <summary>
    /// 捷徑被另一筆優先的項目佔走，因此這一輪不進建議清單。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="IsDisabled"/> 分開的理由：遮住是<b>當下的計算結果</b>，
    /// 不是使用者的決定。混成同一個狀態時，存檔會替被遮住的內建片段寫下
    /// 永久的停用紀錄——使用者只是自己建了一個同捷徑的片段，之後把它改名，
    /// 內建那筆卻再也回不來了。
    /// </remarks>
    public bool IsShadowed { get; }

    /// <summary>這一輪要不要出現在建議清單。</summary>
    public bool IsEffective => !IsDisabled && !IsShadowed;
}
