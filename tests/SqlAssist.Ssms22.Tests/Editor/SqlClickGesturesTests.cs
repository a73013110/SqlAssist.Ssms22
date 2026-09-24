using System.Windows.Input;
using SqlAssist.Ssms22.Editor;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Editor;

/// <summary>修飾鍵對應到哪一個點擊動作；必須完全相同才算。</summary>
public sealed class SqlClickGesturesTests
{
    [Fact]
    public void Ctrl是結構預覽()
    {
        Assert.Equal(SqlClickAction.ShowStructure, SqlClickGestures.Resolve(ModifierKeys.Control));
    }

    [Fact]
    public void Ctrl加Shift是移至定義()
    {
        Assert.Equal(
            SqlClickAction.GoToDefinition,
            SqlClickGestures.Resolve(ModifierKeys.Control | ModifierKeys.Shift));
    }

    /// <summary>Ctrl+Alt＋點擊是編輯器的多重游標；「包含 Ctrl 就算」會把它搶走。</summary>
    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Windows)]
    public void 其他組合不接手(ModifierKeys modifiers)
    {
        Assert.Equal(SqlClickAction.None, SqlClickGestures.Resolve(modifiers));
    }

    [Fact]
    public void 只有結構預覽答得出內建名稱()
    {
        Assert.True(SqlClickGestures.AcceptsBuiltIns(SqlClickAction.ShowStructure));
        Assert.False(SqlClickGestures.AcceptsBuiltIns(SqlClickAction.GoToDefinition));
    }
}
