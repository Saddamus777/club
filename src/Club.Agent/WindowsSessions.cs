using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Club.Agent;

internal static class WindowsSessions
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo { public int Id; public IntPtr Station; public int State; }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);

    public static void LogOff(string visitorSid)
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count)) throw new Win32Exception();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(buffer + i * Marshal.SizeOf<SessionInfo>());
                if (session.Id == 0) continue;
                if (!WTSQueryUserToken((uint)session.Id, out var token))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 1008) continue; // No logged-in user (ERROR_NO_TOKEN).
                    throw new Win32Exception(error);
                }
                try
                {
                    using var user = new WindowsIdentity(token);
                    if (user.User?.Value == visitorSid && !WTSLogoffSession(IntPtr.Zero, session.Id, true)) throw new Win32Exception();
                }
                finally { CloseHandle(token); }
            }
        }
        finally { WTSFreeMemory(buffer); }
    }
}
