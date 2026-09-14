using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlRetentionSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static SqlRetentionSettings Plan(int draftDays = 30, int executionDays = 180,
        int? unsavedDays = 7, long? capacity = 1024, int quota = 100) =>
        new(TimeSpan.FromDays(draftDays), TimeSpan.FromDays(executionDays),
            unsavedDays.HasValue ? TimeSpan.FromDays(unsavedDays.Value) : null,
            capacity, quota, quota, quota);

    [Fact]
    public void TheDailyLevelIsExactlyWhatTheSettingsSay()
    {
        var daily = Plan().BuildLadder(Now, reclaimRecovery: true)[0];

        Assert.Equal(Now.AddDays(-30), daily.DraftBefore);
        Assert.Equal(Now.AddDays(-180), daily.ExecutionBefore);
        Assert.Equal(Now.AddDays(-7), daily.RecoveryBefore);
        Assert.Equal(1024, daily.MaxContentBytes);
        Assert.Equal(100, daily.MaxExecutionEvents);
        Assert.Equal(100, daily.MaxAutoRevisionsPerSession);
        Assert.Equal(100, daily.MaxRevisionsPerFavorite);
    }

    [Fact]
    public void TighterLevelsHalveTheRetentionAndTheQuotasButNotTheCapacityTrigger()
    {
        var ladder = Plan().BuildLadder(Now, reclaimRecovery: true);

        Assert.Equal(SqlRetentionSettings.Tightening.Count, ladder.Count);
        Assert.Equal(Now.AddDays(-15), ladder[1].DraftBefore);
        Assert.Equal(Now.AddDays(-45), ladder[2].ExecutionBefore);
        Assert.Equal(25, ladder[2].MaxExecutionEvents);
        // 容量上限是升級的觸發條件，不是分級內容；各級不同會讓「超限」在同一輪內換意思。
        Assert.Equal(1024, ladder[2].MaxContentBytes);
    }

    /// <summary>
    /// 收緊有下限：期限不縮到一天以下、配額不縮到零。
    /// </summary>
    /// <remarks>
    /// 收緊到零等於「全部刪掉」，那不是容量壓力該做的事——使用者要的是少留一點，
    /// 不是清空。已經在下限的日常保留只會原地不動，分級仍然成立（相等不算放寬）。
    /// </remarks>
    [Fact]
    public void TighteningStopsAtOneDayAndOneItem()
    {
        var ladder = Plan(draftDays: 1, executionDays: 1, unsavedDays: 1, quota: 1)
            .BuildLadder(Now, reclaimRecovery: true);

        Assert.Equal(Now.AddDays(-1), ladder[2].DraftBefore);
        Assert.Equal(Now.AddDays(-1), ladder[2].RecoveryBefore);
        Assert.Equal(1, ladder[2].MaxExecutionEvents);
    }

    /// <summary>宿主沒有在續心跳就整條分級都不給 Recovery 截止時間，不是只有日常那一級。</summary>
    [Fact]
    public void TheRecoveryDeadlineNeedsTheHostToBeRenewingTheHeartbeat()
    {
        var ladder = Plan().BuildLadder(Now, reclaimRecovery: false);

        for (var level = 0; level < ladder.Count; level++)
        {
            Assert.Null(ladder[level].RecoveryBefore);
        }

        // 使用者關掉未存檔草稿保留時也一樣：沒有期限就完全不憑年齡刪。
        Assert.Null(Plan(unsavedDays: null).BuildLadder(Now, reclaimRecovery: true)[0].RecoveryBefore);
    }

    /// <summary>持久化的維護輪次靠指紋判斷設定有沒有改；任何一個分級輸入不同都不能接續舊游標。</summary>
    [Fact]
    public void TheFingerprintChangesWithEveryRetentionInputButNotWithTime()
    {
        Assert.Equal(Plan().Fingerprint, Plan().Fingerprint);
        Assert.NotEqual(Plan().Fingerprint, Plan(draftDays: 29).Fingerprint);
        Assert.NotEqual(Plan().Fingerprint, Plan(executionDays: 179).Fingerprint);
        Assert.NotEqual(Plan().Fingerprint, Plan(unsavedDays: null).Fingerprint);
        Assert.NotEqual(Plan().Fingerprint, Plan(capacity: null).Fingerprint);
        Assert.NotEqual(Plan().Fingerprint, Plan(quota: 99).Fingerprint);
        Assert.NotEqual(Plan(quota: 1).Fingerprint, new SqlRetentionSettings(TimeSpan.FromDays(30),
            TimeSpan.FromDays(180), TimeSpan.FromDays(7), 1024, 1, 1, 2).Fingerprint);
    }

    [Theory]
    [InlineData(SqlMemoryStorageLimit.Megabytes256, 268435456L)]
    [InlineData(SqlMemoryStorageLimit.Megabytes512, 536870912L)]
    [InlineData(SqlMemoryStorageLimit.Gigabytes1, 1073741824L)]
    public void EachStorageLevelMapsToABoundedNumberOfBytes(SqlMemoryStorageLimit limit, long expected) =>
        Assert.Equal(expected, SqlRetentionSettings.ToContentBytes(limit));

    [Fact]
    public void UnlimitedStorageMeansNoCapacityTriggerAtAll() =>
        Assert.Null(SqlRetentionSettings.ToContentBytes(SqlMemoryStorageLimit.Unlimited));

    [Fact]
    public void RetentionLengthsAndQuotasMustBeUsable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(draftDays: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(executionDays: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(unsavedDays: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(capacity: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(quota: -1));
    }
}
