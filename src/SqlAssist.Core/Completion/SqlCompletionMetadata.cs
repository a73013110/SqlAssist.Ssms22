using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Completion;

/// <summary>現成的 <see cref="ISqlCompletionMetadata"/>。</summary>
public static class SqlCompletionMetadata
{
    /// <summary>
    /// 沒有資料庫：每一支都回空的，清單只剩內建項目與指令碼自己寫出來的名稱。
    /// </summary>
    /// <remarks>
    /// 與關掉「列出資料庫物件與欄位」不同：那個開關連指令碼裡的欄位來源（CTE、子查詢）都不併進一般位置，
    /// 這一個只是查不到東西，分派照常走。召回稽核沒有連線時用它。
    /// </remarks>
    public static ISqlCompletionMetadata None { get; } = new NoMetadata();

    private sealed class NoMetadata : ISqlCompletionMetadata
    {
        private static readonly Task<IReadOnlyList<SqlSuggestion>> Nothing =
            Task.FromResult<IReadOnlyList<SqlSuggestion>>(Array.Empty<SqlSuggestion>());

        public Task<SqlCompletionContext> ResolveQualifierAsync(SqlCompletionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(context);

        public Task<IReadOnlyList<SqlSuggestion>> GetObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken) =>
            Nothing;

        public Task<IReadOnlyList<SqlSuggestion>> GetSystemObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken) =>
            Nothing;

        public Task<IReadOnlyList<SqlSuggestion>> GetColumnsAsync(
            SqlTableReference table,
            SqlAssistSettings settings,
            CancellationToken cancellationToken) => Nothing;

        public IReadOnlyList<SqlSuggestion> PeekColumns(SqlTableReference table, string? qualifier, SqlAssistSettings settings) =>
            Array.Empty<SqlSuggestion>();

        public Task WarmColumnsAsync(IReadOnlyList<SqlColumnSource> sources, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SqlSuggestion>> GetParametersAsync(SqlExecutedModule module, CancellationToken cancellationToken) =>
            Nothing;

        public Task<SqlInstanceListData> GetInstanceListAsync(SqlInstanceList list, CancellationToken cancellationToken) =>
            Task.FromResult(SqlInstanceListData.Empty);
    }
}
