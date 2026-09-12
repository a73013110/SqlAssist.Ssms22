using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using SqlAssist.Core.QueryMemory;
using SqlAssist.QueryMemory.Hosting;

namespace SqlAssist.QueryMemory.Probe;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length != 3) { Console.Error.WriteLine("用法：Probe <SSMS IDE 目錄> <隔離資料庫> runtime|write|verify|self-test"); return 2; }
        try { return Run(args[0], args[1], args[2]); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string ssmsDirectory, string database, string mode)
    {
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("Probe 必須使用 x64。");
        if (mode == "self-test")
        {
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database)), "self-test");
            QueryMemoryStorageSelfTest.RunAsync(directory, ssmsDirectory, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(File.ReadAllText(Path.Combine(directory, QueryMemoryStorageSelfTest.ReportFileName)));
            return 0;
        }
        using var repository = IsolatedQueryMemoryRepository.OpenAsync(database, ssmsDirectory, CancellationToken.None).GetAwaiter().GetResult();
        if (mode == "runtime")
        {
            Console.WriteLine(repository.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult());
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name?.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) == true ||
                a.GetName().Name == "Microsoft.Data.Sqlite"))
                throw new InvalidOperationException("SQLite provider 洩漏到宿主 AppDomain。");
            Console.WriteLine("宿主 AppDomain 未載入 Query Memory 的 provider。");
            return 0;
        }
        if (mode == "write")
        {
            var document = new QueryDocument(Guid.NewGuid(), "Library.sql", null);
            var session = new QuerySession(Guid.NewGuid(), document.DocumentId, DateTimeOffset.UtcNow);
            var policy = new QueryMemoryPolicy(false, false, TimeSpan.FromMinutes(10), true, false);
            var processor = new QueryMemoryProcessor(repository, new QueryRevisionEngine());
            for (var i = 1; i <= 20; i++)
                processor.ProcessAsync(new QueryMemoryCapture(Guid.NewGuid(), document, session, i, DateTimeOffset.UtcNow,
                    QueryCaptureKind.BeforeExecute, new QueryTextSnapshot("SELECT * FROM Lib_Reader;")), policy, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("已提交 20 次執行。");
            return 0;
        }
        if (mode == "verify")
        {
            var page = repository.ReadHistoryAsync(new QueryHistoryRequest(100, QueryHistoryKind.Executed), CancellationToken.None).GetAwaiter().GetResult();
            if (page.Items.Count != 40 || page.NextCursor != null || page.Items.Select(i => i.ContentId).Distinct().Count() != 1)
                throw new InvalidOperationException("跨程序提交數量或內容去重不正確。");
            Console.WriteLine("跨程序驗證通過：40 次執行、同一內容位址。");
            return 0;
        }
        throw new ArgumentException("未知的 Probe 模式。");
    }
}
