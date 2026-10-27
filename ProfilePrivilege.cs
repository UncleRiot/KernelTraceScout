using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KernelTraceScout
{
    // SEMANTIC ANCHOR: Enable only the current token's existing SeSystemProfilePrivilege.
    // Restore the exact previous attributes; never edit account rights or system policy.
    internal sealed class ProfilePrivilege : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { internal uint Count; internal Luid Luid; internal uint Attributes; }
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, out TokenPrivileges previous, out uint returned);
        private IntPtr token;
        private TokenPrivileges previous;
        internal ProfilePrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Luid luid;
                if (!LookupPrivilegeValue(null, "SeSystemProfilePrivilege", out luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                uint returned;
                bool ok = AdjustTokenPrivileges(token, false, ref state, (uint)Marshal.SizeOf(typeof(TokenPrivileges)), out previous, out returned);
                int error = Marshal.GetLastWin32Error();
                if (!ok || error != 0) throw new Win32Exception(error, "SeSystemProfilePrivilege is unavailable.");
            }
            catch { CloseHandle(token); token = IntPtr.Zero; throw; }
        }
        public void Dispose()
        {
            if (token == IntPtr.Zero) return;
            try
            {
                if (previous.Count != 0)
                {
                    TokenPrivileges ignored; uint returned;
                    bool ok = AdjustTokenPrivileges(token, false, ref previous, (uint)Marshal.SizeOf(typeof(TokenPrivileges)), out ignored, out returned);
                    int error = Marshal.GetLastWin32Error();
                    if (!ok || error != 0) AppLog.Write("Could not restore own profiling privilege: Win32 " + error);
                }
            }
            finally { CloseHandle(token); token = IntPtr.Zero; }
        }
    }
}
