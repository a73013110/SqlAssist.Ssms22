using System;
using System.Threading;
using System.Windows.Threading;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class ThemeRefreshQueueTests
{
    [Fact]
    public void BurstOfNotificationsOnlyRefreshesOnceOnUiThread()
    {
        WpfTest.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var count = 0;
            using var queue = new ThemeRefreshQueue(dispatcher, () =>
            {
                Assert.True(dispatcher.CheckAccess());
                count++;
            });
            var worker = new Thread(() =>
            {
                for (var index = 0; index < 100; index++)
                {
                    queue.Request();
                }
            });
            worker.Start();
            worker.Join();
            Assert.Equal(0, count);
            Drain(dispatcher);
            Assert.Equal(1, count);
            queue.Request();
            Drain(dispatcher);
            Assert.Equal(2, count);
        });
    }

    [Fact]
    public void ClosingBeforeDispatchCancelsRefreshAndFurtherNotifications()
    {
        WpfTest.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var count = 0;
            var queue = new ThemeRefreshQueue(dispatcher, () => count++);
            queue.Request();
            queue.Dispose();
            queue.Request();
            Drain(dispatcher);
            Assert.Equal(0, count);
        });
    }

    private static void Drain(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
