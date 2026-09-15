using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;

namespace SqlAssist.SqlMemory.Probe;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 4 && args[0] == "--external-load")
        {
            try
            {
                // 從 ApplicationBase 以外載入擴充，不能用 DLL 與 exe 同目錄的測試取代 SSMS 載入情境。
                var assembly = Assembly.LoadFrom(Path.Combine(args[1], "SqlAssist.SqlMemory.Isolation.dll"));
                var selfTest = assembly.GetType("SqlAssist.SqlMemory.Isolation.SqlMemoryStorageSelfTest", throwOnError: true);
                var run = selfTest.GetMethod("RunAsync") ?? throw new MissingMethodException("找不到自我測試入口。");
                var task = (Task)(run.Invoke(null, new object[] { args[3], args[2], CancellationToken.None })
                    ?? throw new InvalidOperationException("自我測試沒有傳回 Task。"));
                task.GetAwaiter().GetResult();
                Console.WriteLine(File.ReadAllText(Path.Combine(args[3], "report.txt")));
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
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
            SqlMemoryStorageSelfTest.RunAsync(directory, ssmsDirectory, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(File.ReadAllText(Path.Combine(directory, SqlMemoryStorageSelfTest.ReportFileName)));
            return 0;
        }
        using var store = IsolatedSqlMemoryStore.OpenAsync(database, ssmsDirectory, CancellationToken.None).GetAwaiter().GetResult();
        if (mode == "runtime")
        {
            Console.WriteLine(store.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult());
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name?.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) == true ||
                a.GetName().Name == "Microsoft.Data.Sqlite"))
                throw new InvalidOperationException("SQLite provider 洩漏到宿主 AppDomain。");
            Console.WriteLine("宿主 AppDomain 未載入 SQL Memory 的 provider。");
            return 0;
        }
        if (mode == "write")
        {
            var document = new SqlDocument(Guid.NewGuid(), "Library.sql", null);
            var session = new SqlSession(Guid.NewGuid(), document.DocumentId, DateTimeOffset.UtcNow);
            var policy = new SqlCapturePolicy(false, false, TimeSpan.FromMinutes(10), true, false);
            var committer = new SqlCaptureCommitter(store, new SqlCapturePlanner());
            for (var i = 1; i <= 20; i++)
                committer.ProcessAsync(new SqlCapture(Guid.NewGuid(), document, session, i, DateTimeOffset.UtcNow,
                    SqlCaptureKind.BeforeExecute, new SqlTextSnapshot("SELECT * FROM Lib_Reader;")), policy, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("已提交 20 次執行。");
            return 0;
        }
        if (mode == "verify")
        {
            var page = store.ReadHistoryAsync(new SqlHistoryRequest(100, SqlHistoryFilter.Executions), CancellationToken.None).GetAwaiter().GetResult();
            if (page.Items.Count != 40 || page.NextCursor != null || page.Items.Select(i => i.ContentId).Distinct().Count() != 1)
                throw new InvalidOperationException("跨程序提交數量或內容去重不正確。");
            Console.WriteLine("跨程序驗證通過：40 次執行、同一內容位址。");
            return 0;
        }
        throw new ArgumentException("未知的 Probe 模式。");
    }
}
