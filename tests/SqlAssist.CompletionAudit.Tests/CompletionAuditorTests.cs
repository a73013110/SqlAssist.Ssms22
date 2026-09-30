using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.CompletionAudit.Tests;

/// <summary>
/// 判定本身：哪些詞元算答案、漏的樣子與簽章、範例怎麼遮。
/// </summary>
/// <remarks>
/// 內建清單給空的，字一定列不出來：這裡驗的是判定，不是產品目前補到哪裡——產品補上一個缺口
/// 不該讓這些測試失敗。
/// </remarks>
public sealed class CompletionAuditorTests
{
    [Fact]
    public async Task 列不出來的字記成不在候選_簽章只看前兩個詞元的形狀()
    {
        var result = await AuditAsync("SELECT 1", AuditCatalog.None);

        var miss = Assert.Single(result.Misses);
        Assert.Equal(AuditTokenClass.Word, miss.TokenClass);
        Assert.Equal(AuditMissKind.Absent, miss.Kind);
        Assert.Equal("^ ^ ⎵SELECT [Absent]", miss.Signature.Text);
        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.Literal]);
    }

    [Fact]
    public async Task 沒有資料庫時名稱查不到存在_排除不算漏()
    {
        var result = await AuditAsync("SELECT Lib_Reader.ReaderId", AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.TokenClass != AuditTokenClass.Word);
        Assert.Equal(2, result.Tally.Excluded[AuditExclusion.Unresolved]);
    }

    [Fact]
    public async Task 名稱索引認得的欄位要列得出來()
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("SELECT r.ReaderId FROM Lib_Reader r", catalog);

        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column && miss.Word == "ReaderId");
        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Object && miss.Word == "Lib_Reader");
    }

    [Fact]
    public async Task 新取的名稱與常值不稽核()
    {
        var result = await AuditAsync("DECLARE @n int = 5", AuditCatalog.None);

        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.NewName]);
        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.Literal]);
    }

    [Fact]
    public async Task 範例遮掉名稱_常值與註解內容()
    {
        var catalog = new FakeCatalog(("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("-- 讀者 Lib_Reader.\nSELECT 'x' FROM Lib_Reader WHERE", catalog, new BracketMask());

        var miss = result.Misses.Single(item => item.Word == "WHERE");
        Assert.Equal("--…\nSELECT '…' FROM <Object> ⎵WHERE", miss.MaskedExample);
        Assert.DoesNotContain("讀者", miss.MaskedExample);
        Assert.Contains("Lib_Reader", miss.Example);
    }

    [Fact]
    public async Task 同一個位置每一次都是同一群()
    {
        var first = await AuditAsync("SELECT 1", AuditCatalog.None);
        var second = await AuditAsync("\n\nSELECT 2", AuditCatalog.None);

        Assert.Equal(first.Misses.Single().Signature.Id, second.Misses.Single().Signature.Id);
    }

    [Fact]
    public async Task 重驗只問那一個位置()
    {
        var auditor = new CompletionAuditor(new SqlAssistSettings(), Array.Empty<SqlSuggestion>(), AuditMask.None);
        var fragment = new AuditFragment("test", "t", "SELECT 1 FROM t");

        var miss = await auditor.RecheckAsync(fragment, "SELECT 1 ".Length, AuditCatalog.None, CancellationToken.None);

        Assert.Equal("FROM", miss?.Word);
    }

    private static Task<AuditResult> AuditAsync(string sql, IAuditCatalog catalog, IAuditMask? mask = null) =>
        new CompletionAuditor(new SqlAssistSettings(), Array.Empty<SqlSuggestion>(), mask ?? AuditMask.None)
            .AuditAsync(new AuditFragment("test", "t", sql), catalog, CancellationToken.None);

    private sealed class BracketMask : IAuditMask
    {
        public string Mask(string name, AuditTokenClass? tokenClass) => $"<{tokenClass}>";
    }

    /// <summary>只認得指定的名稱，清單的資料庫那一份一律是空的。</summary>
    private sealed class FakeCatalog : IAuditCatalog, IAuditNameIndex
    {
        private readonly Dictionary<string, AuditTokenClass> _names;

        public FakeCatalog(params (string Name, AuditTokenClass Class)[] names)
        {
            _names = names.ToDictionary(item => AuditText.Normalize(item.Name), item => item.Class);
        }

        public ISqlCompletionMetadata Metadata => SqlCompletionMetadata.None;

        public Task<IAuditNameIndex> IndexAsync(string batch, IReadOnlyList<SqlToken> tokens, CancellationToken cancellationToken) =>
            Task.FromResult<IAuditNameIndex>(this);

        public AuditTokenClass? Find(string name) =>
            _names.TryGetValue(AuditText.Normalize(name), out var tokenClass) ? tokenClass : null;
    }
}
