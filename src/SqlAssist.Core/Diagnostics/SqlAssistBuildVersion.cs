using System;

namespace SqlAssist.Core.Diagnostics;

/// <summary>從組件的三種版本中整理出給人看與回報問題用的版本資訊。</summary>
public sealed class SqlAssistBuildVersion
{
    private SqlAssistBuildVersion(
        string displayVersion,
        string fullVersion,
        string fileVersion,
        string? commitId)
    {
        DisplayVersion = displayVersion;
        FullVersion = fullVersion;
        FileVersion = fileVersion;
        CommitId = commitId;
    }

    public static SqlAssistBuildVersion Unknown { get; } =
        new("未知", "未知", "未知", commitId: null);

    /// <summary>發布頁與使用者最需要看的版本，例如 <c>0.14.58</c>。</summary>
    public string DisplayVersion { get; }

    /// <summary>含原始碼識別碼的完整版本，例如 <c>0.14.58+bc1c42183c</c>。</summary>
    public string FullVersion { get; }

    /// <summary>Windows 檔案版本；第四段只用於回推來源。</summary>
    public string FileVersion { get; }

    public string? CommitId { get; }

    public string ShortCommitId => string.IsNullOrEmpty(CommitId)
        ? "未知"
        : CommitId!.Substring(0, Math.Min(7, CommitId.Length));

    /// <summary>
    /// 優先使用 InformationalVersion；AssemblyVersion 最後才用，因為它會刻意固定 patch。
    /// </summary>
    public static SqlAssistBuildVersion Create(
        string? informationalVersion,
        string? fileVersion,
        string? assemblyVersion)
    {
        var informational = Normalize(informationalVersion);
        var file = Normalize(fileVersion);
        var assembly = Normalize(assemblyVersion);
        var full = informational ?? file ?? assembly;

        if (full is null)
        {
            return Unknown;
        }

        var display = StripBuildMetadata(full);

        // NBGV 的 FileVersion 第四段由 commit id 推導；發布版號只到前三段。
        if (informational is null && Version.TryParse(display, out var parsed) && parsed.Build >= 0)
        {
            display = $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        }

        return new SqlAssistBuildVersion(
            display,
            full,
            file ?? "未知",
            ExtractCommitId(informational));
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    private static string StripBuildMetadata(string version)
    {
        var separator = version.IndexOf('+');
        return separator < 0 ? version : version.Substring(0, separator);
    }

    private static string? ExtractCommitId(string? informationalVersion)
    {
        if (informationalVersion is null)
        {
            return null;
        }

        var separator = informationalVersion.IndexOf('+');

        if (separator < 0 || separator == informationalVersion.Length - 1)
        {
            return null;
        }

        var metadata = informationalVersion.Substring(separator + 1);
        var nextItem = metadata.IndexOf('.');
        return nextItem < 0 ? metadata : metadata.Substring(0, nextItem);
    }
}
