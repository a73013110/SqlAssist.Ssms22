namespace SqlAssist.Core;

/// <summary>T-SQL 文字在某個位置所處的語彙狀態。</summary>
public enum SqlLexicalState
{
    /// <summary>一般程式碼。</summary>
    Code,

    /// <summary>雙連字號開始的單行註解。</summary>
    LineComment,

    /// <summary>斜線星號包住的區塊註解。</summary>
    BlockComment,

    /// <summary>單引號字串常值。</summary>
    String,

    /// <summary>雙引號識別字（需 QUOTED_IDENTIFIER ON）。</summary>
    QuotedIdentifier,

    /// <summary>方括號識別字。</summary>
    BracketedIdentifier
}
