# KernelTraceScout WinUI 3 v0.9.1

Source project for Windows desktop (unpackaged WinUI 3). Build using Visual Studio 2022/2026 with .NET 8 SDK, the Windows App SDK prerequisites, x64, and NuGet restore. The existing WinForms project remains unchanged.

Author: Daniel Capilla
Version: 0.9.1

The app reuses the original ETW capture, ETW analyzer, process monitor, privilege, and watchdog code. Watchdog executable lookup is updated for the new process host.

**Not built or run in this Linux environment.** Validate on a dedicated Windows test system before production use, especially ETW recording, watchdog cleanup, capture completion, tooltips, export, and thread transitions. WinUI 3 unpackaged apps require the appropriate Windows App SDK runtime.
