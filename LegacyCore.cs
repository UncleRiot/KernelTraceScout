using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace KernelTraceScout
{
    public sealed class AppSettings
    {
        public int KernelThresholdPercent { get; set; }
        public int RefreshIntervalSeconds { get; set; }
        public double MinimumKernelCpuPercent { get; set; }
        public double MinimumKernelMilliseconds { get; set; }
        public double MinimumKernelUserRatio { get; set; }
        public int MinimumValidSamples { get; set; }
        public int MinimumObservationSeconds { get; set; }
        public int StrongSuspiciousSamples { get; set; }
        public bool DarkMode { get; set; }
        public int CaptureSeconds { get; set; }
        public int MaximumTraceMegabytes { get; set; }
        public AppSettings()
        {
            DarkMode = true; KernelThresholdPercent = 75; RefreshIntervalSeconds = 2;
            MinimumKernelCpuPercent = 5; MinimumKernelMilliseconds = 50; MinimumKernelUserRatio = 3;
            MinimumValidSamples = 3; MinimumObservationSeconds = 5; StrongSuspiciousSamples = 3;
            CaptureSeconds = 30; MaximumTraceMegabytes = 128;
        }
        internal void Validate()
        {
            if (RefreshIntervalSeconds < 1 || RefreshIntervalSeconds > 300 || KernelThresholdPercent < 1 || KernelThresholdPercent > 99 ||
                !Finite(MinimumKernelCpuPercent, 0.01, 100) || !Finite(MinimumKernelMilliseconds, 1, 60000) ||
                !Finite(MinimumKernelUserRatio, 0.1, 1000) || MinimumValidSamples < 1 || MinimumValidSamples > 100 ||
                MinimumObservationSeconds < 1 || MinimumObservationSeconds > 600 || StrongSuspiciousSamples < 2 || StrongSuspiciousSamples > 100 ||
                CaptureSeconds < 5 || CaptureSeconds > 120 || MaximumTraceMegabytes < 16 || MaximumTraceMegabytes > 256)
                throw new InvalidDataException("Configuration contains out-of-range diagnostic limits.");
        }
        private static bool Finite(double value, double min, double max) { return !double.IsNaN(value) && value >= min && value <= max; }
    }

    internal static class AppFiles
    {
        internal static readonly string DirectoryPath = AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string ConfigPath = Path.Combine(DirectoryPath, "KernelTraceScout.config.xml");
        internal static readonly string LogDirectory = Path.Combine(DirectoryPath, "Logs");
    }

    internal static class SettingsStore
    {
        internal static AppSettings Load()
        {
            if (!File.Exists(AppFiles.ConfigPath))
            {
                var defaults = new AppSettings();
                try { Save(defaults); } catch (IOException ex) { AppLog.Write("Default configuration could not be saved: " + ex.Message); } catch (UnauthorizedAccessException ex) { AppLog.Write("Default configuration could not be saved: " + ex.Message); }
                return defaults;
            }
            try
            {
                if (new FileInfo(AppFiles.ConfigPath).Length > 65536) throw new InvalidDataException("Configuration exceeds 64 KiB.");
                using (var reader = XmlReader.Create(AppFiles.ConfigPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                {
                    var settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(reader);
                    settings.Validate();
                    return settings;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Configuration read failed: " + ex);
                throw new InvalidOperationException("Cannot read configuration: " + AppFiles.ConfigPath, ex);
            }
        }

        internal static void Save(AppSettings settings)
        {
            settings.Validate();
            string temporary = AppFiles.ConfigPath + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    new XmlSerializer(typeof(AppSettings)).Serialize(file, settings);
                if (File.Exists(AppFiles.ConfigPath)) File.Replace(temporary, AppFiles.ConfigPath, null);
                else File.Move(temporary, AppFiles.ConfigPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static class AppLog
    {
        private static readonly object Sync = new object();
        private static string logPath;

        internal static void Initialize()
        {
            try { Directory.CreateDirectory(AppFiles.LogDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { PruneLogs(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            logPath = Path.Combine(AppFiles.LogDirectory, "KernelTraceScout_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        }

        internal static void Write(string message)
        {
            lock (Sync)
            {
                try
                {
                    if (logPath == null) Initialize();
                    PruneLogs();
                    if (File.Exists(logPath) && new FileInfo(logPath).Length >= 2 * 1024 * 1024)
                    {
                        string old = logPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(logPath, old);
                    }
                    if (message.Length > 16384) message = message.Substring(0, 16384);
                    File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { /* Logging must not stop shutdown or diagnosis. */ }
                catch (UnauthorizedAccessException) { /* Portable folder may be read-only. */ }
            }
        }
        // SEMANTIC ANCHOR: Retention touches only our named, closed log/capture files.
        // At most four ETLs and seven days of logs; deletion failure prevents a new capture.
        private static void PruneLogs()
        {
            if (!Directory.Exists(AppFiles.LogDirectory)) return;
            foreach (var f in new DirectoryInfo(AppFiles.LogDirectory).GetFiles("KernelTraceScout_*.log*"))
                if ((f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".log.old", StringComparison.OrdinalIgnoreCase)) && f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) f.Delete();
        }
        internal static void Prune()
        {
            if (!Directory.Exists(AppFiles.LogDirectory)) return;
            var files = new DirectoryInfo(AppFiles.LogDirectory).GetFiles("KernelTraceScout_*");
            var captures = files.Where(f => f.Extension.Equals(".etl", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in captures.Skip(3))
            {
                f.Delete(); string report = Path.ChangeExtension(f.FullName, ".stack-report.txt");
                if (File.Exists(report)) File.Delete(report);
            }
            foreach (var f in files.Where(f => (f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".log.old", StringComparison.OrdinalIgnoreCase)) && f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))) f.Delete();
        }

    }

    internal sealed class ProcessRow
    {
        internal string Name;
        internal int Pid;
        internal double Cpu, Kernel, User, Share, Ratio, Elapsed, KernelCores, KernelMilliseconds;
        internal DateTime Start;
        internal bool Valid;
        internal string Status;
    }

    internal static class NativeFilters
    {
        [DllImport("fltlib.dll", ExactSpelling = true)]
        private static extern int FilterFindFirst(int informationClass, IntPtr buffer, uint bufferSize, out uint bytesReturned, out IntPtr handle);
        [DllImport("fltlib.dll", ExactSpelling = true)]
        private static extern int FilterFindNext(IntPtr handle, int informationClass, IntPtr buffer, uint bufferSize, out uint bytesReturned);
        [DllImport("fltlib.dll", ExactSpelling = true)]
        private static extern int FilterFindClose(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct FilterFullInformation
        {
            internal uint NextEntryOffset, FrameId, NumberOfInstances;
            internal ushort FilterNameLength, FilterNameBuffer;
        }

        internal sealed class Filter
        {
            internal readonly string Name;
            internal readonly uint Instances;
            internal readonly uint Frame;
            internal Filter(string name, uint instances, uint frame) { Name = name; Instances = instances; Frame = frame; }
        }

        internal static List<Filter> Enumerate()
        {
            const int bufferSize = 65536;
            var result = new List<Filter>();
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            IntPtr handle = IntPtr.Zero;
            try
            {
                // SEMANTIC ANCHOR: FILTER_FULL_INFORMATION requires FilterFullInformation (0).
                // The buffer parser below is defined exclusively for this information class.
                const int filterFullInformation = 0;
                int hr = FilterFindFirst(filterFullInformation, buffer, bufferSize, out uint returned, out handle);
                if (hr == unchecked((int)0x80070103)) return result;
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                while (true)
                {
                    if (returned < 16 || returned > bufferSize) throw new InvalidDataException("Invalid filter record length.");
                    int offset = 0;
                    while (true)
                    {
                        if (offset > returned - 16) throw new InvalidDataException("Truncated minifilter record.");
                        IntPtr entry = IntPtr.Add(buffer, offset);
                        var header = (FilterFullInformation)Marshal.PtrToStructure(entry, typeof(FilterFullInformation));
                        int nameOffset = Marshal.OffsetOf(typeof(FilterFullInformation), "FilterNameBuffer").ToInt32();
                        int remaining = (int)returned - offset;
                        int length = header.NextEntryOffset == 0 ? remaining : checked((int)header.NextEntryOffset);
                        if (length < 16 || length > remaining || header.FilterNameLength % 2 != 0 || header.FilterNameLength > length - nameOffset)
                            throw new InvalidDataException("Invalid minifilter name or entry length.");
                        string name = Marshal.PtrToStringUni(IntPtr.Add(entry, nameOffset), header.FilterNameLength / 2) ?? "";
                        result.Add(new Filter(name, header.NumberOfInstances, header.FrameId));
                        if (result.Count >= 4096) throw new InvalidDataException("Minifilter enumeration limit exceeded.");
                        if (header.NextEntryOffset == 0) break;
                        if (header.NextEntryOffset % 8 != 0) throw new InvalidDataException("Unaligned minifilter record.");
                        offset += length;
                    }
                    hr = FilterFindNext(handle, filterFullInformation, buffer, bufferSize, out returned);
                    if (hr == unchecked((int)0x80070103)) break;
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) FilterFindClose(handle);
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }
    }


}
