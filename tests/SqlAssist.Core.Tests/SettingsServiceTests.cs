using System;
using System.IO;
using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public SettingsServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"SqlAssist.Tests.{Guid.NewGuid():N}");
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void 預設值符合預期()
    {
        var snapshot = new SettingsService(_path).GetSnapshot();

        Assert.True(snapshot.Enabled);
        Assert.True(snapshot.Features.TabExpansion);
        Assert.True(snapshot.Features.KeywordUppercase);
        Assert.True(snapshot.Features.ObjectPicker);
        Assert.False(snapshot.DiagnosticsEnabled);
        Assert.True(snapshot.Suggestions.Enabled);
        Assert.Equal(1, snapshot.Suggestions.TriggerAfterCharacters);
        Assert.True(snapshot.Suggestions.ShowPreview);
        Assert.False(snapshot.Suggestions.QualifyObjectNames);
        Assert.False(snapshot.Suggestions.UseSquareBrackets);
    }

    [Fact]
    public void 切換後會永久保存()
    {
        var service = new SettingsService(_path);
        service.ToggleEnabled();
        service.ToggleFeature(SqlAssistFeature.KeywordUppercase);
        service.ToggleDiagnostics();

        var reloaded = new SettingsService(_path).GetSnapshot();

        Assert.False(reloaded.Enabled);
        Assert.False(reloaded.Features.KeywordUppercase);
        Assert.True(reloaded.DiagnosticsEnabled);
    }

    [Fact]
    public void 原子寫入不會留下暫存檔()
    {
        var service = new SettingsService(_path);
        service.ToggleEnabled();
        service.ToggleSuggestions();

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void 快照是複本_修改不會影響服務內部狀態()
    {
        var service = new SettingsService(_path);
        var snapshot = service.GetSnapshot();
        snapshot.Enabled = false;
        snapshot.Suggestions.MaximumItems = 1;

        var fresh = service.GetSnapshot();

        Assert.True(fresh.Enabled);
        Assert.Equal(100, fresh.Suggestions.MaximumItems);
    }

    [Fact]
    public void 外部改寫設定檔後會重新載入()
    {
        var service = new SettingsService(_path);
        Assert.True(service.GetSnapshot().Enabled);

        File.WriteAllText(_path, "{\"enabled\":false}");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(1));

        Assert.False(service.GetSnapshot().Enabled);
    }
}
