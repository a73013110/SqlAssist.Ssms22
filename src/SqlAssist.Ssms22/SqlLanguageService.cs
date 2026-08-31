using System;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;

namespace SqlAssist.Ssms22;

/// <summary>SSMS SQL 語言服務識別碼的唯一出處。</summary>
internal static class SqlLanguageService
{
    private static readonly Guid Fallback = new("c4d96929-a9b0-42cc-b3e0-adac0435d7f2");
    private const string Collection = @"Languages\Language Services\SQL";
    private static readonly object Gate = new();

    private static IServiceProvider? _serviceProvider;
    private static Guid? _value;

    public static void Initialize(IServiceProvider serviceProvider)
    {
        lock (Gate)
        {
            if (_serviceProvider is null)
            {
                _serviceProvider = serviceProvider;
                // Resolve 可能在套件初始化前先用過備援值；服務到齊後要允許重新向殼層查一次。
                _value = null;
            }
        }
    }

    public static Guid Resolve()
    {
        lock (Gate)
        {
            return _value ??= Read();
        }
    }

    private static Guid Read()
    {
        if (_serviceProvider is not { } serviceProvider)
        {
            return Fallback;
        }

        var declared = SqlAssistPlatformGuard.Probe(
            "讀取 SQL 語言服務識別碼",
            () =>
            {
                var store = new ShellSettingsManager(serviceProvider)
                    .GetReadOnlySettingsStore(SettingsScope.Configuration);

                return store.CollectionExists(Collection)
                    ? store.GetString(Collection, string.Empty, string.Empty)
                    : string.Empty;
            },
            fallback: string.Empty);

        if (Guid.TryParse(declared, out var value))
        {
            return value;
        }

        SqlAssistDiagnostics.Write($"SQL 語言服務識別碼改用內建值：{Fallback}");
        return Fallback;
    }
}
