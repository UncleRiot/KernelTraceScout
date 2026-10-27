using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KernelTraceScout
{
    // SEMANTIC ANCHOR: evntrace.h / evntcons.h x64 ABI, default pack 8.
    // Union fields have equal-sized blittable representations. No native record offsets.
    internal static class EtwNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Wnode
        {
            internal uint BufferSize, ProviderId;
            internal ulong HistoricalContext;
            internal long TimeStamp;
            internal Guid Guid;
            internal uint ClientContext, Flags;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Properties
        {
            internal Wnode Wnode;
            internal uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            internal int AgeLimit;
            internal uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            internal IntPtr LoggerThreadId;
            internal uint LogFileNameOffset, LoggerNameOffset;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ClassicId
        {
            internal Guid Guid;
            internal byte Type, R0, R1, R2, R3, R4, R5, R6;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Descriptor
        {
            internal ushort Id;
            internal byte Version, Channel, Level, Opcode;
            internal ushort Task;
            internal ulong Keyword;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Header
        {
            internal ushort Size, HeaderType, Flags, EventProperty;
            internal uint ThreadId, ProcessId;
            internal long TimeStamp;
            internal Guid ProviderId;
            internal Descriptor Descriptor;
            internal ulong ProcessorTime;
            internal Guid ActivityId;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Record
        {
            internal Header Header;
            internal uint BufferContext;
            internal ushort ExtendedDataCount, UserDataLength;
            internal IntPtr ExtendedData, UserData, UserContext;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct LegacyHeader
        {
            internal ushort Size, FieldTypeFlags;
            internal uint Version, ThreadId, ProcessId;
            internal long TimeStamp;
            internal Guid Guid;
            internal ulong ProcessorTime;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct LegacyEvent
        {
            internal LegacyHeader Header;
            internal uint InstanceId, ParentInstanceId;
            internal Guid ParentGuid;
            internal IntPtr MofData;
            internal uint MofLength, ClientContext;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct SystemTime
        {
            internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TimeZone
        {
            internal int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string StandardName;
            internal SystemTime StandardDate;
            internal int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DaylightName;
            internal SystemTime DaylightDate;
            internal int DaylightBias;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct LogHeader
        {
            internal uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
            internal long EndTime;
            internal uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
            internal uint StartBuffers, PointerSize, EventsLost, CpuSpeed;
            internal IntPtr LoggerName, LogFileName;
            internal TimeZone TimeZone;
            internal long BootTime, PerfFreq, StartTime;
            internal uint ReservedFlags, BuffersLost;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct LogFile
        {
            internal IntPtr LogFileName, LoggerName;
            internal long CurrentTime;
            internal uint BuffersRead, ProcessTraceMode;
            internal LegacyEvent CurrentEvent;
            internal LogHeader Header;
            internal IntPtr BufferCallback;
            internal uint BufferSize, Filled, EventsLost;
            internal IntPtr EventRecordCallback;
            internal uint IsKernelTrace;
            internal IntPtr Context;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PropertyDescriptor
        {
            internal ulong PropertyName;
            internal uint ArrayIndex, Reserved;
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void EventCallback(IntPtr record);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint BufferCallback(IntPtr logfile);
        [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
        [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint code);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern uint TraceSetInformation(ulong handle, int informationClass, IntPtr data, uint length);
        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", ExactSpelling = true, SetLastError = true)]
        internal static extern ulong OpenTrace(ref LogFile logfile);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll", ExactSpelling = true)] internal static extern uint CloseTrace(ulong handle);
        [DllImport("tdh.dll", ExactSpelling = true)]
        internal static extern uint TdhGetPropertySize(IntPtr record, uint contexts, IntPtr context, uint count, ref PropertyDescriptor descriptor, out uint size);
        [DllImport("tdh.dll", ExactSpelling = true)]
        internal static extern uint TdhGetProperty(IntPtr record, uint contexts, IntPtr context, uint count, ref PropertyDescriptor descriptor, uint size, [Out] byte[] value);

        internal static void ValidateLayout()
        {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Use the Windows x64 build.");
            Check<Wnode>(48); Check<Properties>(120); Check<ClassicId>(24); Check<Descriptor>(16);
            Check<Header>(80); Check<Record>(112); Check<LegacyHeader>(48); Check<LegacyEvent>(88);
            Check<TimeZone>(172); Check<LogHeader>(280); Check<LogFile>(448); Check<PropertyDescriptor>(16);
            Offset<Record>("UserData", 96); Offset<LogFile>("ProcessTraceMode", 28);
            Offset<LogFile>("Header", 120); Offset<LogFile>("EventRecordCallback", 424);
        }
        private static void Check<T>(int size) { if (Marshal.SizeOf(typeof(T)) != size) throw new InvalidOperationException(typeof(T).Name + " ABI size mismatch."); }
        private static void Offset<T>(string field, int value) { if (Marshal.OffsetOf(typeof(T), field).ToInt32() != value) throw new InvalidOperationException(typeof(T).Name + "." + field + " ABI offset mismatch."); }
        internal static byte[] Property(IntPtr record, string name)
        {
            IntPtr text = Marshal.StringToHGlobalUni(name);
            try
            {
                var descriptor = new PropertyDescriptor { PropertyName = unchecked((ulong)text.ToInt64()), ArrayIndex = uint.MaxValue };
                uint size;
                uint status = TdhGetPropertySize(record, 0, IntPtr.Zero, 1, ref descriptor, out size);
                if (status != 0) throw new Win32Exception((int)status, "TDH cannot decode property " + name + ".");
                if (size == 0 || size > 8192) throw new InvalidDataException("Invalid TDH property size.");
                byte[] value = new byte[size];
                status = TdhGetProperty(record, 0, IntPtr.Zero, 1, ref descriptor, size, value);
                if (status != 0) throw new Win32Exception((int)status, "TDH property decode failed.");
                return value;
            }
            finally { Marshal.FreeHGlobal(text); }
        }
        internal static ulong Number(byte[] bytes)
        {
            if (bytes.Length == 4) return BitConverter.ToUInt32(bytes, 0);
            if (bytes.Length == 8) return BitConverter.ToUInt64(bytes, 0);
            throw new InvalidDataException("Unexpected numeric property width.");
        }
    }

    // SEMANTIC ANCHOR: Only the handle returned by a successful StartTrace belongs to us.
    // Hard limits: <=120 s, <=256 MiB ETL, <=32 MiB requested native buffers.
    // No sampling-rate changes, registry changes, or foreign-session takeover.
    internal sealed class NativeEtwCapture : IDisposable
    {
        private readonly object sync = new object();
        private ulong handle;
        private volatile bool running;
        private string path, sessionName;
        private Guid sessionGuid;
        private Timer deadline;
        internal event Action<string, Exception> Completed;
        internal bool Running { get { return running; } }
        internal uint EventsLost { get; private set; }

        private IntPtr CreateProperties(string fileName, int maxMb)
        {
            int size = Marshal.SizeOf(typeof(EtwNative.Properties));
            int total = size + 2048 + 8192;
            IntPtr memory = Marshal.AllocHGlobal(total);
            try
            {
                Marshal.Copy(new byte[total], 0, memory, total);
                var p = new EtwNative.Properties();
                p.Wnode.BufferSize = (uint)total; p.Wnode.Flags = 0x20000;
                p.Wnode.ClientContext = 1; p.Wnode.Guid = sessionGuid;
                p.BufferSize = 64;
                p.MinimumBuffers = p.MaximumBuffers = (uint)Math.Max(8, (int)ProcessMonitor.LogicalProcessors * 2);
                p.MaximumFileSize = (uint)maxMb;
                p.LogFileMode = 0x02000001; p.FlushTimer = 1;
                p.LoggerNameOffset = (uint)size; p.LogFileNameOffset = (uint)(size + 2048);
                Marshal.StructureToPtr(p, memory, false);
                char[] name = (sessionName + "\0").ToCharArray();
                char[] file = (fileName + "\0").ToCharArray();
                if (name.Length > 1024 || file.Length > 4096) throw new PathTooLongException();
                Marshal.Copy(name, 0, IntPtr.Add(memory, size), name.Length);
                Marshal.Copy(file, 0, IntPtr.Add(memory, size + 2048), file.Length);
                return memory;
            }
            catch { Marshal.FreeHGlobal(memory); throw; }
        }

        internal void Start(string fileName, int seconds, int maxMb)
        {
            EtwNative.ValidateLayout();
            if (Environment.OSVersion.Version < new Version(6, 2)) throw new PlatformNotSupportedException("Independent system logger capture requires Windows 8 / Server 2012 or later.");
            if (ProcessMonitor.LogicalProcessors > 256) throw new NotSupportedException("Capture is limited to 256 logical processors to bound native buffers.");
            if (seconds < 5 || seconds > 120 || maxMb < 16 || maxMb > 256) throw new ArgumentOutOfRangeException();
            lock (sync)
            {
                if (handle != 0) throw new InvalidOperationException("A capture is already running.");
                Directory.CreateDirectory(AppFiles.LogDirectory);
                AppLog.Prune();
                var drive = new DriveInfo(Path.GetPathRoot(fileName));
                if (drive.AvailableFreeSpace < (maxMb + 128L) * 1024 * 1024) throw new IOException("Insufficient free disk space for bounded capture.");
                sessionGuid = Guid.NewGuid();
                sessionName = "KernelTraceScout CPU Stack Capture";
                path = fileName;
                using (var privilege = new ProfilePrivilege())
                {
                IntPtr memory = CreateProperties(fileName, maxMb);
                bool started = false;
                try
                {
                    ulong created;
                    uint status = EtwNative.StartTrace(out created, sessionName, memory);
                    if (status != 0) throw new Win32Exception((int)status, "StartTrace failed. Existing sessions are never replaced.");
                    handle = created; running = true; EventsLost = 0; started = true;
                    CaptureWatchdog.Start(sessionGuid, handle, seconds);
                    // Configure stacks before enabling CPU profile events (Microsoft system logger sample).
                    var id = new EtwNative.ClassicId { Guid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc"), Type = 46 };
                    IntPtr data = Marshal.AllocHGlobal(32);
                    try
                    {
                        Marshal.StructureToPtr(id, data, false);
                        status = EtwNative.TraceSetInformation(handle, 3, data, (uint)Marshal.SizeOf(typeof(EtwNative.ClassicId)));
                        if (status != 0) throw new Win32Exception((int)status, "CPU stack tracing is unavailable.");
                        Marshal.Copy(new byte[32], 0, data, 32);
                        Marshal.WriteInt32(data, 0x01000067); // Profile | Process | Thread | Image | DPC | Interrupt
                        status = EtwNative.TraceSetInformation(handle, 4, data, 32);
                        if (status != 0) throw new Win32Exception((int)status, "System CPU event enable failed.");
                    }
                    finally { Marshal.FreeHGlobal(data); }
                    deadline = new Timer(_ => Complete(), null, seconds * 1000, Timeout.Infinite);
                    AppLog.Write("Own ETW session started: " + sessionName + "; " + seconds + " seconds; " + maxMb + " MiB maximum.");
                }
                catch
                {
                    if (started)
                    {
                        uint stopped = EtwNative.ControlTrace(handle, null, memory, 1);
                        if (stopped == 0 || stopped == 4201 || stopped == 234) { handle = 0; running = false; }
                        else
                        {
                            AppLog.Write("Own ETW rollback failed: " + stopped + "; session " + sessionName);
                            deadline = new Timer(_ => Complete(), null, 1000, Timeout.Infinite);
                        }
                    }
                    throw;
                }
                finally { Marshal.FreeHGlobal(memory); }
                }
            }
        }
        internal string Stop()
        {
            lock (sync)
            {
                if (handle == 0) return path;
                IntPtr memory = CreateProperties(path, 256);
                try
                {
                    uint status = EtwNative.ControlTrace(handle, null, memory, 1);
                    if (status != 0 && status != 4201 && status != 234) throw new Win32Exception((int)status, "Stopping own ETW session failed: " + sessionName);
                    var p = (EtwNative.Properties)Marshal.PtrToStructure(memory, typeof(EtwNative.Properties));
                    EventsLost = p.EventsLost + p.LogBuffersLost + p.RealTimeBuffersLost;
                    handle = 0; running = false;
                    if (deadline != null) { deadline.Dispose(); deadline = null; }
                    return path;
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
        }
        internal void Complete()
        {
            string file = null; Exception error = null;
            try { file = Stop(); } catch (Exception ex) { error = ex; AppLog.Write(ex.ToString()); }
            var callback = Completed;
            if (callback != null) callback(file, error);
        }
        public void Dispose() { Stop(); }
    }

    internal static class NativeEtwAnalyzer
    {
        private static readonly Guid StackGuid = new Guid("def2fe46-7bd6-4b80-bd94-f57fe20d0ce3");
        private static readonly Guid ImageGuid = new Guid("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");
        private static readonly Guid ProcessGuid = new Guid("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
        private static readonly Guid ProfileGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        internal sealed class Module
        {
            internal ulong Base, Size;
            internal uint Pid;
            internal string File;
            internal long From, To = long.MaxValue;
        }
        internal struct Key : IEquatable<Key>
        {
            internal long Time;
            internal uint Tid;
            public bool Equals(Key other) { return Time == other.Time && Tid == other.Tid; }
            public override bool Equals(object obj) { return obj is Key && Equals((Key)obj); }
            public override int GetHashCode() { return Time.GetHashCode() * 397 ^ (int)Tid; }
        }
        internal sealed class Profile { internal Key Key; internal ulong Ip; }
        internal sealed class Observation { internal Key Key; internal uint Pid; internal ulong[] Frames; }
        internal sealed class ModuleEvidence
        {
            internal int Pid, SampleCount, LeafCount, TimeBuckets;
            internal string ModuleName, ExampleStack;
            internal double ShareOfProcessStacks;
        }
        internal sealed class InterruptObservation
        {
            internal bool IsIsr;
            internal ulong Routine;
            internal long StartTime, EndTime;
        }
        internal sealed class RankedDriver
        {
            internal string Name, Level, Explanation;
            internal int Samples, LeafSamples, AffectedProcesses, DpcCount, IsrCount, LongDpcCount, LongIsrCount;
            internal double Share, DpcMilliseconds, IsrMilliseconds, MaxDpcMicroseconds, MaxIsrMicroseconds;
            internal List<ModuleEvidence> Processes;
        }
        internal sealed class AnalysisResult
        {
            internal string ReportPath, Quality;
            internal int StackCount, MatchedStackCount, ProfileCount, UnmatchedStacks;
            internal uint Lost;
            internal readonly List<ModuleEvidence> Evidence = new List<ModuleEvidence>();
            internal readonly List<RankedDriver> Drivers = new List<RankedDriver>();
            internal int DpcEventCount, IsrEventCount, UnmappedInterruptCount;
        }
        // SEMANTIC ANCHOR: Finite file/event/frame budgets. Callbacks never throw across native frames.
        // RAW_TIMESTAMP is required: StackWalk payload contains the original profile timestamp.
        internal sealed class Consumer
        {
            internal readonly Dictionary<Key, Profile> Profiles = new Dictionary<Key, Profile>();
            internal readonly List<Observation> Stacks = new List<Observation>();
            internal readonly List<Module> Modules = new List<Module>();
            internal readonly List<InterruptObservation> Interrupts = new List<InterruptObservation>();
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal CancellationToken Cancellation;
            internal Exception Error;
            internal int Frames, Records, Invalid, ImageFailures;
            internal uint Lost;
            internal int PointerSize;
            internal readonly HashSet<int> ChangingPids = new HashSet<int>();
            internal int ProcessFailures;
            internal void OnEvent(IntPtr pointer)
            {
                if (Error != null) return;
                try
                {
                    if (++Records > 4000000 || Clock.Elapsed.TotalSeconds > 90 || Cancellation.IsCancellationRequested)
                        throw new InvalidDataException("Analysis cancelled or resource/time budget exceeded.");
                    var r = (EtwNative.Record)Marshal.PtrToStructure(pointer, typeof(EtwNative.Record));
                    int width = (r.Header.Flags & 0x20) != 0 ? 4 : (r.Header.Flags & 0x40) != 0 ? 8 : PointerSize;
                    if (width != 4 && width != 8) throw new InvalidDataException("Unknown event pointer width.");
                    Guid provider = r.Header.ProviderId;
                    byte opcode = r.Header.Descriptor.Opcode;
                    if (r.UserData == IntPtr.Zero) return;
                    if (provider == ProfileGuid && (opcode == 66 || opcode == 67 || opcode == 68 || opcode == 69))
                    {
                        if (Interrupts.Count >= 250000) throw new InvalidDataException("DPC/ISR event budget exceeded.");
                        try
                        {
                            ulong initial = EtwNative.Number(EtwNative.Property(pointer, "InitialTime"));
                            ulong routine = EtwNative.Number(EtwNative.Property(pointer, "Routine"));
                            if (initial > long.MaxValue || routine == 0 || (long)initial >= r.Header.TimeStamp) { Invalid++; return; }
                            Interrupts.Add(new InterruptObservation { IsIsr = opcode == 67, Routine = routine, StartTime = (long)initial, EndTime = r.Header.TimeStamp });
                        }
                        catch (Exception ex) when (ex is Win32Exception || ex is InvalidDataException || ex is OverflowException)
                        { Invalid++; }
                    }
                    if (provider == ProfileGuid && opcode == 46)
                    {
                        if (r.UserDataLength < width + 4) { Invalid++; return; }
                        byte[] b = new byte[width + 4]; Marshal.Copy(r.UserData, b, 0, b.Length);
                        var key = new Key { Time = r.Header.TimeStamp, Tid = BitConverter.ToUInt32(b, width) };
                        if (Profiles.Count >= 250000) throw new InvalidDataException("CPU profile sample budget exceeded.");
                        if (!Profiles.ContainsKey(key)) Profiles.Add(key, new Profile { Key = key, Ip = Address(b, 0, width) });
                    }
                    else if (provider == StackGuid && opcode == 32)
                    {
                        int length = r.UserDataLength;
                        if (length < 16 + width || (length - 16) % width != 0 || (length - 16) / width > 192) { Invalid++; return; }
                        int count = (length - 16) / width;
                        if (Stacks.Count >= 250000 || Frames + count > 2000000) throw new InvalidDataException("Stack memory budget exceeded.");
                        byte[] b = new byte[length]; Marshal.Copy(r.UserData, b, 0, length);
                        ulong[] addresses = new ulong[count];
                        for (int i = 0; i < count; i++) addresses[i] = Address(b, 16 + i * width, width);
                        Stacks.Add(new Observation { Key = new Key { Time = BitConverter.ToInt64(b, 0), Tid = BitConverter.ToUInt32(b, 12) }, Pid = BitConverter.ToUInt32(b, 8), Frames = addresses });
                        Frames += count;
                    }
                    else if (provider == ProcessGuid && (opcode == 1 || opcode == 2))
                    {
                        try
                        {
                            ulong id = EtwNative.Number(EtwNative.Property(pointer, "ProcessId"));
                            if (id <= int.MaxValue) ChangingPids.Add((int)id);
                        }
                        catch (Win32Exception) { ProcessFailures++; }
                    }
                    else if (provider == ImageGuid && (opcode == 10 || opcode == 2 || opcode == 3 || opcode == 4))
                    {
                        // TDH resolves version-specific image layout, including pointer-sized ImageSize.
                        try
                        {
                            ulong address = EtwNative.Number(EtwNative.Property(pointer, "ImageBase"));
                            // Only kernel addresses can participate in this kernel-only analysis.
                            // Do not retain the per-process user DLL rundown.
                            if (!Kernel(address)) return;
                            uint pid = checked((uint)EtwNative.Number(EtwNative.Property(pointer, "ProcessId")));
                            if (opcode == 2)
                            {
                                TrackImage(address, 0, pid, null, opcode, r.Header.TimeStamp);
                                return;
                            }
                            if (opcode == 4) return; // End rundown cannot establish a prior lifetime.
                            ulong size = EtwNative.Number(EtwNative.Property(pointer, "ImageSize"));
                            byte[] name = EtwNative.Property(pointer, "FileName");
                            if (size == 0 || size > (1UL << 32) || address > ulong.MaxValue - size || name.Length % 2 != 0) { Invalid++; return; }
                            string file = Encoding.Unicode.GetString(name).TrimEnd('\0');
                            if (string.IsNullOrWhiteSpace(file) || file.Length > 1024) { Invalid++; return; }
                            TrackImage(address, size, pid, file, opcode, r.Header.TimeStamp);
                        }
                        catch (Win32Exception) { ImageFailures++; }
                    }
                }
                catch (Exception ex) { Error = ex; }
            }
            // SEMANTIC ANCHOR: Kernel images are system-wide. Repeated rundown records
            // must not consume lifetime budget; a real unload/reload remains a new lifetime.
            internal void TrackImage(ulong address, ulong size, uint pid, string file, byte opcode, long timestamp)
            {
                if (!Kernel(address)) return;
                if (opcode == 2)
                {
                    foreach (var module in Modules.Where(m => m.Base == address && m.To == long.MaxValue && timestamp >= m.From)) module.To = timestamp;
                    return;
                }
                if (opcode != 10 && opcode != 3) return;
                if (size == 0 || size > (1UL << 32) || address > ulong.MaxValue - size || string.IsNullOrWhiteSpace(file) || file.Length > 1024)
                    throw new InvalidDataException("Invalid kernel image metadata.");
                if (Modules.Any(m => m.Base == address && m.Size == size && m.To == long.MaxValue && string.Equals(m.File, file, StringComparison.OrdinalIgnoreCase))) return;
                if (Modules.Count >= 20000) throw new InvalidDataException("Kernel image lifetime budget exceeded.");
                Modules.Add(new Module { Base = address, Size = size, Pid = 0, File = file, From = opcode == 3 ? long.MinValue : timestamp });
            }
            internal uint OnBuffer(IntPtr logfile)
            {
                try
                {
                    var f = (EtwNative.LogFile)Marshal.PtrToStructure(logfile, typeof(EtwNative.LogFile));
                    Lost = Math.Max(Lost, f.Header.EventsLost + f.Header.BuffersLost);
                    return Error == null && !Cancellation.IsCancellationRequested && Clock.Elapsed.TotalSeconds <= 90 ? 1u : 0u;
                }
                catch (Exception ex) { Error = ex; return 0; }
            }
        }
        private static ulong Address(byte[] b, int offset, int width) { return width == 8 ? BitConverter.ToUInt64(b, offset) : BitConverter.ToUInt32(b, offset); }
        private static bool Kernel(ulong address) { return address >= 0xffff000000000000UL; }

        internal static AnalysisResult Analyze(string etlPath, HashSet<int> suspicious, uint captureLost, CancellationToken cancellation)
        {
            EtwNative.ValidateLayout();
            var info = new FileInfo(etlPath);
            if (!info.Exists || info.Length < 1 || info.Length > 257L * 1024 * 1024) throw new InvalidDataException("ETL size is outside the accepted 1 byte to 257 MiB range.");
            var consumer = new Consumer { Cancellation = cancellation };
            EtwNative.EventCallback events = consumer.OnEvent;
            EtwNative.BufferCallback buffers = consumer.OnBuffer;
            IntPtr filename = Marshal.StringToHGlobalUni(info.FullName);
            ulong handle = ulong.MaxValue;
            var logfile = new EtwNative.LogFile { LogFileName = filename, ProcessTraceMode = 0x10001000,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(events), BufferCallback = Marshal.GetFunctionPointerForDelegate(buffers) };
            try
            {
                handle = EtwNative.OpenTrace(ref logfile);
                if (handle == ulong.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenTrace failed.");
                if (logfile.Header.PointerSize != 8) throw new NotSupportedException("Only captures from Windows x64 are supported.");
                consumer.PointerSize = (int)logfile.Header.PointerSize;
                uint status = EtwNative.ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
                if (consumer.Error != null) throw new InvalidDataException("Native event analysis aborted: " + consumer.Error.Message, consumer.Error);
                cancellation.ThrowIfCancellationRequested();
                if (status != 0) throw new Win32Exception((int)status, "ProcessTrace failed or was cancelled.");
            }
            finally
            {
                if (handle != ulong.MaxValue) EtwNative.CloseTrace(handle);
                Marshal.FreeHGlobal(filename);
                GC.KeepAlive(events); GC.KeepAlive(buffers);
            }
            // PID-only correlation is unsafe across a lifecycle change. Exclude such PIDs
            // completely rather than merge unrelated process instances under one label.
            int lifecycleExcluded = consumer.Stacks.RemoveAll(s => s.Pid <= int.MaxValue && consumer.ChangingPids.Contains((int)s.Pid));
            var result = Aggregate(consumer.Profiles, consumer.Stacks, consumer.Modules, suspicious, logfile.Header.PerfFreq, Math.Max(captureLost, Math.Max(consumer.Lost, logfile.Header.EventsLost + logfile.Header.BuffersLost)), cancellation, consumer.Interrupts);
            result.DpcEventCount = consumer.Interrupts.Count(e => !e.IsIsr);
            result.IsrEventCount = consumer.Interrupts.Count(e => e.IsIsr);
            result.Quality += " DPC events: " + result.DpcEventCount + "; ISR events: " + result.IsrEventCount + "; unmapped DPC/ISR events: " + result.UnmappedInterruptCount + "; invalid records: " + consumer.Invalid + "; unreadable image records: " + consumer.ImageFailures + "; process decoding failures: " + consumer.ProcessFailures + "; stack records excluded due to PID changes: " + lifecycleExcluded + ".";
            if (consumer.Invalid > 0 || consumer.ImageFailures > 0 || consumer.ProcessFailures > 0)
                foreach (var d in result.Drivers) { d.Level = "Incomplete (decoding errors)"; d.Explanation += " Decoding errors prevent a reliable ranking."; }
            result.ReportPath = Path.ChangeExtension(etlPath, ".stack-report.txt");
            // Report failure must not discard the in-app evidence already computed.
            try
            {
                using (var writer = new StreamWriter(result.ReportPath, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("KernelTraceScout: CPU kernel time and direct DPC/ISR routine attribution");
                    writer.WriteLine(DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                    writer.WriteLine(result.Quality);
                    bool truncated = false;
                    foreach (var driver in result.Drivers)
                    {
                        if (writer.BaseStream.Position >= 15L * 1024 * 1024) { truncated = true; break; }
                        writer.WriteLine(driver.Name + " | DPC " + driver.DpcMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms / " + driver.DpcCount + " calls; ISR " + driver.IsrMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms / " + driver.IsrCount + " calls; long calls DPC>100us=" + driver.LongDpcCount + ", ISR>25us=" + driver.LongIsrCount + " | " + driver.Level + " | " + driver.Explanation);
                        foreach (var e in driver.Processes)
                        {
                            if (writer.BaseStream.Position >= 15L * 1024 * 1024) { truncated = true; break; }
                            writer.WriteLine("PID " + e.Pid + " | stacks " + e.SampleCount + " | sampled IP hits " + e.LeafCount + " | " + e.ExampleStack);
                        }
                    }
                    if (truncated) writer.WriteLine("Report display limit reached. Inspect the in-app evidence table for remaining results.");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Quality += " Report write failed: " + ex.Message;
                AppLog.Write("Evidence retained in GUI; report write failed: " + ex);
            }
            return result;
        }

        // SEMANTIC ANCHOR: Correlate by exact original timestamp AND thread, not callback order.
        // Denominator: correlated kernel-IP CPU samples for the PID, including unresolved frames.
        // Presence is inclusive; leaf evidence is sampled instruction residence, never causal proof.
        internal static AnalysisResult Aggregate(Dictionary<Key, Profile> profiles, List<Observation> stacks, List<Module> modules, HashSet<int> suspicious, long frequency, uint lost, CancellationToken cancellation = default(CancellationToken), List<InterruptObservation> interrupts = null)
        {
            var clock = Stopwatch.StartNew();
            var result = new AnalysisResult { StackCount = stacks.Count, ProfileCount = profiles.Count, Lost = lost };
            var ordered = modules.OrderBy(m => m.Base).ToArray();
            var totals = new Dictionary<int, int>();
            var evidence = new Dictionary<string, ModuleEvidence>(StringComparer.OrdinalIgnoreCase);
            var buckets = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
            long first = profiles.Count == 0 ? 0 : profiles.Keys.Min(k => k.Time);
            foreach (var group in stacks.GroupBy(s => s.Key))
            {
                cancellation.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds > 30) throw new InvalidDataException("Aggregation time budget exceeded.");
                Profile profile;
                if (!profiles.TryGetValue(group.Key, out profile) || !Kernel(profile.Ip)) { result.UnmatchedStacks += group.Count(); continue; }
                uint pid = group.First().Pid;
                if (pid > int.MaxValue || group.Any(s => s.Pid != pid)) { result.UnmatchedStacks += group.Count(); continue; }
                int id = (int)pid;
                totals[id] = totals.ContainsKey(id) ? totals[id] + 1 : 1;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var chain = new List<string>();
                Module leaf = Resolve(ordered, profile.Ip, pid, group.Key.Time);
                foreach (ulong address in group.SelectMany(s => s.Frames).Where(Kernel).Take(384))
                {
                    Module module = Resolve(ordered, address, pid, group.Key.Time);
                    string frame = module == null ? "0x" + address.ToString("x") : Path.GetFileName(module.File) + "+0x" + (address - module.Base).ToString("x");
                    if (chain.Count < 24) chain.Add(frame);
                    if (module != null && module.File.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) seen.Add(module.File);
                }
                if (seen.Count > 0) result.MatchedStackCount++;
                foreach (string name in seen)
                {
                    string key = id.ToString(CultureInfo.InvariantCulture) + "|" + name;
                    ModuleEvidence e;
                    if (!evidence.TryGetValue(key, out e))
                    {
                        if (evidence.Count >= 10000) throw new InvalidDataException("Evidence entry budget exceeded.");
                        string example = string.Join(" -> ", chain);
                        if (example.Length > 2048) example = example.Substring(0, 2048) + " [display truncated]";
                        e = new ModuleEvidence { Pid = id, ModuleName = name, ExampleStack = example };
                        evidence.Add(key, e); buckets.Add(key, new HashSet<long>());
                    }
                    e.SampleCount++;
                    if (leaf != null && string.Equals(leaf.File, name, StringComparison.OrdinalIgnoreCase)) e.LeafCount++;
                    if (frequency > 0) buckets[key].Add((group.Key.Time - first) / frequency);
                }
            }
            foreach (var pair in evidence)
            {
                pair.Value.ShareOfProcessStacks = 100.0 * pair.Value.SampleCount / totals[pair.Value.Pid];
                pair.Value.TimeBuckets = buckets[pair.Key].Count;
                result.Evidence.Add(pair.Value);
            }
            int denominator = totals.Values.Sum();
            var ranked = new Dictionary<string, RankedDriver>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in result.Evidence.GroupBy(e => e.ModuleName, StringComparer.OrdinalIgnoreCase))
            {
                int count = g.Sum(e => e.SampleCount), leaf = g.Sum(e => e.LeafCount);
                int affected = g.Count(e => suspicious.Contains(e.Pid));
                bool repeated = g.Any(e => e.TimeBuckets >= 3);
                double share = denominator == 0 ? 0 : 100.0 * count / denominator;
                string level = lost > 0 ? "Incomplete capture" : count < 30 ? "Insufficient stacks" :
                    affected >= 2 && leaf >= 30 && share >= 20 && repeated ? "High" :
                    affected >= 1 && repeated && count >= 50 ? "Moderate stack clue" : "Stack involvement only";
                var driver = new RankedDriver { Name = g.Key, Samples = count, LeafSamples = leaf, AffectedProcesses = affected, Share = share, Level = level, Processes = g.ToList() };
                driver.Explanation = count + " kernel stack samples; " + leaf + " samples with instruction directly in this module; " + affected + " suspicious processes. Stack share " + share.ToString("F1", CultureInfo.InvariantCulture) + "% of correlated kernel samples. This is statistical stack evidence, not proof of causation.";
                ranked[g.Key] = driver;
            }
            if (interrupts != null && frequency > 0)
            {
                foreach (var sample in interrupts)
                {
                    cancellation.ThrowIfCancellationRequested();
                    long ticks = sample.EndTime - sample.StartTime;
                    if (ticks <= 0 || ticks > frequency * 120L) { result.UnmappedInterruptCount++; continue; }
                    Module module = Resolve(ordered, sample.Routine, 0, sample.StartTime);
                    if (module == null || !module.File.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) { result.UnmappedInterruptCount++; continue; }
                    RankedDriver driver;
                    if (!ranked.TryGetValue(module.File, out driver))
                    {
                        driver = new RankedDriver { Name = module.File, Processes = new List<ModuleEvidence>(), Level = "Direct DPC/ISR routine" };
                        ranked.Add(module.File, driver);
                    }
                    double microseconds = ticks * 1000000.0 / frequency;
                    if (sample.IsIsr)
                    {
                        driver.IsrCount++; driver.IsrMilliseconds += microseconds / 1000.0;
                        driver.MaxIsrMicroseconds = Math.Max(driver.MaxIsrMicroseconds, microseconds);
                        if (microseconds > 25) driver.LongIsrCount++;
                    }
                    else
                    {
                        driver.DpcCount++; driver.DpcMilliseconds += microseconds / 1000.0;
                        driver.MaxDpcMicroseconds = Math.Max(driver.MaxDpcMicroseconds, microseconds);
                        if (microseconds > 100) driver.LongDpcCount++;
                    }
                }
            }
            foreach (var driver in ranked.Values)
            {
                if (driver.DpcCount + driver.IsrCount > 0)
                {
                    driver.Level = lost > 0 ? "Directly measured; incomplete trace" : "Direct DPC/ISR time";
                    driver.Explanation = "Routine address belongs directly to this driver module. DPC: " + driver.DpcCount + " calls, " + driver.DpcMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms total, longest " + driver.MaxDpcMicroseconds.ToString("F1", CultureInfo.InvariantCulture) + " us, above 100 us: " + driver.LongDpcCount + ". ISR: " + driver.IsrCount + " calls, " + driver.IsrMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms total, longest " + driver.MaxIsrMicroseconds.ToString("F1", CultureInfo.InvariantCulture) + " us, above 25 us: " + driver.LongIsrCount + ".";
                }
                else if (driver.Explanation == null)
                {
                    driver.Level = lost > 0 ? "Incomplete capture" : "Stack involvement only";
                    driver.Explanation = "This module appears in kernel stacks but not as a directly attributed DPC/ISR routine. Stack involvement alone does not establish causation.";
                }
                result.Drivers.Add(driver);
            }
            result.Drivers.Sort((a, b) =>
            {
                int c = (b.DpcMilliseconds + b.IsrMilliseconds).CompareTo(a.DpcMilliseconds + a.IsrMilliseconds);
                if (c != 0) return c;
                c = b.LeafSamples.CompareTo(a.LeafSamples);
                return c != 0 ? c : b.Samples.CompareTo(a.Samples);
            });
            result.Quality = "Kernel CPU profile samples: " + profiles.Count + "; correlated kernel stacks: " + denominator + "; DPC/ISR events: " + (interrupts == null ? 0 : interrupts.Count) + "; lost events/buffers: " + lost + ". Stack shares overlap and are not CPU percentages. DPC/ISR time is attributed to a loaded .sys module using the routine address. Missing stack/image data and lost events limit attribution.";
            return result;
        }
        private static Module Resolve(Module[] modules, ulong address, uint pid, long time)
        {
            int low = 0, high = modules.Length;
            while (low < high) { int mid = low + (high - low) / 2; if (modules[mid].Base <= address) low = mid + 1; else high = mid; }
            Module found = null;
            for (int i = low - 1, examined = 0; i >= 0 && examined < 64; i--, examined++)
            {
                Module m = modules[i];
                if (address - m.Base < m.Size && time >= m.From && time < m.To && (m.Pid == pid || Kernel(m.Base)))
                {
                    if (found != null && !string.Equals(found.File, m.File, StringComparison.OrdinalIgnoreCase)) return null;
                    found = m;
                }
            }
            return found;
        }
    }
}
