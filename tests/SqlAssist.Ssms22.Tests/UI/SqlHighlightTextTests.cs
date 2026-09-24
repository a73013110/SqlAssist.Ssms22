using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using SqlAssist.Core.Matching;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlHighlightTextTests
{
    [Fact]
    public void 容器上的比對器往下繼承且自己的區段優先()
    {
        WpfTest.Run(() =>
        {
            var free = new SqlHighlightText { SourceText = "借閱日期 DueDate" };
            var own = new SqlHighlightText { SourceText = "Due", Spans = new[] { new MatchSpan(0, 1) } };
            var host = new StackPanel();
            host.Children.Add(free);
            host.Children.Add(own);

            SqlHighlightText.SetMatcher(host, new TextMatcher("due", TextMatchOptions.None));
            Assert.Equal(new[] { "Due" }, Hits(free));
            Assert.Equal(new[] { "D" }, Hits(own));

            // 清掉比對器就整行還原，不留上一輪的底色。
            SqlHighlightText.SetMatcher(host, null);
            Assert.Empty(Hits(free));
            Assert.Equal("借閱日期 DueDate", string.Concat(free.Inlines.OfType<Run>().Select(run => run.Text)));
        });
    }

    private static string[] Hits(SqlHighlightText text) =>
        text.Inlines.OfType<Run>().Where(run => run.ReadLocalValue(TextElement.BackgroundProperty) != System.Windows.DependencyProperty.UnsetValue)
            .Select(run => run.Text).ToArray();
}
