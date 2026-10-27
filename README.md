# KernelTraceScout

**Source update only. No rebuilt executable is included.**
Build it on Windows with the .NET Framework 4.8 targeting pack before running it.

## Run and see an analysis

1. Open `KernelTraceScout.sln` in Visual Studio 2022, select **Release / x64**, and
   build the app. For ETW, start it as administrator; limited mode can only read
   process CPU counters.
2. In **Prozesse**, enable automatic refresh and let it run for at least five seconds.
   The table is sorted by Kernel-CPU. It shows system-wide CPU percentage, average
   logical cores consumed, kernel milliseconds per measurement interval, user CPU,
   and the Kernel:User ratio. The app's own process and PID 4 are omitted here.
3. The default alert rule is at least 5% of total system CPU in kernel mode per interval,
   at least 50 ms kernel time, at least 75% kernel share, and a Kernel:User ratio of 3:1.
   It must persist through three valid intervals and at least five seconds. The rule is
   visible in the status bar. This is an adjustable diagnostic heuristic, not a Windows
   standard or a universal definition of excessive CPU.
4. Start **ETW-Aufzeichnung** while the problem is occurring. After the capture, the app
   returns to **Prozesse**. The column **ETW-Treiberhinweis pro Prozess** shows the strongest
   sampled `.sys` module for that process. A CPU sample whose instruction address is inside
   the module is shown separately from a module that only appears elsewhere in the stack.
   If the module name matches a currently listed minifilter name, the column says so.
5. Open **Treiber-/Stack-Auswertung** for module details and related process stacks. Direct
   DPC/ISR routine time is measured separately and is not the process's file-I/O kernel time.
   Kernel-stack correlation can point to a minifilter running in a process's kernel call path,
   but sampling is statistical and does not prove that the filter caused the entire process
   load. The **Dateisystem-Minifilter** tab is an inventory; being loaded does not prove activity.
   If ETW reports no correlated stack evidence, the app cannot identify a process's driver from
   that capture. `System` is not itself presented as the culprit.

All measurement data displayed during normal use comes from process counters or ETW.
Deterministic fixtures exist only in the separate test executable.

## What counts as long DPC/ISR work

Microsoft's driver guidance recommends keeping an individual DPC below 100 µs and an
individual ISR below 25 µs. The table counts calls that exceed those values. It also
shows each driver's summed DPC and ISR time, so repeated shorter calls remain visible.
Those per-call recommendations do not define a universal process-level Kernel-CPU limit.

## Configuration

`KernelTraceScout.config.xml` is next to the EXE. Edit it while the application is closed.
The threshold, refresh interval and capture duration can also be set in the GUI.
The file is copied to the build output; an existing file is preserved when newer.

| Setting | Default | Meaning |
|---|---:|---|
| KernelThresholdPercent | 75 | Minimum kernel share of the process's CPU time |
| RefreshIntervalSeconds | 2 | Regular auto-refresh interval, 1–300 s |
| MinimumKernelCpuPercent | 5 | Minimum kernel CPU as a percentage of total system capacity |
| MinimumKernelMilliseconds | 50 | Absolute kernel CPU time in the measured interval |
| MinimumKernelUserRatio | 3 | Kernel/User time ratio |
| MinimumValidSamples | 3 | Valid measurement intervals before classification |
| MinimumObservationSeconds | 5 | Minimum accumulated observation duration |
| StrongSuspiciousSamples | 3 | Consecutive anomalous intervals for strong suspicion |
| CaptureSeconds | 30 | User-started capture duration, 5–120 s |
| MaximumTraceMegabytes | 128 | ETL size cap, 16–256 MiB |

Zero user time is shown as “kein User-CPU”; the UI does not show Infinity. A ratio
alone cannot trigger an alert. PID and creation time identify a live process;
ETW PIDs with observed creation/exit events are conservatively excluded from evidence.
Process names are attached only when the captured and live creation times match.

## Capture and analysis limits

- One stable, application-specific system logger name; no replacement of existing sessions.
- No capture until the user starts it. Independent system logger support requires at least
  Windows 8 / Server 2012. These are API minima, not a claim of current OS support or validation.
- At most 120 s per capture and 256 MiB ETL, sequential mode (stops at file limit).
- Requested 64 KiB native buffers: two per logical processor, minimum eight; capture refused
  above 256 active logical processors. Windows can adjust buffer settings.
- Watchdog is a second instance of this same EXE, not a third-party program. It monitors the
  parent's identity and deadline, queries the fixed session name, checks its random GUID,
  and only stops the original successfully-created session handle. It does not stop processes.
- GUI shutdown waits for capture cleanup and cancels analysis. A thread-pool deadline also
  stops the session if the GUI thread is busy. If both application and watchdog are forcibly
  killed, cleanup is not guaranteed; the ETL file cap still limits disk growth. Never stop an
  unknown session merely because it has a similar name.
- No registry, driver, service, security software, sampling-frequency or account-policy changes.
  SeSystemProfilePrivilege is enabled temporarily in the application's existing access token
  and restored after setup. Failure is reported.
- Decode limits: 257 MiB input; 4 million events; 250,000 CPU profiles; 250,000 stack events;
  250,000 DPC/ISR events;
  2 million stored frame addresses; 20,000 deduplicated kernel-image lifetime records; 10,000 evidence entries.
- Consumer processing budget 90 s plus aggregation budget 30 s; cancellation checked in
  callbacks and aggregation. These are cooperative budgets, not a hard OS CPU quota or a
  guarantee that a blocking Windows API call returns within the budget.
- Retention keeps three prior ETLs before starting a new capture (four including the new one),
  associated reports, seven days of text logs, and a 2 MiB current/previous rotation per log date.
  Reports are limited to approximately 15 MiB. Failed retention prevents a new capture.
- ETW buffers may be lost. Unknown schemas, malformed records and loss prevent strong ranking.
  Image data is decoded by Windows TDH, never by guessed version-dependent string offsets.
  Image unload timestamps and ambiguous overlapping ranges are respected.
- Symbol functions are not resolved. Frames show module names and hexadecimal offsets.
  Windows paging, missing image metadata, sample limits and truncated stacks can prevent
  driver identification. No registry setting is changed to force stack availability.

## Build

Visual Studio 2022 with .NET Framework 4.8 targeting pack and desktop development workload:
open `KernelTraceScout.sln`, choose Release / x64 and build. This source package omits
the executable so an older binary cannot be mistaken for the updated build.
The project has no third-party NuGet dependencies. A non-Windows cross-build may restore
Microsoft reference assemblies as build-time dependencies; these are not shipped with the app.

CLI with a suitable .NET SDK and targeting packs:

```text
dotnet build KernelTraceScout.csproj -c Release -m:1
dotnet build KernelTraceScout.Tests.csproj -c Release -m:1
bin-tests\net48\KernelTraceScout.Tests.exe
```

Optional .NET 10 build (requires the .NET 10 SDK; its supported Windows runtime is required):

```text
dotnet build KernelTraceScout.csproj -c Release -m:1 -p:BuildNet10=true
```

No complete redevelopment is required for the second target. Its Windows runtime behavior
has not been tested. Launch its generated EXE apphost to support same-EXE watchdog startup.
`VerifyEtwLayouts.cpp` contains a separate Windows SDK C++ static-assert probe. It is not an
application dependency and was not compiled here; run it in an x64 Native Tools prompt.

See `VERIFICATION.md` for executed checks and outstanding release gates.
