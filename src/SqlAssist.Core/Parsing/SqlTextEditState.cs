using System;

namespace SqlAssist.Core.Parsing;

/// <summary>編輯只保存原文，不從著色文件反向取得 SQL；不綁任何儲存功能。</summary>
public sealed class SqlTextEditState
{
    public SqlTextEditState(string sql) { Original = sql ?? throw new ArgumentNullException(nameof(sql)); Text = sql; }
    public string Original { get; }
    public string Text { get; set; }
    public bool IsModified => !string.Equals(Original, Text, StringComparison.Ordinal);
}
