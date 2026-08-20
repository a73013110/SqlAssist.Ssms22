using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Text.Editor;

namespace SqlAssist.Ssms22;

internal static class CompletionSessionRegistry
{
    private static readonly ConditionalWeakTable<ITextView, SqlCompletionController> Sessions = new();

    public static void Register(ITextView textView, SqlCompletionController controller)
    {
        Sessions.Remove(textView);
        Sessions.Add(textView, controller);
    }

    public static bool TryGet(ITextView textView, out SqlCompletionController? controller)
    {
        return Sessions.TryGetValue(textView, out controller);
    }

    public static void Remove(ITextView textView)
    {
        Sessions.Remove(textView);
    }
}
