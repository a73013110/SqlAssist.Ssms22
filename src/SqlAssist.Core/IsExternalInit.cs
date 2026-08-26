namespace System.Runtime.CompilerServices;

/// <summary>
/// <c>init</c> 存取子需要的標記型別。
/// </summary>
/// <remarks>
/// netstandard2.0 沒有提供它，編譯器又要求它必須存在，
/// 所以在這裡補一份。除了讓編譯器找得到之外沒有任何作用。
/// </remarks>
internal static class IsExternalInit
{
}
