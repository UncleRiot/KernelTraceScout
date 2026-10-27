using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace KernelTraceScout
{
    // SEMANTIC ANCHOR: A second instance of this EXE watches only a random session GUID.
    // It survives GUI termination, but never terminates a process. Query AND GUID comparison
    // precede named session stop. No takeover of NT Kernel Logger or other applications.
    internal static class CaptureWatchdog
    {
        internal static void Start(Guid guid, ulong sessionHandle, int seconds)
        {
            using (var parent = Process.GetCurrentProcess())
            using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = "--watch-etw " + guid.ToString("D") + " " + parent.Id.ToString(CultureInfo.InvariantCulture) + " " + parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + " " + seconds.ToString(CultureInfo.InvariantCulture) + " " + sessionHandle.ToString(CultureInfo.InvariantCulture),
                WorkingDirectory = AppFiles.DirectoryPath
            }))
            {
                if (child == null) throw new InvalidOperationException("Could not start the own-session cleanup watchdog.");
            }
        }
        internal static bool Run(string[] args)
        {
            if (args.Length == 0 || args[0] != "--watch-etw") return false;
            try
            {
                Guid guid; int pid, seconds; long ticks; ulong sessionHandle;
                if (args.Length != 6 || !Guid.TryParse(args[1], out guid) ||
                    !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out pid) ||
                    !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) ||
                    !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || !ulong.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out sessionHandle) || sessionHandle == 0 || seconds < 5 || seconds > 120) return true;
                EtwNative.ValidateLayout();
                Process parent = null;
                try { parent = Process.GetProcessById(pid); if (parent.StartTime.ToUniversalTime().Ticks != ticks) { parent.Dispose(); parent = null; } }
                catch (ArgumentException) { }
                using (parent)
                {
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed.TotalSeconds < seconds + 2)
                    {
                        if (!SessionExists(guid)) return true;
                        if (parent == null || parent.HasExited) break;
                        Thread.Sleep(500);
                    }
                    StopOwn(guid, sessionHandle);
                }
            }
            catch (Exception ex) { AppLog.Write("Own-session watchdog failed: " + ex); }
            return true;
        }
        private static IntPtr Properties()
        {
            int bytes = Marshal.SizeOf(typeof(EtwNative.Properties)) + 12288;
            IntPtr memory = Marshal.AllocHGlobal(bytes);
            Marshal.Copy(new byte[bytes], 0, memory, bytes);
            var properties = new EtwNative.Properties(); properties.Wnode.BufferSize = (uint)bytes;
            properties.Wnode.Flags = 0x20000;
            properties.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EtwNative.Properties));
            properties.LogFileNameOffset = properties.LoggerNameOffset + 2048;
            Marshal.StructureToPtr(properties, memory, false);
            return memory;
        }
        private static bool SessionExists(Guid guid)
        {
            IntPtr memory = Properties();
            try
            {
                uint status = EtwNative.ControlTrace(0, "KernelTraceScout CPU Stack Capture", memory, 0);
                if (status == 4201) return false;
                if (status != 0) throw new Win32Exception((int)status, "Watchdog session query failed.");
                var properties = (EtwNative.Properties)Marshal.PtrToStructure(memory, typeof(EtwNative.Properties));
                return properties.Wnode.Guid == guid;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        private static void StopOwn(Guid guid, ulong sessionHandle)
        {
            if (!SessionExists(guid)) return;
            IntPtr memory = Properties();
            try
            {
                uint status = EtwNative.ControlTrace(sessionHandle, null, memory, 1);
                if (status != 0 && status != 4201 && status != 234) throw new Win32Exception((int)status, "Watchdog could not stop own session.");
                AppLog.Write("Watchdog finalized own ETW session: " + guid);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
}
