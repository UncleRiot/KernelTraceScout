using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace KernelTraceScout
{
    // SEMANTIC ANCHOR: Per-process monotonic intervals; PID + creation time is the identity.
    // CPU percentages use all active logical processors. Idle/new/inaccessible rows remain visible.
    internal sealed class ProcessMonitor
    {
        private sealed class State
        {
            internal DateTime Start;
            internal long Tick;
            internal TimeSpan Kernel, User;
            internal int Valid, Consecutive;
            internal double Duration;
        }
        private Dictionary<int, State> previous = new Dictionary<int, State>();
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetActiveProcessorCount(ushort groupNumber);
        internal static uint LogicalProcessors { get { uint count = GetActiveProcessorCount(0xffff); if (count == 0) throw new Win32Exception("Processor count unavailable."); return count; } }
        internal List<ProcessRow> Read(AppSettings settings)
        {
            uint cpus = LogicalProcessors;
            if (cpus == 0) throw new Win32Exception("Cannot determine active logical processor count.");
            int ownPid;
            using (var self = Process.GetCurrentProcess()) ownPid = self.Id;
            var current = new Dictionary<int, State>();
            var rows = new List<ProcessRow>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    var row = new ProcessRow { Status = "Erste Messung – noch kein Intervall" };
                    try
                    {
                        row.Pid = p.Id; row.Name = p.ProcessName;
                        var start = p.StartTime.ToUniversalTime();
                        var kernel = p.PrivilegedProcessorTime; var user = p.UserProcessorTime;
                        long tick = Stopwatch.GetTimestamp();
                        var state = new State { Start = start, Tick = tick, Kernel = kernel, User = user };
                        row.Start = start;
                        State old;
                        if (previous.TryGetValue(row.Pid, out old) && old.Start == start)
                        {
                            double elapsed = (tick - old.Tick) / (double)Stopwatch.Frequency;
                            double k = (kernel - old.Kernel).TotalSeconds, u = (user - old.User).TotalSeconds;
                            if (elapsed >= 0.1 && k >= 0 && u >= 0)
                            {
                                row.Valid = true; row.Elapsed = elapsed;
                                row.Cpu = 100 * (k + u) / (elapsed * cpus);
                                row.Kernel = 100 * k / (elapsed * cpus); row.User = 100 * u / (elapsed * cpus);
                                row.KernelCores = k / elapsed; row.KernelMilliseconds = k * 1000;
                                row.Share = k + u <= 0 ? 0 : 100 * k / (k + u);
                                row.Ratio = u <= 0 ? k <= 0 ? 0 : double.PositiveInfinity : k / u;
                                state.Valid = old.Valid + 1; state.Duration = old.Duration + elapsed;
                                bool eligible = row.Pid != 4 && row.Pid != ownPid;
                                bool suspect = eligible && IsSuspicious(settings, row.Kernel, k * 1000, row.Share, row.Ratio);
                                state.Consecutive = suspect ? old.Consecutive + 1 : 0;
                                row.Status = !eligible ? "System/Tool overhead — not ranked as culprit" :
                                    state.Valid < settings.MinimumValidSamples || state.Duration < settings.MinimumObservationSeconds ?
                                    "Collecting data: " + state.Valid + "/" + settings.MinimumValidSamples + " intervals, " + state.Duration.ToString("F1") + "/" + settings.MinimumObservationSeconds + " s" :
                                    !suspect ? "Below threshold" :
                                    (state.Consecutive >= settings.StrongSuspiciousSamples ? "Highly suspicious" : "Suspicious") +
                                    ": Kernel " + row.Kernel.ToString("F1") + "% system CPU; share " + row.Share.ToString("F0") + "%; ratio " +
                                    (double.IsPositiveInfinity(row.Ratio) ? "no user CPU" : row.Ratio.ToString("F1") + ":1");
                            }
                        }
                        current[row.Pid] = state;
                    }
                    catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                    { row.Status = "Unavailable (process exited or access denied)"; }
                    rows.Add(row);
                }
            }
            previous = current;
            return rows.OrderByDescending(r => r.Kernel).ToList();
        }
        internal static bool IsSuspicious(AppSettings s, double kernelCpuPercent, double kernelMs, double share, double ratio)
        {
            return kernelCpuPercent >= s.MinimumKernelCpuPercent && kernelMs >= s.MinimumKernelMilliseconds && share >= s.KernelThresholdPercent && ratio >= s.MinimumKernelUserRatio;
        }
    }
}
