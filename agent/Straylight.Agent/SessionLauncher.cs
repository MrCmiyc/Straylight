using System.Runtime.InteropServices;
using System.Text;

namespace Straylight.Agent;

/// <summary>
/// Launches a process into the active interactive console session from the LocalSystem
/// service (Session 0). The documented chain: WTSGetActiveConsoleSessionId ->
/// WTSQueryUserToken -> DuplicateTokenEx -> CreateProcessAsUser on winsta0\default.
/// Used to run session-bound work (DDC brightness) that the service itself can't do.
/// </summary>
public static class SessionLauncher
{
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const int SecurityImpersonation = 2, TokenPrimary = 1;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_NO_WINDOW = 0x08000000;

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int tokType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string? app, StringBuilder cmd, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }

    /// <summary>Run exePath with args in the active console session. Returns true if launched.</summary>
    public static bool Run(string exePath, string args)
    {
        uint sid = WTSGetActiveConsoleSessionId();
        if (sid == 0xFFFFFFFF) return false;
        if (!WTSQueryUserToken(sid, out IntPtr userTok)) return false;
        IntPtr primTok = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primTok))
                return false;
            CreateEnvironmentBlock(out env, primTok, false);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{exePath}\" {args}");
            bool ok = CreateProcessAsUser(primTok, exePath, cmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, null, ref si, out var pi);
            if (ok) { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
            return ok;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primTok != IntPtr.Zero) CloseHandle(primTok);
            CloseHandle(userTok);
        }
    }
}
