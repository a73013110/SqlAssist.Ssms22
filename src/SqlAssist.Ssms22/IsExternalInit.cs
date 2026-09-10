namespace System.Runtime.CompilerServices;

/// <summary>
/// <c>init</c> 存取子需要的標記型別。
/// </summary>
/// <remarks>
/// net48 沒有提供它，編譯器又要求它必須存在，所以在這裡補一份。除了讓編譯器找得到
/// 之外沒有任何作用。Core 那一份是 <c>internal</c>，跨組件看不到，兩邊各留一份。
/// </remarks>
internal static class IsExternalInit
{
}
