# 🔍 KernelTraceScout

**Find suspicious kernel CPU activity and identify potentially involved Windows drivers.**

KernelTraceScout is a lightweight Windows diagnostic tool that helps investigate unusually high CPU usage in kernel mode.

Sometimes a process shows high CPU usage even though the underlying workload may originate from a driver, such as an antivirus file-system filter. Windows Task Manager alone often cannot explain this relationship.

**KernelTraceScout helps you:**
- 📊 Monitor kernel and user CPU usage per process.
- 🔎 Identify processes with unusual kernel CPU activity.
- 🧩 Correlate ETW kernel stack samples with potentially involved drivers.
- 🛡️ Inspect installed file-system minifilters.
- 💾 Save, load and export diagnostic results.
- 🧪 Explore an example using the built-in Demo mode.

> **Important:** Driver correlations are diagnostic indications, not definitive proof of causality.

## 📸 Screenshots

![KernelTraceScout – Process overview](https://github.com/user-attachments/assets/dddb73e3-6cd0-401b-ade6-002e149722e7)

![KernelTraceScout – Driver and stack analysis](https://github.com/user-attachments/assets/84cda0c1-7a9c-46dd-9ec3-ee59a226ebfb)

---

## 🚀 Getting started

**Requirements**
- Windows with .NET Framework 4.8
- Administrator privileges for ETW capture
- Visual Studio 2022 with the .NET Framework 4.8 targeting pack to build from source

Open `KernelTraceScout.sln`, select **Release / x64**, build and start the application.

Without administrator privileges, limited process monitoring remains available.

## 🩺 Example: A process has unusually high kernel CPU usage

Imagine a monitoring process, `Zabbix2.exe`, showing 45% total CPU usage, although its normal workload should be much lower.

**Step 1 — Monitor processes**

Open the **Processes** tab and click **Start auto-refresh**.

KernelTraceScout continuously updates CPU measurements and highlights suspicious activity.

**Step 2 — Capture the problem**

While the high CPU usage occurs, click **Start ETW capture**.

The default capture duration is 30 seconds. KernelTraceScout collects Windows tracing information and analyzes the results.

**Step 3 — Examine the possible driver**

The **Processes** tab shows the strongest correlated driver evidence for each process.

For example:

| Process | Total CPU | Kernel CPU | Driver evidence |
|---|---:|---:|---|
| Zabbix2.exe | 45% | 40% | AntivirusFilter.sys |
| powershell.exe | 28% | 24% | AntivirusFilter.sys |

Both processes may show evidence involving the same driver.

**Step 4 — Review the details**

Open **Driver / Stack Analysis** to inspect sampled driver activity, kernel stacks and related processes.

Use **File System Minifilters** to see which file-system filters are installed.

**Step 5 — Save or share the results**

Save the results for later analysis, export them to CSV, or right-click a table to copy selected rows or the entire table as formatted text or CSV.

> 💡 **Try it yourself:** Click **Demo** to display this fictional scenario alongside the existing measurements. Click Demo again to remove the simulated entries.

---

## 🧰 Main controls

| Control | Purpose |
|---|---|
| ▶️ Start ETW capture | Record and analyze kernel activity |
| 🔄 Refresh now | Update process measurements once |
| ⏱️ Start auto-refresh | Monitor CPU usage continuously |
| 🛡️ Refresh minifilters | Update the file-system filter inventory |
| 💾 Save / Load results | Store or reopen analysis results |
| 📋 Export CSV | Export diagnostic data |
| 🧪 Demo | Show or hide simulated driver-related CPU problems |
| ✅ Run self-test | Check built-in diagnostic functions |
| ❔ About | Display version, author, license and project information |

## 🎯 How suspicious activity is identified

KernelTraceScout considers several factors together, including:

- Kernel CPU usage relative to total system capacity
- Kernel time compared with user time
- Duration and consistency of the observed activity
- Available ETW evidence linking processes and drivers

The default heuristic uses **5% kernel CPU**, **50 ms kernel time per interval**, **75% kernel share**, and a **3:1 kernel/user ratio**, observed over multiple intervals.

These thresholds are diagnostic guidelines, not universal Windows performance limits.

## ⚠️ Limitations

- High kernel CPU usage does not automatically indicate a faulty driver.
- A driver appearing in a stack does not prove it caused the CPU load.
- Missing ETW samples, dropped events and incomplete stack information can reduce accuracy.
- DPC/ISR measurements are separate from ordinary process kernel CPU usage.
- KernelTraceScout does not modify drivers, services or security settings.

## ⚙️ Configuration

Application settings are stored in `KernelTraceScout.config.xml`, next to the executable.

Key parameters such as capture duration, refresh interval and kernel CPU threshold can be adjusted in the application.

## 🛠️ Build from source

Open the solution in **Visual Studio 2022**, select **Release / x64** and build with the **.NET Framework 4.8 targeting pack** installed.

This source distribution does not include a rebuilt executable.

The project does not declare third-party NuGet dependencies.

For detailed technical limitations and verification information, see `VERIFICATION.md`.

## 📄 License & Author

- **License:** [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.html)
- **Author:** Daniel Capilla
- **Copyright:** © 2026 Daniel Capilla
- **GitHub:** [UncleRiot/KernelTraceScout](https://github.com/UncleRiot/KernelTraceScout)

**Version:** 0.9.0.0
