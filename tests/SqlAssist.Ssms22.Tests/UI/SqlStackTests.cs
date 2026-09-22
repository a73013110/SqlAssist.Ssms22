using System.Windows;
using System.Windows.Controls;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public class SqlStackTests
{
    private const double Gap = 8;

    [Fact]
    public void 間距只加在看得見的兩塊之間()
    {
        WpfTest.Run(() =>
        {
            var stack = new SqlStack(Gap);
            var first = Block(20);
            var middle = Block(30);
            var last = Block(40);
            stack.Children.Add(first);
            stack.Children.Add(middle);
            stack.Children.Add(last);

            Measure(stack);
            Assert.Equal(20 + 30 + 40 + (Gap * 2), stack.DesiredSize.Height);

            // 收起的那一塊連同它前面那一段間距一起讓開；各出一半 margin 的那一版會留下半格空白。
            middle.Visibility = Visibility.Collapsed;
            Measure(stack);
            Assert.Equal(20 + 40 + Gap, stack.DesiredSize.Height);

            // 只剩一塊時一段間距都不該有，否則工具列整組收起後清單會往下掉一格。
            first.Visibility = Visibility.Collapsed;
            Measure(stack);
            Assert.Equal(40, stack.DesiredSize.Height);
        });
    }

    [Fact]
    public void 收起的那一塊不佔位置也不推開後面那一塊()
    {
        WpfTest.Run(() =>
        {
            var stack = new SqlStack(Gap);
            var first = Block(20);
            var middle = Block(30);
            var last = Block(40);
            stack.Children.Add(first);
            stack.Children.Add(middle);
            stack.Children.Add(last);

            middle.Visibility = Visibility.Collapsed;
            Measure(stack);
            stack.Arrange(new Rect(0, 0, 200, stack.DesiredSize.Height));
            stack.UpdateLayout();

            Assert.Equal(0, first.TranslatePoint(default, stack).Y);
            Assert.Equal(20 + Gap, last.TranslatePoint(default, stack).Y);
        });
    }

    private static Border Block(double height) => new() { Height = height, Width = 100 };

    private static void Measure(SqlStack stack) =>
        stack.Measure(new Size(200, double.PositiveInfinity));
}
