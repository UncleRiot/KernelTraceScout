using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;

namespace KernelTraceScout
{
    // Deterministic fixtures only. Never loaded into live diagnostic results.
    internal static class Tests
    {
        private static int assertions;
        private static void Check(bool condition, string name)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            Console.WriteLine("PASS: " + name);
        }
        private static int Main()
        {
            try
            {
                EtwNative.ValidateLayout(); Check(true, "x64 Marshal sizes and critical field offsets");
                var settings = new AppSettings(); settings.Validate();
                Check(!ProcessMonitor.IsSuspicious(settings, 0.001, 0.001, 100, double.PositiveInfinity), "near-zero CPU with infinite ratio is not suspicious");
                Check(ProcessMonitor.IsSuspicious(settings, 5, 500, 90, 9), "substantial kernel-dominant work is suspicious");
                Check(!ProcessMonitor.IsSuspicious(settings, 5, 0.1, 90, 9), "minimum absolute kernel time enforced");
                settings.MinimumKernelCpuPercent = double.NaN;
                bool invalid = false; try { settings.Validate(); } catch (System.IO.InvalidDataException) { invalid = true; }
                Check(invalid, "NaN configuration rejected");
                Decode();
                ImageBudget();
                Aggregate();
                Console.WriteLine("All " + assertions + " assertions passed. Native Windows API execution was NOT tested.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void WithRecord(Guid provider, byte opcode, ushort flags, byte[] payload, Action<IntPtr> action)
        {
            IntPtr data = Marshal.AllocHGlobal(Math.Max(1, payload.Length));
            IntPtr record = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EtwNative.Record)));
            try
            {
                Marshal.Copy(payload, 0, data, payload.Length);
                var value = new EtwNative.Record { Header = new EtwNative.Header { ProviderId = provider, Descriptor = new EtwNative.Descriptor { Opcode = opcode }, Flags = flags, TimeStamp = 123 }, UserData = data, UserDataLength = (ushort)payload.Length };
                Marshal.StructureToPtr(value, record, false); action(record);
            }
            finally { Marshal.FreeHGlobal(record); Marshal.FreeHGlobal(data); }
        }
        private static void Decode()
        {
            var stack = new Guid("def2fe46-7bd6-4b80-bd94-f57fe20d0ce3");
            var profile = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
            var consumer = new NativeEtwAnalyzer.Consumer { PointerSize = 8 };
            WithRecord(stack, 32, 0x40, new byte[17], consumer.OnEvent);
            Check(consumer.Invalid == 1 && consumer.Stacks.Count == 0, "unaligned/truncated StackWalk payload rejected before reads");
            byte[] payload = new byte[24];
            Array.Copy(BitConverter.GetBytes(123L), 0, payload, 0, 8);
            Array.Copy(BitConverter.GetBytes(100u), 0, payload, 8, 4);
            Array.Copy(BitConverter.GetBytes(10u), 0, payload, 12, 4);
            Array.Copy(BitConverter.GetBytes(0xfffff80000001000UL), 0, payload, 16, 8);
            WithRecord(stack, 32, 0x40, payload, consumer.OnEvent);
            Check(consumer.Stacks.Single().Key.Time == 123 && consumer.Stacks.Single().Pid == 100 && consumer.Stacks.Single().Key.Tid == 10, "documented StackWalk fields decoded with 64-bit frames");
            byte[] cpu = new byte[16];
            Array.Copy(BitConverter.GetBytes(0xfffff80000001000UL), 0, cpu, 0, 8);
            Array.Copy(BitConverter.GetBytes(10u), 0, cpu, 8, 4);
            WithRecord(profile, 46, 0x40, cpu, consumer.OnEvent);
            Check(consumer.Profiles.ContainsKey(consumer.Stacks[0].Key), "raw CPU profile timestamp and payload thread match stack key");
            WithRecord(profile, 46, 0x40, new byte[4], consumer.OnEvent);
            Check(consumer.Invalid == 2, "short CPU profile rejected");
            consumer.Frames = 2000000;
            WithRecord(stack, 32, 0x40, payload, consumer.OnEvent);
            Check(consumer.Error is System.IO.InvalidDataException && consumer.Stacks.Count == 1, "frame budget stops callback collection");
            var cancelled = new NativeEtwAnalyzer.Consumer { PointerSize = 8, Cancellation = new CancellationToken(true) };
            WithRecord(profile, 46, 0x40, cpu, cancelled.OnEvent);
            Check(cancelled.Error != null && cancelled.Profiles.Count == 0, "cancelled callbacks do not accumulate samples");
        }

        private static void ImageBudget()
        {
            const ulong address = 0xfffff80000001000UL;
            var consumer = new NativeEtwAnalyzer.Consumer();
            for (uint i = 0; i < 25000; i++) consumer.TrackImage(0x100000 + i * 4096UL, 4096, i, "User.dll", 3, 0);
            Check(consumer.Modules.Count == 0, "more than 20000 user images do not consume kernel image budget");
            for (uint i = 0; i < 25000; i++) consumer.TrackImage(address, 4096, i, "Fixture.sys", 3, 1);
            Check(consumer.Modules.Count == 1 && consumer.Modules[0].Pid == 0, "repeated kernel rundown deduplicates across process IDs");
            consumer.TrackImage(address, 0, 99, null, 2, 10);
            Check(consumer.Modules[0].To == 10, "kernel unload closes the global lifetime");
            consumer.TrackImage(address, 4096, 100, "Fixture.sys", 10, 20);
            Check(consumer.Modules.Count == 2 && consumer.Modules[1].From == 20, "kernel reload creates a distinct lifetime");
            consumer.TrackImage(address + 4096, 4096, 0, "EndOnly.sys", 4, 30);
            Check(consumer.Modules.Count == 2, "end rundown does not invent a prior lifetime");
            bool bounded = false;
            for (int i = consumer.Modules.Count; i < 20000; i++) consumer.Modules.Add(new NativeEtwAnalyzer.Module());
            try { consumer.TrackImage(address + 8192, 4096, 0, "New.sys", 10, 30); }
            catch (System.IO.InvalidDataException) { bounded = true; }
            Check(bounded && consumer.Modules.Count == 20000, "hard kernel lifetime limit remains enforced");
        }

        private static void Aggregate()
        {
            const ulong address = 0xfffff80000001000UL;
            var profiles = new Dictionary<NativeEtwAnalyzer.Key, NativeEtwAnalyzer.Profile>();
            var stacks = new List<NativeEtwAnalyzer.Observation>();
            var modules = new List<NativeEtwAnalyzer.Module> {
                new NativeEtwAnalyzer.Module { Base = address, Size = 4096, File = "Fixture.sys", From = 0, To = 10000 },
                new NativeEtwAnalyzer.Module { Base = address + 4096, Size = 4096, File = "Caller.sys", From = 0, To = 10000 }
            };
            for (int i = 0; i < 120; i++)
            {
                var key = new NativeEtwAnalyzer.Key { Time = i * 100, Tid = (uint)(10 + i % 2) };
                profiles.Add(key, new NativeEtwAnalyzer.Profile { Key = key, Ip = address + 32 });
                stacks.Add(new NativeEtwAnalyzer.Observation { Key = key, Pid = (uint)(100 + i % 2), Frames = new[] { address + 32, address + 64, address + 4096 + 32 } });
                // Separate kernel/user stack records for the same original event must not double-count.
                stacks.Add(new NativeEtwAnalyzer.Observation { Key = key, Pid = (uint)(100 + i % 2), Frames = new[] { address + 32 } });
            }
            var interrupts = new List<NativeEtwAnalyzer.InterruptObservation> {
                new NativeEtwAnalyzer.InterruptObservation { IsIsr = false, Routine = address + 32, StartTime = 300, EndTime = 401 },
                new NativeEtwAnalyzer.InterruptObservation { IsIsr = true, Routine = address + 32, StartTime = 500, EndTime = 526 }
            };
            var result = NativeEtwAnalyzer.Aggregate(profiles, stacks, modules, new HashSet<int> { 100, 101 }, 1000000, 0, default(CancellationToken), interrupts);
            var driver = result.Drivers.Single(d => d.Name == "Fixture.sys");
            Check(driver.Samples == 100, "unloaded driver excluded from later sample timestamps");
            Check(driver.LeafSamples == 100 && driver.AffectedProcesses == 2, "sampled instruction residence and cross-process count");
            Check(result.Drivers.Single(d => d.Name == "Caller.sys").LeafSamples == 0, "caller participation is not exclusive CPU residence");
            Check(driver.Level == "High", "repeated multi-process CPU evidence can reach high suspicion");
            Check(Math.Abs(driver.Share - 100.0 * 100 / 120) < 0.0001, "unresolved samples remain in denominator");
            Check(result.Evidence.Where(e => e.ModuleName == "Fixture.sys").Sum(e => e.SampleCount) == 100, "recursive frames and split stack records count once per sample");
            Check(driver.DpcCount == 1 && Math.Abs(driver.DpcMilliseconds - 0.101) < 0.0001 && driver.LongDpcCount == 1, "DPC routine address is mapped to driver and duration over 100us counted");
            Check(driver.IsrCount == 1 && Math.Abs(driver.IsrMilliseconds - 0.026) < 0.0001 && driver.LongIsrCount == 1, "ISR routine address is mapped to driver and duration over 25us counted");
            var lost = NativeEtwAnalyzer.Aggregate(profiles, stacks, modules, new HashSet<int> { 100, 101 }, 1000, 1);
            Check(lost.Drivers.All(d => d.Level == "Insufficient data"), "loss suppresses strong ranking");
            var noProfile = NativeEtwAnalyzer.Aggregate(new Dictionary<NativeEtwAnalyzer.Key, NativeEtwAnalyzer.Profile>(), stacks, modules, new HashSet<int>(), 1000, 0);
            Check(noProfile.Drivers.Count == 0 && noProfile.UnmatchedStacks == stacks.Count, "stacks without exact CPU timestamp/thread match are not attributed");
            var ambiguous = new List<NativeEtwAnalyzer.Module>(modules) { new NativeEtwAnalyzer.Module { Base = address, Size = 4096, File = "Conflicting.sys", From = 0, To = 10000 } };
            var conflict = NativeEtwAnalyzer.Aggregate(profiles, stacks, ambiguous, new HashSet<int>(), 1000, 0);
            Check(conflict.Evidence.All(e => e.ModuleName != "Fixture.sys" && e.ModuleName != "Conflicting.sys"), "ambiguous image ranges remain unresolved");
            bool cancelled = false;
            try { NativeEtwAnalyzer.Aggregate(profiles, stacks, modules, new HashSet<int>(), 1000, 0, new CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "aggregation cancellation honored");
        }
    }
}
