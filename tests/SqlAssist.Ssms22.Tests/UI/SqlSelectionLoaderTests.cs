using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

/// <summary>
/// 兩個 Preview 共用的選取時序：去彈跳、取消上一輪，以及「回來的還是不是目前這一列」。
/// </summary>
/// <remarks>
/// 這一段原本在兩個 Preview 裡各寫一次，而它的錯誤只在特定時序下才看得見——方向鍵連按、
/// 重新選同一列、面板關掉時還在飛的那一輪。所以斷言的是時序本身，不是畫面。
/// </remarks>
public sealed class SqlSelectionLoaderTests
{
    private const int Delay = 40;

    [Fact]
    public void 連續換列只讀最後一列()
    {
        WpfTest.Run(() =>
        {
            var read = new List<string>();
            using var loader = Loader(read);

            foreach (var row in new[] { "a", "b", "c" })
            {
                loader.Select(row);
                loader.Load();
            }

            Pump(Delay * 4);

            // 方向鍵捲過去的那幾列不該各發一輪；使用者只看了最後一列。
            Assert.Equal(new[] { "c" }, read);
        });
    }

    [Fact]
    public void 換列會取消上一輪的權杖()
    {
        WpfTest.Run(() =>
        {
            using var loader = Loader(new List<string>());
            loader.Select("a");
            var first = loader.Token;

            loader.Select("b");

            Assert.True(first.IsCancellationRequested);
            Assert.False(loader.Token.IsCancellationRequested);
        });
    }

    [Fact]
    public void 重新選同一列之後舊那一輪不再算是目前這一列()
    {
        WpfTest.Run(() =>
        {
            using var loader = Loader(new List<string>());
            const string row = "a";
            loader.Select(row);
            var stale = loader.Token;

            // 只比對列參考的那一版在這裡會判成「還是目前這一列」，於是上一輪的收尾把新那一輪
            // 剛點亮的載入狀態關掉，畫面上是一塊不再轉的空白。
            loader.Select(row);

            Assert.False(loader.IsCurrent(row, stale));
            Assert.True(loader.IsCurrent(row, loader.Token));
        });
    }

    [Fact]
    public void 選到空的就不排這一輪()
    {
        WpfTest.Run(() =>
        {
            var read = new List<string>();
            using var loader = Loader(read);

            loader.Select(null);
            loader.Load();
            Pump(Delay * 3);

            Assert.Empty(read);
            Assert.Null(loader.Current);
        });
    }

    [Fact]
    public void 釋放之後不再回呼也不再有目前這一列()
    {
        WpfTest.Run(() =>
        {
            var read = new List<string>();
            var loader = Loader(read);
            loader.Select("a");
            loader.Load();

            loader.Dispose();
            Pump(Delay * 3);

            Assert.Empty(read);
            Assert.Null(loader.Current);
            // 面板關掉的那一刻還在飛的工作本來就該當成取消，不是去碰已經釋放的來源。
            Assert.True(loader.Token.IsCancellationRequested);
            Assert.False(loader.IsCurrent("a", CancellationToken.None));
        });
    }

    [Fact]
    public void 重複釋放不擲出()
    {
        WpfTest.Run(() =>
        {
            var loader = Loader(new List<string>());
            loader.Dispose();
            loader.Dispose();
        });
    }

    private static SqlSelectionLoader<string> Loader(ICollection<string> read) =>
        new(Dispatcher.CurrentDispatcher, TimeSpan.FromMilliseconds(Delay), (row, _) => read.Add(row));

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
