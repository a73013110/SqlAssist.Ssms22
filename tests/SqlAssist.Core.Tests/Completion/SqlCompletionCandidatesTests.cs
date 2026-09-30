using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>分派問資料庫的那幾格：問哪一支、帶什麼、關掉資料庫時一支都不問。</summary>
public sealed class SqlCompletionCandidatesTests
{
    private static readonly IReadOnlyList<SqlSuggestion> BuiltIn = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);

    [Fact]
    public async Task 限定字指到別的資料庫時只列那個地方的物件()
    {
        var metadata = new RecordingMetadata { Objects = { new SqlSuggestion("Loan", "Loan", string.Empty, string.Empty, SuggestionKind.Table, schemaName: "dbo") } };

        var list = await GetAsync("SELECT * FROM LibArchive.dbo.|", new SqlAssistSettings(), metadata);

        Assert.Equal(new[] { "Loan" }, list.Select(item => item.DisplayText));
        Assert.Equal("LibArchive", Assert.Single(metadata.ObjectPaths)?.DatabaseName);
    }

    [Fact]
    public async Task 多個限定字的敘述裡快取欄位帶著別名查()
    {
        var metadata = new RecordingMetadata();

        await GetAsync("SELECT | FROM Lib_Reader r JOIN Loan l ON r.Id = l.ReaderId", new SqlAssistSettings(), metadata);

        Assert.Equal(new[] { "r", "l" }, metadata.PeekQualifiers);
    }

    [Fact]
    public async Task 關掉資料庫物件時不問資料庫_指令碼裡的欄位照列()
    {
        var metadata = new RecordingMetadata { Throws = true };
        var settings = new SqlAssistSettings { IncludeDatabaseObjects = false };

        var list = await GetAsync("WITH c AS (SELECT Id, Name FROM Lib_Reader) SELECT c.| FROM c", settings, metadata);

        Assert.Contains(list, item => item.DisplayText == "Name" && item.Kind == SuggestionKind.Column);
    }

    /// <param name="text">整份指令碼；<c>|</c> 是游標。</param>
    private static async Task<IReadOnlyList<SqlSuggestion>> GetAsync(
        string text,
        SqlAssistSettings settings,
        ISqlCompletionMetadata metadata)
    {
        var caret = text.IndexOf('|');
        var context = SqlCompletionContextAnalyzer.Analyze(text.Remove(caret, 1), caret);
        return await SqlCompletionCandidates.GetAsync(context, BuiltIn, settings, metadata, CancellationToken.None);
    }

    private sealed class RecordingMetadata : ISqlCompletionMetadata
    {
        public bool Throws { get; init; }

        public List<SqlSuggestion> Objects { get; } = new();

        public List<SqlObjectPath?> ObjectPaths { get; } = new();

        public List<string?> PeekQualifiers { get; } = new();

        public Task<SqlCompletionContext> ResolveQualifierAsync(SqlCompletionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(context);

        public Task<IReadOnlyList<SqlSuggestion>> GetObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken)
        {
            Asked();
            ObjectPaths.Add(qualifierPath);
            return Task.FromResult<IReadOnlyList<SqlSuggestion>>(Objects);
        }

        public Task<IReadOnlyList<SqlSuggestion>> GetSystemObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken) =>
            Nothing();

        public Task<IReadOnlyList<SqlSuggestion>> GetColumnsAsync(
            SqlTableReference table,
            SqlAssistSettings settings,
            CancellationToken cancellationToken) => Nothing();

        public IReadOnlyList<SqlSuggestion> PeekColumns(SqlTableReference table, string? qualifier, SqlAssistSettings settings)
        {
            Asked();
            PeekQualifiers.Add(qualifier);
            return Array.Empty<SqlSuggestion>();
        }

        public Task WarmColumnsAsync(IReadOnlyList<SqlColumnSource> sources, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SqlSuggestion>> GetParametersAsync(SqlExecutedModule module, CancellationToken cancellationToken) =>
            Nothing();

        public Task<SqlInstanceListData> GetInstanceListAsync(SqlInstanceList list, CancellationToken cancellationToken)
        {
            Asked();
            return Task.FromResult(SqlInstanceListData.Empty);
        }

        private Task<IReadOnlyList<SqlSuggestion>> Nothing()
        {
            Asked();
            return Task.FromResult<IReadOnlyList<SqlSuggestion>>(Array.Empty<SqlSuggestion>());
        }

        private void Asked()
        {
            if (Throws)
            {
                throw new InvalidOperationException("關掉資料庫物件時不該問資料庫。");
            }
        }
    }
}
