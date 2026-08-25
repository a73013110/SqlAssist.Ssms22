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
        Assert.Equal(CompletionEngine.Native, snapshot.Suggestions.Engine);
        Assert.True(snapshot.Suggestions.SuppressNativeIntelliSense);
        Assert.Equal(SqlPreviewMode.RightArrow, snapshot.Preview.Mode);
    }

    /// <summary>設定檔是舊版、沒有 preview 區段時不能讀出 null。</summary>
    [Fact]
    public void 舊設定檔缺少預覽區段時補上預設值()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"enabled\":true}");
        var snapshot = new SettingsService(_path).GetSnapshot();

        Assert.NotNull(snapshot.Preview);
        Assert.Equal(SqlPreviewMode.RightArrow, snapshot.Preview.Mode);
    }

    [Fact]
    public void 預覽模式以字串保存並讀回()
    {
        var service = new SettingsService(_path);
        service.Update(settings => settings.Preview.Mode = SqlPreviewMode.Delay);

        Assert.Contains("\"mode\":\"delay\"", File.ReadAllText(_path));
        Assert.Equal(SqlPreviewMode.Delay, new SettingsService(_path).GetSnapshot().Preview.Mode);
    }

    [Fact]
    public void 預覽尺寸會寫回設定檔()
    {
        var service = new SettingsService(_path);
        service.Update(settings =>
        {
            settings.Preview.Width = 760;
            settings.Preview.Height = 500;
        });

        var reloaded = new SettingsService(_path).GetSnapshot().Preview;
        Assert.Equal(760, reloaded.Width);
        Assert.Equal(500, reloaded.Height);
    }

    /// <summary>
    /// 列舉必須寫成可讀的字串。少了 <c>DataContract</c> 標註會存成 0 與 1，
    /// 使用者手動編輯 settings.json 時無從判斷是什麼。
    /// </summary>
    [Fact]
    public void 清單引擎以字串保存並讀回()
    {
        var service = new SettingsService(_path);
        service.Update(settings => settings.Suggestions.Engine = CompletionEngine.Custom);

        Assert.Contains("\"engine\":\"custom\"", File.ReadAllText(_path));
        Assert.Equal(CompletionEngine.Custom, new SettingsService(_path).GetSnapshot().Suggestions.Engine);
    }

    /// <summary>
    /// 舊版設定檔沒有 engine 欄位，讀回時必須落在預設值而不是 0 對應的第一個成員以外的東西。
    /// </summary>
    [Fact]
    public void 舊版設定檔沿用預設引擎()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, "{\"enabled\":true,\"suggestions\":{\"enabled\":true}}");

        var snapshot = new SettingsService(_path).GetSnapshot();

        Assert.Equal(CompletionEngine.Native, snapshot.Suggestions.Engine);
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
    public void Update回傳套用後的快照並寫入檔案()
    {
        var service = new SettingsService(_path);

        var applied = service.Update(settings =>
        {
            settings.Suggestions.TriggerAfterCharacters = 3;
            settings.Suggestions.MaximumItems = 42;
            settings.AsyncCompletionProbe = true;
        });

        Assert.Equal(3, applied.Suggestions.TriggerAfterCharacters);
        Assert.Equal(42, applied.Suggestions.MaximumItems);
        Assert.True(applied.AsyncCompletionProbe);

        var reloaded = new SettingsService(_path).GetSnapshot();
        Assert.Equal(3, reloaded.Suggestions.TriggerAfterCharacters);
        Assert.Equal(42, reloaded.Suggestions.MaximumItems);
        Assert.True(reloaded.AsyncCompletionProbe);
    }

    [Fact]
    public void Update會先併入外部變更再套用()
    {
        var service = new SettingsService(_path);

        // 模擬使用者在對話框開著的時候直接改了設定檔。
        File.WriteAllText(_path, "{\"enabled\":false,\"diagnosticsEnabled\":true}");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(1));

        var applied = service.Update(settings => settings.Suggestions.ShowPreview = false);

        Assert.False(applied.Enabled);
        Assert.True(applied.DiagnosticsEnabled);
        Assert.False(applied.Suggestions.ShowPreview);
    }

    [Fact]
    public void Update傳入null時擲回()
    {
        var service = new SettingsService(_path);

        Assert.Throws<ArgumentNullException>(() => service.Update(null!));
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
