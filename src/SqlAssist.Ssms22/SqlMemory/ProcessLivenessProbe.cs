using System;
using System.ComponentModel;
using System.Diagnostics;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 租約擁有者是否還在執行的保守探測。
/// </summary>
/// <remarks>
/// 只有「這個 PID 不存在」與「PID 存在但啟動時間對不上（被重用了）」兩種情形才算不在。
/// 其餘一律回報還活著：查得到程序卻問不到啟動時間（實測 PID 0 的
/// <see cref="Process.StartTime"/> 就是存取被拒）並不代表 SSMS 已經結束，
/// 猜錯方向會刪掉使用者正在編輯的未存檔草稿。留下遺留租約只是晚一點回收。
/// </remarks>
internal static class ProcessLivenessProbe
{
    public static SqlMemoryLeaseOwner CurrentOwner()
    {
        using var current = Process.GetCurrentProcess();
        return new SqlMemoryLeaseOwner(Environment.MachineName, current.Id, current.StartTime);
    }

    public static bool IsOwnerRunning(SqlMemoryLeaseOwner owner)
    {
        if (owner is null) return true;

        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return SqlMemoryLeaseReaper.IsSameProcess(owner, process.StartTime);
        }
        // 沒有這個 PID，或程序在兩次呼叫之間結束了：這兩種才是真的不在。
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // 存取被拒、查詢失敗；不是「不在」的證據。
        catch (Win32Exception) { return true; }
        catch (NotSupportedException) { return true; }
    }
}
