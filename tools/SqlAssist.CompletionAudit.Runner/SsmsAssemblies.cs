using System;
using System.IO;
using System.Reflection;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 從 SSMS 安裝目錄解析 SqlParser、SmoMetadataProvider 與它們的相依（Microsoft.Data.SqlClient 等）。
/// </summary>
/// <remarks>
/// 這些組件不複製進輸出：換版的 SSMS 會帶著自己那一份相依一起換，複製一份等於把建置機當下的版本凍住。
/// 找的順序與 SQL Memory 隔離層相同（IDE、PublicAssemblies、PrivateAssemblies）。必須在碰到任何
/// SSMS 型別之前註冊，所以呼叫端要把用到它們的程式放在另一個不內嵌的方法裡。
/// </remarks>
internal static class SsmsAssemblies
{
    private static string? _ide;

    /// <summary>SSMS 的 Common7\IDE 目錄。</summary>
    public static string Ide => _ide ?? throw new InvalidOperationException("尚未指定 SSMS 安裝目錄。");

    public static void Register(string ideDirectory)
    {
        if (!Directory.Exists(ideDirectory))
        {
            throw new DirectoryNotFoundException($"找不到 SSMS 的 IDE 目錄：{ideDirectory}");
        }

        _ide = ideDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) => Resolve(request.Name);
    }

    private static Assembly? Resolve(string requested)
    {
        var name = new AssemblyName(requested).Name;

        foreach (var folder in new[] { Ide, Path.Combine(Ide, "PublicAssemblies"), Path.Combine(Ide, "PrivateAssemblies") })
        {
            var file = Path.Combine(folder, name + ".dll");

            if (File.Exists(file))
            {
                return Assembly.LoadFrom(file);
            }
        }

        return null;
    }
}
