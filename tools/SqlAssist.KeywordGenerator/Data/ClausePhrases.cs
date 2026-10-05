namespace SqlAssist.KeywordGenerator.Data;

/// <summary>第四階段的片語宣告：寫法見 <see cref="PhraseDeclaration"/>，各領域的宣告在同一資料夾的 *Phrases.cs。</summary>
internal static class ClausePhrases
{
    // 探測順序只在這裡排。順序就是產物裡片語的順序，也決定誰先佔用展開過的格子：已探到同樣深度的一格
    // 不再展開，同一條尾巴、同一個位置後寫的覆蓋先寫的（Expand 展開出來的片語可以在後面補 Values）。
    // 所以各領域分成幾段，段與段照舊有的次序交錯；新增的片語加在它那一段裡就好。
    internal static readonly PhraseDeclaration[] All =
    [
        .. StatementPhrases.Set,
        .. DdlPhrases.Objects,
        .. DatabasePhrases.Files,
        .. DatabasePhrases.SetOptions,
        .. DatabasePhrases.Server,
        .. StatementPhrases.BackupHeaders,
        .. IndexPhrases.Options,
        .. DdlPhrases.TriggerPositions,
        .. StatementPhrases.BackupOptions,
        .. StatementPhrases.Statistics,
        .. StatementPhrases.Transactions,
        .. DdlPhrases.TriggersAndModules,
        .. QueryPhrases.Merge,
        .. StatementPhrases.ExecuteAs,
        .. DdlPhrases.Tables,
        .. QueryPhrases.Rowsets,
        .. PolyBaseXePhrases.ExternalData,
        .. SecurityPhrases.AuditAndPolicies,
        .. PolyBaseXePhrases.ExternalModels,
        .. PolyBaseXePhrases.ExternalLibraries,
        .. PolyBaseXePhrases.Events,
        .. DdlPhrases.Sequence,
        .. StatementPhrases.Waitfor,
        .. CursorPhrases.OpenClose,
        .. SecurityPhrases.Keys,
        .. CursorPhrases.Fetch,
        .. StatementPhrases.Dbcc,
        .. QueryPhrases.Clauses,
        .. StatementPhrases.ExecOptions,
        .. QueryPhrases.TableSample,
        .. SecurityPhrases.Principals,
        .. DdlPhrases.Constraint,
        .. QueryPhrases.OffsetFetch,
        .. QueryPhrases.JsonFunctions,
        .. WindowPhrases.Frames,
        .. ServiceBrokerPhrases.All,
    ];
}
