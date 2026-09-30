using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 建議清單召回稽核。結束碼：0 跑完（有漏也是 0）、1 工具本身出錯、2 參數不對。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        AuditOptions options;

        try
        {
            options = AuditOptions.Parse(args);
            SsmsAssemblies.Register(options.Ide);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // 第一次 Ctrl+C 收尾：做完的段已經進快取，報告照寫；再按一次才硬停。
            if (!cancellation.IsCancellationRequested)
            {
                eventArgs.Cancel = true;
                Console.Error.WriteLine("收到停止要求，寫完報告後結束…");
                cancellation.Cancel();
            }
        };

        try
        {
            return Run(options, cancellation.Token);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    /// <summary>用到 SSMS 組件的程式從這裡開始，解析器已經註冊好。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(AuditOptions options, CancellationToken cancellationToken) =>
        new AuditRun(options).ExecuteAsync(cancellationToken).GetAwaiter().GetResult();
}
