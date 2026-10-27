using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Threading.Tasks;
using System.Threading;
using System.Xml;

namespace KernelTraceScout
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (CaptureWatchdog.Run(args)) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                AppLog.Initialize();
                AppLog.Write("Application started.");
                Application.Run(new MainWindow());
            }
            catch (Exception ex)
            {
                AppLog.Write("Fatal error: " + ex);
                MessageBox.Show(ex.Message, "KernelTraceScout - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { AppLog.Write("Application stopped."); }
        }
    }

    // SSOT: Configuration is kept only in KernelTraceScout.config.xml next to the executable.
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
        public int CaptureSeconds { get; set; }
        public int MaximumTraceMegabytes { get; set; }
        public AppSettings()
        {
            KernelThresholdPercent = 75; RefreshIntervalSeconds = 2;
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

    internal enum ToolbarIcon
    {
        CheckOnce,
        Reload,
        Play,
        Pause,
        Export,
        Analyze,
        Stop,
        Test
    }

    internal sealed class MainWindow : Form
    {
        private readonly DataGridView processes = MakeGrid();
        private readonly DataGridView filters = MakeGrid();
        private readonly DataGridView stackEvidence = MakeGrid();
        private NativeEtwAnalyzer.AnalysisResult lastAnalysis;
        private readonly Label information = new Label { Dock = DockStyle.Bottom, Height = 58, AutoSize = false,
            Text = "Kernel time is CPU time spent in the operating system. ETW (Event Tracing for Windows) shows measured interrupt activity and sampled driver clues.",
            TextAlign = ContentAlignment.MiddleLeft };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly Button manualRefresh = new Button { Text = "Refresh processes", AutoSize = true };
        private readonly Button traceButton = new Button { Text = "Start ETW capture", AutoSize = true };
        private readonly NativeEtwCapture trace = new NativeEtwCapture();
        private readonly Button autoRefresh = new Button { Text = "Enable auto refresh", AutoSize = true };
        private readonly NumericUpDown refreshSeconds = new NumericUpDown { Minimum = 1, Maximum = 300, Value = 2, Width = 56 };
        private readonly ProcessMonitor monitor = new ProcessMonitor();
        private List<ProcessRow> currentRows = new List<ProcessRow>();
        private readonly TextBox details = new TextBox { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly System.Windows.Forms.Timer captureTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly Dictionary<int, DateTime> captureIdentities = new Dictionary<int, DateTime>();
        private readonly HashSet<int> captureSuspicious = new HashSet<int>();
        private readonly HashSet<string> loadedMinifilterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool refreshing, analyzing, captureCompleting, closingAllowed, starting, shuttingDown;
        private Task activeRefresh;
        private Task activeCapture;
        private bool elevated;
        private readonly NumericUpDown captureSeconds = new NumericUpDown { Minimum = 5, Maximum = 120, Width = 55 };
        private readonly Button refresh = new Button { Text = "Refresh filters", AutoSize = true };
        private readonly Button export = new Button { Text = "Export CSV", AutoSize = true };
        private readonly Button demoButton = new Button { Text = "Demo", AutoSize = true };
        private readonly ToolTip hints = new ToolTip { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 100, ShowAlways = true };
        private readonly ProgressBar captureProgress = new ProgressBar { Width = 160, Height = 22, Minimum = 0, Maximum = 100, Visible = false };
        private readonly Label captureProgressLabel = new Label { AutoSize = true, Visible = false, Margin = new Padding(3, 7, 3, 3) };
        private readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private DateTime captureStartedUtc;
        private int captureDurationSeconds;
        private bool demoMode;
        private readonly NumericUpDown threshold = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 5, Width = 55 };
        private readonly AppSettings settings;


        internal MainWindow()
        {
            settings = SettingsStore.Load();
            Text = "KernelTraceScout - Kernel CPU Diagnostics";
            using (var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KernelTraceScout.Icons.KernelTraceScout.ico"))
                if (iconStream != null) Icon = new Icon(iconStream);
            Width = 1250;
            Height = 780;
            MinimumSize = new Size(900, 550);
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 108 };
            var buttonBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 7, 8, 0), WrapContents = false, AutoScroll = true };
            var settingsBar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8), WrapContents = false, AutoScroll = true };
            toolbar.Controls.Add(settingsBar);
            toolbar.Controls.Add(buttonBar);
            buttonBar.Controls.Add(manualRefresh);
            buttonBar.Controls.Add(autoRefresh);
            buttonBar.Controls.Add(refresh);
            buttonBar.Controls.Add(export);
            buttonBar.Controls.Add(traceButton);
            buttonBar.Controls.Add(demoButton);
            settingsBar.Controls.Add(new Label { Text = "Kernel CPU limit (% of total):", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            threshold.Value = (decimal)settings.MinimumKernelCpuPercent;
            settingsBar.Controls.Add(threshold);
            settingsBar.Controls.Add(new Label { Text = "Interval (s):", AutoSize = true, Margin = new Padding(8, 7, 3, 3) });
            refreshSeconds.Value = settings.RefreshIntervalSeconds;
            timer.Interval = settings.RefreshIntervalSeconds * 1000;
            settingsBar.Controls.Add(refreshSeconds);
            settingsBar.Controls.Add(new Label { Text = "Capture duration (s):", AutoSize = true, Margin = new Padding(8, 7, 3, 3) });
            captureSeconds.Value = settings.CaptureSeconds;
            settingsBar.Controls.Add(captureSeconds);
            settingsBar.Controls.Add(captureProgress);
            settingsBar.Controls.Add(captureProgressLabel);
            SetButtonIcon(manualRefresh, ToolbarIcon.CheckOnce, "Check running processes once");
            SetButtonIcon(autoRefresh, ToolbarIcon.Play, "Start automatic refresh");
            SetButtonIcon(refresh, ToolbarIcon.Reload, "Reload the file system filter list");
            SetButtonIcon(export, ToolbarIcon.Export, "Export the current table to a CSV file");
            SetButtonIcon(traceButton, ToolbarIcon.Analyze, "Analyze drivers with an ETW capture (Event Tracing for Windows, administrator required)");
            SetButtonIcon(demoButton, ToolbarIcon.Test, "Show fictional example results (test mode)");
            captureSeconds.ValueChanged += (sender, e) => { settings.CaptureSeconds = (int)captureSeconds.Value; SaveSettings(); };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var ptab = new TabPage("Processes");
            var ftab = new TabPage("File System Filters");
            var etab = new TabPage("Driver / Stack Analysis");
            var helpTab = new TabPage("Help");
            var helpText = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 10), Text =
                "KernelTraceScout - Quick Help\r\n\r\n" +
                "What this tool does\r\n" +
                "- It shows which programs use a lot of CPU time.\r\n" +
                "- It can record a short trace to look for driver clues.\r\n\r\n" +
                "Buttons\r\n" +
                "- Check once (green tick): Check running programs one time.\r\n" +
                "- Auto refresh (play / pause): Start or stop repeated checks.\r\n" +
                "- Reload filters: Refresh the list of loaded file system filters. Use this after software changes, driver installs or service starts if you want to see the current filter list again.\r\n" +
                "- Export: Save the visible table as a CSV file.\r\n" +
                "- Analyze: Start a short ETW capture. ETW means Event Tracing for Windows. It records detailed Windows performance events and helps find driver clues. Administrator rights are needed.\r\n" +
                "- Test (red button): Show fake example results. This does not scan your PC.\r\n\r\n" +
                "Tabs\r\n" +
                "- Processes: Running programs and their CPU load.\r\n" +
                "- File System Filters: Filter drivers that are currently loaded.\r\n" +
                "- Driver / Stack Analysis: Driver clues found in the ETW trace.\r\n\r\n" +
                "Important\r\n" +
                "- A driver clue is not proof. It only shows where to look next." };
            helpTab.Padding = new Padding(12); helpTab.Controls.Add(helpText);
            ptab.Controls.Add(processes);
            ftab.Controls.Add(filters);
            etab.Controls.Add(stackEvidence);
            etab.Controls.Add(details);
            tabs.TabPages.Add(ptab);
            tabs.TabPages.Add(ftab);
            tabs.TabPages.Add(etab);
            tabs.TabPages.Add(helpTab);
            Controls.Add(tabs);
            Controls.Add(information);
            Controls.Add(toolbar);
            processes.Columns.Add("name", "Process");
            processes.Columns.Add("pid", "PID");
            processes.Columns.Add("cpu", "Total CPU %");
            processes.Columns.Add("kernel", "Kernel CPU %");
            processes.Columns.Add("cores", "Kernel core equivalent");
            processes.Columns.Add("kernelms", "Kernel time / interval");
            processes.Columns.Add("user", "User-CPU %");
            processes.Columns.Add("ratio", "Kernel : User");
            processes.Columns.Add("status", "Assessment / reason");
            processes.Columns.Add("cause", "ETW driver clue per process");
            stackEvidence.Columns.Add("module", "Driver / kernel module");
            stackEvidence.Columns.Add("dpc", "DPC time (ms)");
            stackEvidence.Columns.Add("isr", "ISR time (ms)");
            stackEvidence.Columns.Add("longdpc", "DPC > 100 µs");
            stackEvidence.Columns.Add("longisr", "ISR > 25 µs");
            stackEvidence.Columns.Add("samples", "Kernel stack samples");
            stackEvidence.Columns.Add("level", "Evidence");
            stackEvidence.Columns[0].Width = 350;
            stackEvidence.SelectionChanged += (sender, e) => DisplayDriverDetails();
            filters.Columns.Add("filter", "Minifilter");
            filters.Columns.Add("instances", "Instances");
            filters.Columns.Add("frame", "Frame");
            filters.Columns.Add("note", "Note");
            processes.Columns[0].Width = 230;
            processes.Columns[2].Width = 115;
            processes.Columns[3].Width = 125;
            processes.Columns[4].Width = 115;
            processes.Columns[5].Width = 160;
            processes.Columns[6].Width = 110;
            processes.Columns[7].Width = 120;
            processes.Columns[8].Width = 360;
            processes.Columns[9].Width = 320;
            // Keep the process-to-driver evidence visible without horizontal scrolling.
            processes.Columns[9].DisplayIndex = 2;
            filters.Columns[0].Width = 300;
            filters.Columns[3].Width = 420;
            refresh.Click += (sender, e) => RefreshFilters();
            export.Click += (sender, e) => ExportCsv(tabs.SelectedIndex == 0 ? processes : tabs.SelectedIndex == 1 ? filters : stackEvidence);
            threshold.ValueChanged += (sender, e) =>
            {
                settings.MinimumKernelCpuPercent = (double)threshold.Value;
                SaveSettings();
            };
            // SEMANTIC ANCHOR: Auto-refresh is opt-in; one manual refresh makes one measurement.
            // Attribution remains unknown without ETW kernel stack evidence.
            manualRefresh.Click += (sender, e) => RefreshProcesses();
            autoRefresh.Click += (sender, e) =>
            {
                if (timer.Enabled)
                {
                    timer.Stop();
                    SetButtonIcon(autoRefresh, ToolbarIcon.Play, "Start automatic refresh");
                }
                else
                {
                    RefreshProcesses();
                    timer.Start();
                    SetButtonIcon(autoRefresh, ToolbarIcon.Pause, "Stop automatic refresh");
                }
            };
            refreshSeconds.ValueChanged += (sender, e) =>
            {
                settings.RefreshIntervalSeconds = (int)refreshSeconds.Value;
                timer.Interval = settings.RefreshIntervalSeconds * 1000;
                SaveSettings();
            };
            traceButton.Click += (sender, e) =>
            {
                if (analyzing || captureCompleting || starting || closing.IsCancellationRequested) return;
                if (!Environment.Is64BitProcess || Environment.OSVersion.Version < new Version(6, 2))
                {
                    MessageBox.Show(this, "ETW capture needs Windows 8 / Server 2012 or newer.", "ETW unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!elevated && !trace.Running)
                {
                    MessageBox.Show(this, "ETW capture needs administrator rights. Restart the tool as administrator to use this button.", "Administrator rights required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                activeCapture = ToggleCapture();
            };
            trace.Completed += (file, error) =>
            {
                if (IsDisposed || closing.IsCancellationRequested) return;
                try { BeginInvoke((Action)(() => { if (!closing.IsCancellationRequested && !captureCompleting && !analyzing) activeCapture = FinishCapture(file, error, tabs); })); }
                catch (InvalidOperationException) { }
            };
            captureTimer.Tick += (sender, e) => RefreshProcesses();
            progressTimer.Tick += (sender, e) =>
            {
                double elapsed = Math.Max(0, (DateTime.UtcNow - captureStartedUtc).TotalSeconds);
                captureProgress.Value = Math.Max(0, Math.Min(100, (int)(100 * elapsed / captureDurationSeconds)));
                captureProgressLabel.Text = Math.Min(captureDurationSeconds, (int)elapsed) + " / " + captureDurationSeconds + " s";
            };
            demoButton.Click += (sender, e) => ShowDemo(tabs);
            timer.Tick += (sender, e) => RefreshProcesses();
            Shown += (sender, e) =>
            {
                PromptForElevation();
                if (IsDisposed) return;
                RefreshProcesses(); // baseline only; no recurring sampling until enabled
                RefreshFilters();
            };
            FormClosing += async (sender, e) =>
            {
                if (closingAllowed) return;
                e.Cancel = true;
                if (shuttingDown) return;
                shuttingDown = true;
                timer.Stop(); captureTimer.Stop(); progressTimer.Stop(); closing.Cancel();
                traceButton.Enabled = manualRefresh.Enabled = autoRefresh.Enabled = false;
                try
                {
                    if (activeCapture != null) await activeCapture;
                    await Task.Run(() => trace.Dispose());
                    if (activeRefresh != null) await activeRefresh;
                    closingAllowed = true;
                    Close();
                }
                catch (Exception ex)
                {
                    ShowError("ETW cleanup failed; retry closing", ex);
                    shuttingDown = false;
                }
            };
        }

        private async Task ToggleCapture()
        {
            starting = true; traceButton.Enabled = false;
            try
            {
                if (trace.Running) { progressTimer.Stop(); await Task.Run(() => trace.Complete()); return; }
                await RefreshProcessesAsync();
                if (closing.IsCancellationRequested) return;
                captureIdentities.Clear(); captureSuspicious.Clear();
                foreach (var row in currentRows.Where(r => r.Start != default(DateTime))) captureIdentities[row.Pid] = row.Start;
                lastAnalysis = null; DisplayStackEvidence();
                string file = Path.Combine(AppFiles.LogDirectory, "KernelTraceScout_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".etl");
                int seconds = settings.CaptureSeconds, mb = settings.MaximumTraceMegabytes;
                await Task.Run(() => trace.Start(file, seconds, mb));
                if (closing.IsCancellationRequested) { await Task.Run(() => trace.Dispose()); return; }
                SetButtonIcon(traceButton, ToolbarIcon.Stop, "Stop the ETW capture"); captureTimer.Start();
                captureDurationSeconds = seconds; captureStartedUtc = DateTime.UtcNow;
                captureProgress.Value = 0; captureProgress.Visible = true; captureProgressLabel.Visible = true;
                captureProgressLabel.Text = "0 / " + seconds + " s"; progressTimer.Start();
                information.Text = "Recording kernel CPU, DPC and ISR data for up to " + seconds + " seconds. File limit: " + mb + " MiB.";
            }
            catch (Exception ex) { progressTimer.Stop(); captureProgress.Visible = false; captureProgressLabel.Visible = false; ShowError("ETW capture failed", ex); }
            finally { starting = false; traceButton.Enabled = Environment.Is64BitProcess && Environment.OSVersion.Version >= new Version(6, 2) && !analyzing && !captureCompleting && !closing.IsCancellationRequested; }
        }

        private void ShowError(string title, Exception ex)
        {
            AppLog.Write(title + ": " + ex);
            information.Text = title + ": " + ex.Message;
            if (!closing.IsCancellationRequested) MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private async Task FinishCapture(string file, Exception error, TabControl tabs)
        {
            captureCompleting = true; captureTimer.Stop(); progressTimer.Stop(); traceButton.Enabled = false;
            captureProgress.Value = 100; captureProgressLabel.Text = "Analyzing...";
            try
            {
                if (error != null) throw error;
                await RefreshProcessesAsync();
                analyzing = true;
                var suspicious = new HashSet<int>(captureSuspicious);
                information.Text = "Analyzing ETW capture...";
                lastAnalysis = await Task.Run(() => NativeEtwAnalyzer.Analyze(file, suspicious, trace.EventsLost, closing.Token));
                if (closing.IsCancellationRequested) return;
                DisplayStackEvidence();
                information.Text = "ETW analysis complete. Processes shows the strongest driver clue per process; Driver / Stack Analysis shows details. ETW means Event Tracing for Windows. Stack clues do not prove causality.";
                tabs.SelectedIndex = 0;
            }
            catch (Exception ex) { ShowError("ETW analysis failed", ex); }
            finally { analyzing = false; captureCompleting = false; captureProgress.Visible = false; captureProgressLabel.Visible = false; SetButtonIcon(traceButton, trace.Running ? ToolbarIcon.Stop : ToolbarIcon.Analyze, trace.Running ? "Stop the ETW capture" : "Start an ETW capture (administrator required)"); traceButton.Enabled = Environment.Is64BitProcess && Environment.OSVersion.Version >= new Version(6, 2) && !closing.IsCancellationRequested; }
        }

        // SEMANTIC ANCHOR: All toolbar button images are embedded resources and centered by WinForms.
        // No external icon files or font glyphs are required at runtime.
        private void SetButtonIcon(Button button, ToolbarIcon icon, string hint)
        {
            button.Text = string.Empty;
            button.AutoSize = false;
            button.Size = new Size(40, 36);
            button.Margin = new Padding(3);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.ImageAlign = ContentAlignment.MiddleCenter;
            button.TextImageRelation = TextImageRelation.Overlay;
            string name = null;
            switch (icon)
            {
                case ToolbarIcon.CheckOnce: name = "CheckOnce"; break;
                case ToolbarIcon.Reload: name = "Reload"; break;
                case ToolbarIcon.Play: name = "Play"; break;
                case ToolbarIcon.Export: name = "Export"; break;
                case ToolbarIcon.Analyze: name = "Analyze"; break;
                case ToolbarIcon.Test: name = "Test"; break;
                case ToolbarIcon.Pause: name = "Pause"; break;
                case ToolbarIcon.Stop: name = "Stop"; break;
            }
            if (name != null)
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KernelTraceScout.Icons." + name + ".png"))
                {
                    if (stream == null) throw new InvalidOperationException("Missing embedded toolbar icon: " + name);
                    using (var source = Image.FromStream(stream))
                    {
                        // Keep the original embedded PNG; resample once at high quality for the button.
                        var iconBitmap = new Bitmap(30, 30, PixelFormat.Format32bppPArgb);
                        using (var graphics = Graphics.FromImage(iconBitmap))
                        using (var attributes = new ImageAttributes())
                        {
                            graphics.Clear(Color.Transparent);
                            graphics.CompositingMode = CompositingMode.SourceCopy;
                            graphics.CompositingQuality = CompositingQuality.HighQuality;
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            graphics.SmoothingMode = SmoothingMode.AntiAlias;
                            attributes.SetWrapMode(WrapMode.TileFlipXY);
                            graphics.DrawImage(source, new Rectangle(0, 0, 30, 30),
                                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                        }
                        var previousImage = button.Image;
                        button.Image = iconBitmap;
                        if (previousImage != null) previousImage.Dispose();
                    }
                }
            }
            else
            {
                button.Image = null;
                button.Text = "\u25B6";
                button.Font = new Font("Segoe UI Symbol", 13, FontStyle.Regular);
            }
            hints.SetToolTip(button, hint);
            button.AccessibleName = hint;
            button.Invalidate();
        }

        // Demo data are deliberately separate from live ETW analysis and never exported as genuine trace data.
        private void ShowDemo(TabControl tabs)
        {
            if (trace.Running || starting || analyzing || captureCompleting || refreshing || closing.IsCancellationRequested)
            {
                MessageBox.Show(this, "Stop the active capture or refresh before opening the demo.", "Demo unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            timer.Stop(); SetButtonIcon(autoRefresh, ToolbarIcon.Play, "Start automatic refresh");
            demoMode = true;
            lastAnalysis = null; captureIdentities.Clear(); captureSuspicious.Clear();
            processes.Rows.Clear(); filters.Rows.Clear(); stackEvidence.Rows.Clear(); details.Clear();
            int index = processes.Rows.Add("ExampleApp.exe", 4242, "31.0", "24.0", "1.92", "2400 ms / 10 s", "7.0", "3.4 : 1", "DEMO - Suspicious kernel CPU use", "exampledriver.sys | DEMO - sampled driver clue only");
            processes.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose;
            processes.Rows.Add("BackgroundApp.exe", 4350, "2.0", "0.3", "0.02", "30 ms / 10 s", "1.7", "0.2 : 1", "DEMO - Normal", "No driver clue");
            filters.Rows.Add("ExampleFilter", 1, 0, "DEMO - loaded filter; no fault proven");
            stackEvidence.Rows.Add("exampledriver.sys", "12.500", "0.300", 2, 0, 85, "DEMO - illustrative data only");
            details.Text = "DEMO DATA - NOT A REAL MEASUREMENT\r\n\r\n" +
                "ExampleApp.exe uses unusually high kernel CPU. The sample trace mentions exampledriver.sys. " +
                "This is a clue for further investigation, not proof that the driver caused the load.\r\n" +
                "DPC = deferred interrupt work; ISR = immediate interrupt handling.\r\n" +
                "To inspect your own computer, refresh processes and run an ETW capture as administrator.";
            information.Text = "DEMO MODE - All displayed values are fictional. Refresh processes or filters to return to live measurements.";
            tabs.SelectedIndex = 0;
        }

        private void SaveSettings()
        {
            try { SettingsStore.Save(settings); }
            catch (Exception ex)
            {
                AppLog.Write("Configuration save failed: " + ex);
                MessageBox.Show(this, ex.Message, "Configuration write failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PromptForElevation()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                AppLog.Write("Privilege check failed: " + ex);
                return;
            }
            traceButton.Enabled = Environment.Is64BitProcess && Environment.OSVersion.Version >= new Version(6, 2);
            if (!traceButton.Enabled) SetButtonIcon(traceButton, ToolbarIcon.Analyze, elevated ? "ETW requires Windows 8 / Server 2012+" : "ETW requires administrator rights");
            if (elevated) return;
            DialogResult choice;
            using (var dialog = new Form { Text = "Limited diagnostic permissions", Width = 700, Height = 180,
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false })
            {
                var label = new Label { Dock = DockStyle.Fill, Padding = new Padding(14), Text = "KernelTraceScout is running without administrator privileges. Kernel capture is disabled. Choose how to continue." };
                var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10) };
                var restart = new Button { Text = "Restart as administrator", AutoSize = true, DialogResult = DialogResult.Yes };
                var limited = new Button { Text = "Continue with limited functionality", AutoSize = true, DialogResult = DialogResult.No };
                var exit = new Button { Text = "Exit", AutoSize = true, DialogResult = DialogResult.Cancel };
                actions.Controls.Add(restart); actions.Controls.Add(limited); actions.Controls.Add(exit);
                dialog.Controls.Add(label); dialog.Controls.Add(actions); dialog.CancelButton = exit;
                choice = dialog.ShowDialog(this);
            }
            if (choice == DialogResult.Cancel) { Close(); return; }
            if (choice != DialogResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppFiles.DirectoryPath
                });
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Write("Elevated launch was cancelled or failed: " + ex);
                MessageBox.Show(this, "Administrator restart was not completed. Continuing in limited mode.",
                    "Limited diagnostic permissions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static DataGridView MakeGrid()
        {
            return new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
        }

        private void RefreshProcesses()
        {
            if (refreshing || closing.IsCancellationRequested) return;
            if (demoMode) { demoMode = false; processes.Rows.Clear(); stackEvidence.Rows.Clear(); filters.Rows.Clear(); details.Clear(); lastAnalysis = null; information.Text = "Live measurements - demo ended."; }
            activeRefresh = RefreshProcessesAsync();
        }

        private async Task RefreshProcessesAsync()
        {
            if (refreshing) { if (activeRefresh != null) await activeRefresh; return; }
            if (closing.IsCancellationRequested) return;
            refreshing = true; manualRefresh.Enabled = false;
            try
            {
                var limits = new AppSettings { KernelThresholdPercent = settings.KernelThresholdPercent, MinimumKernelCpuPercent = settings.MinimumKernelCpuPercent,
                    MinimumKernelMilliseconds = settings.MinimumKernelMilliseconds, MinimumKernelUserRatio = settings.MinimumKernelUserRatio,
                    MinimumValidSamples = settings.MinimumValidSamples, MinimumObservationSeconds = settings.MinimumObservationSeconds, StrongSuspiciousSamples = settings.StrongSuspiciousSamples };
                var rows = await Task.Run(() => monitor.Read(limits));
                if (closing.IsCancellationRequested) return;
                currentRows = rows;
                processes.Rows.Clear();
                int ownPid;
                using (var self = Process.GetCurrentProcess()) ownPid = self.Id;
                foreach (var r in rows)
                {
                    bool suspect = r.Status.StartsWith("Suspicious", StringComparison.Ordinal) || r.Status.StartsWith("Highly suspicious", StringComparison.Ordinal);
                    DateTime capturedStart;
                    if ((trace.Running || captureCompleting) && suspect && captureIdentities.TryGetValue(r.Pid, out capturedStart) && capturedStart == r.Start)
                        captureSuspicious.Add(r.Pid);
                    if (r.Pid == 4 || r.Pid == ownPid) continue;
                    string ratio = !r.Valid ? "N/A" : double.IsPositiveInfinity(r.Ratio) ? "no user CPU" : F(r.Ratio) + " : 1";
                    int index = processes.Rows.Add(r.Name, r.Pid, r.Valid ? F(r.Cpu) : "N/A", r.Valid ? F(r.Kernel) : "N/A",
                        r.Valid ? F2(r.KernelCores) : "N/A", r.Valid ? F(r.KernelMilliseconds) + " ms / " + F(r.Elapsed) + " s" : "N/A",
                        r.Valid ? F(r.User) : "N/A", ratio, r.Status, GetStackEvidenceSummary(r.Pid));
                    if (suspect) { processes.Rows[index].DefaultCellStyle.ForeColor = Color.DarkRed; processes.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose; }
                }
                if (!trace.Running && !analyzing && !captureCompleting)
                    information.Text = "Sorted by kernel CPU time. Suspicious at " + F(settings.MinimumKernelCpuPercent) + "% total capacity, at least " + F(settings.MinimumKernelMilliseconds) + " ms per interval, " + settings.KernelThresholdPercent + "% kernel share, ratio " + F(settings.MinimumKernelUserRatio) + ":1 for " + settings.MinimumObservationSeconds + " s.\r\nSystem (PID 4) and KernelTraceScout are excluded. These limits are heuristics, not Windows standards.";
            }
            catch (Exception ex) { ShowError("Process refresh failed", ex); }
            finally { refreshing = false; manualRefresh.Enabled = !closing.IsCancellationRequested; }
        }

        private string GetStackEvidenceSummary(int pid)
        {
            if (lastAnalysis == null) return "No ETW analysis yet";
            DateTime identity;
            var row = currentRows.FirstOrDefault(r => r.Pid == pid);
            if (row == null || !captureIdentities.TryGetValue(pid, out identity) || identity != row.Start) return "No matching process identity in capture";
            var best = lastAnalysis.Evidence.Where(e => e.Pid == pid).OrderByDescending(e => e.LeafCount).ThenByDescending(e => e.SampleCount).FirstOrDefault();
            if (best == null) return "No correlated kernel driver samples";
            string moduleName = Path.GetFileName(best.ModuleName);
            string minifilterMatch = loadedMinifilterNames.Contains(Path.GetFileNameWithoutExtension(moduleName)) ? " | Name matches a loaded minifilter" : "";
            if (best.LeafCount > 0)
                return moduleName + minifilterMatch + " | " + best.LeafCount + " CPU samples directly in module; " + best.SampleCount + " stack samples";
            return moduleName + minifilterMatch + " | seen only in kernel stacks; no direct CPU sample in module";
        }

        private void DisplayStackEvidence()
        {
            stackEvidence.Rows.Clear(); details.Clear();
            if (lastAnalysis == null) return;
            foreach (var driver in lastAnalysis.Drivers)
            {
                int index = stackEvidence.Rows.Add(Path.GetFileName(driver.Name), driver.DpcMilliseconds.ToString("F3", CultureInfo.CurrentCulture), driver.IsrMilliseconds.ToString("F3", CultureInfo.CurrentCulture), driver.LongDpcCount, driver.LongIsrCount, driver.Samples, driver.Level);
                stackEvidence.Rows[index].Tag = driver;
                if (driver.LongDpcCount > 0 || driver.LongIsrCount > 0 || driver.Level == "Moderate stack clue" || driver.Level == "High")
                {
                    stackEvidence.Rows[index].DefaultCellStyle.ForeColor = Color.DarkRed;
                    stackEvidence.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose;
                }
            }
            foreach (DataGridViewRow row in processes.Rows)
                    row.Cells[9].Value = GetStackEvidenceSummary(Convert.ToInt32(row.Cells[1].Value, CultureInfo.InvariantCulture));
            DisplayDriverDetails();
            if (lastAnalysis.Drivers.Count == 0) details.Text = "No usable DPC/ISR driver time or kernel stack clues. Cause unknown. " + lastAnalysis.Quality;
        }

        private void DisplayDriverDetails()
        {
            if (stackEvidence.CurrentRow == null) return;
            var driver = stackEvidence.CurrentRow.Tag as NativeEtwAnalyzer.RankedDriver;
            if (driver == null) return;
            int ownPid;
            using (var self = Process.GetCurrentProcess()) ownPid = self.Id;
            var text = new StringBuilder(driver.Explanation).AppendLine().AppendLine();
            foreach (var e in driver.Processes.OrderByDescending(e => e.SampleCount))
            {
                var process = currentRows.FirstOrDefault(r => r.Pid == e.Pid);
                DateTime identity;
                string name = e.Pid == 4 ? "System kernel context (not attributed to a user process)" : e.Pid == ownPid ? "KernelTraceScout measurement overhead" :
                    process != null && captureIdentities.TryGetValue(e.Pid, out identity) && identity == process.Start ? process.Name : "recorded process (identity unavailable)";
                text.AppendLine("Kernel stack | " + name + " | PID " + e.Pid + " | Samples " + e.SampleCount + " | share in this process " + F(e.ShareOfProcessStacks) + "% | instruction hits " + e.LeafCount + " | time buckets " + e.TimeBuckets);
                text.AppendLine("Example kernel frames (leaf toward callers): " + e.ExampleStack);
                if (text.Length > 60000) { text.AppendLine("Display limit reached; see report."); break; }
            }
            text.AppendLine();
            text.AppendLine("Data quality: " + lastAnalysis.Quality);
            details.Text = text.ToString();
        }

        private static string F(double number) { return number.ToString("F1", CultureInfo.CurrentCulture); }
        private static string F2(double number) { return number.ToString("F2", CultureInfo.CurrentCulture); }

        private async void RefreshFilters()
        {
            if (!refresh.Enabled || closing.IsCancellationRequested) return;
            if (demoMode) { demoMode = false; processes.Rows.Clear(); stackEvidence.Rows.Clear(); filters.Rows.Clear(); details.Clear(); lastAnalysis = null; information.Text = "Live measurements - demo ended."; }
            refresh.Enabled = false;
            try
            {
                var list = await Task.Run(() => NativeFilters.Enumerate());
                if (closing.IsCancellationRequested) return;
                filters.Rows.Clear();
                loadedMinifilterNames.Clear();
                foreach (var f in list)
                {
                    loadedMinifilterNames.Add(f.Name);
                    filters.Rows.Add(f.Name, f.Instances, f.Frame, "Loaded; no activity or causality proven");
                }
            }
            catch (Exception ex)
            {
                if (closing.IsCancellationRequested) return;
                AppLog.Write("Minifilter enumeration failed: " + ex);
                filters.Rows.Clear();
                filters.Rows.Add("Enumeration unavailable", null, null, ex.Message + "; no installed filter list can be inferred.");
            }
            finally { if (!IsDisposed) refresh.Enabled = true; }
        }

        private void ExportCsv(DataGridView grid)
        {
            using (var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "KernelTraceScout_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var file = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
                    {
                        file.WriteLine(string.Join(";", grid.Columns.Cast<DataGridViewColumn>().Select(c => Quote(c.HeaderText))));
                        foreach (DataGridViewRow row in grid.Rows)
                            file.WriteLine(string.Join(";", row.Cells.Cast<DataGridViewCell>().Select(c => Quote(c.Value))));
                    }
                    AppLog.Write("CSV exported: " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    AppLog.Write("CSV export failed: " + ex);
                    MessageBox.Show(this, ex.Message, "CSV export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string Quote(object value)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (text.Length > 0 && "=+-@\t\r".IndexOf(text[0]) >= 0) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
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
