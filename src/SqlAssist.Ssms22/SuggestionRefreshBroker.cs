using System;

namespace SqlAssist.Ssms22;

internal static class SuggestionRefreshBroker
{
    public static event EventHandler? RefreshRequested;

    public static void RequestRefresh()
    {
        RefreshRequested?.Invoke(null, EventArgs.Empty);
    }
}
