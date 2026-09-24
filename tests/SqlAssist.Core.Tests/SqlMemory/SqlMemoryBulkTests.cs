using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryBulkTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task 逐頁讀到沒有游標並回報進度()
    {
        var pages = new Queue<SqlMemoryPage<int>>(new[]
        {
            new SqlMemoryPage<int>(new[] { 1, 2 }, "a"),
            // 搜尋預算用盡的空頁仍帶游標，要照著讀下去，不當成讀完。
            new SqlMemoryPage<int>(Array.Empty<int>(), "b", At),
            new SqlMemoryPage<int>(new[] { 3 }, null),
        });
        var cursors = new List<string?>();
        var reported = new List<int>();

        var batch = await SqlMemoryBulk.ReadAllAsync((cursor, _) =>
        {
            cursors.Add(cursor);
            return Task.FromResult(pages.Dequeue());
        }, 10, new ImmediateProgress(reported), CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, batch.Items);
        Assert.False(batch.IsTruncated);
        Assert.Equal(new string?[] { null, "a", "b" }, cursors);
        Assert.Equal(new[] { 2, 2, 3 }, reported);
    }

    [Fact]
    public async Task 超過上限就截斷且剛好等於上限不算截斷()
    {
        Task<SqlMemoryPage<int>> Pages(string? cursor, int total)
        {
            var start = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var items = Enumerable.Range(start, Math.Min(3, total - start)).ToArray();
            var next = start + items.Length;
            return Task.FromResult(new SqlMemoryPage<int>(items,
                next < total ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }

        var over = await SqlMemoryBulk.ReadAllAsync((cursor, _) => Pages(cursor, 8), 5, null, CancellationToken.None);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, over.Items);
        Assert.True(over.IsTruncated);

        var exact = await SqlMemoryBulk.ReadAllAsync((cursor, _) => Pages(cursor, 6), 6, null, CancellationToken.None);
        Assert.Equal(6, exact.Items.Count);
        Assert.False(exact.IsTruncated);
    }

    [Fact]
    public async Task 取消在頁與頁之間生效()
    {
        using var cancel = new CancellationTokenSource();
        var reads = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlMemoryBulk.ReadAllAsync((_, _) =>
        {
            reads++;
            cancel.Cancel();
            return Task.FromResult(new SqlMemoryPage<int>(new[] { reads }, "next"));
        }, 100, null, cancel.Token));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void 上限與預設執行保留筆數同一級()
    {
        Assert.Equal(SqlAssist.Core.Settings.SqlAssistLimits.DefaultSqlMemoryExecutions, SqlMemoryBulk.Limit);
        Assert.Equal(200, SqlMemoryBulk.PageSize);
        _ = new SqlHistoryRequest(SqlMemoryBulk.PageSize);
    }

    internal sealed class ImmediateProgress : IProgress<int>
    {
        private readonly List<int> _values;

        public ImmediateProgress(List<int> values) => _values = values;

        public void Report(int value) => _values.Add(value);
    }
}
