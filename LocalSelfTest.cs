using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace KernelTraceScout
{
    // Deterministic in-memory test fixtures. No ETW session, disk output or system inspection.
    internal static class LocalSelfTest
    {
        private const ulong DriverAddress = 0xfffff80000100000UL;
        private const ulong CallerAddress = 0xfffff80000200000UL;

        internal static string Run()
        {
            var report = new StringBuilder("Local synthetic diagnostic self-test\r\n");
            int passed = 0;
            Check(report, ref passed, "High kernel/user ratio alone does not identify a driver",
                !ProcessMonitor.IsSuspicious(new AppSettings(), 0.001, 0.001, 99, double.PositiveInfinity));
            Check(report, ref passed, "Substantial kernel-dominant CPU is flagged",
                ProcessMonitor.IsSuspicious(new AppSettings(), 8, 250, 95, 10));

            var direct = Build(120, 120, 2, false, 0);
            var driver = direct.Drivers.Single(d => d.Name == "SyntheticFilter.sys");
            Check(report, ref passed, "Repeated direct driver samples in two affected processes rank high",
                driver.Level == "High" && driver.LeafSamples == 120 && driver.AffectedProcesses == 2);

            var callerOnly = Build(120, 0, 2, true, 0);
            var caller = callerOnly.Drivers.Single(d => d.Name == "SyntheticFilter.sys");
            Check(report, ref passed, "Stack-only presence is not counted as CPU instruction residence",
                caller.LeafSamples == 0 && caller.Level != "High");

            var lost = Build(120, 120, 2, false, 1);
            Check(report, ref passed, "Lost ETW events prevent high-confidence grading",
                lost.Drivers.Single(d => d.Name == "SyntheticFilter.sys").Level == "Incomplete capture");

            var single = Build(120, 120, 1, false, 0);
            Check(report, ref passed, "One affected process does not meet two-process high criterion",
                single.Drivers.Single(d => d.Name == "SyntheticFilter.sys").Level != "High");

            report.AppendLine("PASS: " + passed + " synthetic checks. No production data was read or transmitted.");
            report.Append("NOTE: Passing fixtures does not validate real-world cause attribution.");
            return report.ToString();
        }

        private static void Check(StringBuilder report, ref int passed, string label, bool ok)
        {
            report.AppendLine((ok ? "PASS: " : "FAIL: ") + label);
            if (!ok) throw new InvalidOperationException(report.ToString());
            passed++;
        }

        private static NativeEtwAnalyzer.AnalysisResult Build(int samples, int directSamples, int processCount, bool includeDriverStack, uint lost)
        {
            var profiles = new Dictionary<NativeEtwAnalyzer.Key, NativeEtwAnalyzer.Profile>();
            var stacks = new List<NativeEtwAnalyzer.Observation>();
            var modules = new List<NativeEtwAnalyzer.Module>
            {
                new NativeEtwAnalyzer.Module { Base = DriverAddress, Size = 4096, File = "SyntheticFilter.sys", From = 0, To = long.MaxValue },
                new NativeEtwAnalyzer.Module { Base = CallerAddress, Size = 4096, File = "SyntheticCaller.sys", From = 0, To = long.MaxValue }
            };
            var suspicious = new HashSet<int>();
            for (int i = 0; i < processCount; i++) suspicious.Add(4000 + i);
            for (int i = 0; i < samples; i++)
            {
                var key = new NativeEtwAnalyzer.Key { Time = i * 1000000L, Tid = (uint)(100 + i % processCount) };
                ulong ip = i < directSamples ? DriverAddress + 16 : CallerAddress + 16;
                profiles.Add(key, new NativeEtwAnalyzer.Profile { Key = key, Ip = ip });
                stacks.Add(new NativeEtwAnalyzer.Observation
                {
                    Key = key, Pid = (uint)(4000 + i % processCount),
                    Frames = includeDriverStack ? new[] { DriverAddress + 16, CallerAddress + 16 } : new[] { CallerAddress + 16 }
                });
            }
            return NativeEtwAnalyzer.Aggregate(profiles, stacks, modules, suspicious, 1000000, lost, CancellationToken.None);
        }
    }
}
