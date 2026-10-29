using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
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

    [Serializable] public sealed class ResultRow
    {
        public List<string> Cells { get; set; } = new List<string>();
        public int Foreground { get; set; }
        public int Background { get; set; }
    }
    [Serializable] public sealed class ResultTable
    {
        public List<ResultRow> Rows { get; set; } = new List<ResultRow>();
    }
    [Serializable] public sealed class ResultSnapshot
    {
        public int FormatVersion { get; set; }
        public DateTime SavedUtc { get; set; }
        public string Status { get; set; }
        public ResultTable Processes { get; set; }
        public ResultTable Minifilters { get; set; }
        public ResultTable Drivers { get; set; }
        public List<string> DriverDetails { get; set; } = new List<string>();
    }

    internal sealed class MainWindow : Form
    {
        private readonly DataGridView processes = MakeGrid();
        private readonly DataGridView filters = MakeGrid();
        private readonly DataGridView stackEvidence = MakeGrid();
        private NativeEtwAnalyzer.AnalysisResult lastAnalysis;
        private readonly Label information = new Label { Dock = DockStyle.Fill, Height = 58, AutoSize = false,
            Text = "Kernel time measures CPU usage in kernel mode. ETW distinguishes direct DPC/ISR driver time from statistical stack evidence.",
            TextAlign = ContentAlignment.MiddleLeft };
        private readonly ProgressBar analysisProgress = new ProgressBar { Dock = DockStyle.Bottom, Height = 14,
            Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 1000 };
        private readonly Label progressLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomRight,
            Text = "", AutoEllipsis = true };
        private readonly Panel progressPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        private readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer { Interval = 200 };
        private readonly Stopwatch progressWatch = new Stopwatch();
        private bool progressAnalyzing;
        private int captureDuration;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly Button manualRefresh = new Button { Text = "Refresh now", AutoSize = true };
        private readonly Button traceButton = new Button { Text = "Start ETW capture", AutoSize = true, Enabled = false };
        private readonly NativeEtwCapture trace = new NativeEtwCapture();
        private readonly Button autoRefresh = new Button { Text = "Start auto-refresh", AutoSize = true };
        private readonly NumericUpDown refreshSeconds = new NumericUpDown { Minimum = 1, Maximum = 300, Value = 2, Width = 56 };
        private readonly ProcessMonitor monitor = new ProcessMonitor();
        private List<ProcessRow> currentRows = new List<ProcessRow>();
        private readonly TextBox details = new TextBox { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly System.Windows.Forms.Timer captureTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly Dictionary<int, DateTime> captureIdentities = new Dictionary<int, DateTime>();
        private readonly HashSet<int> captureSuspicious = new HashSet<int>();
        private readonly Dictionary<int, ProcessRow> capturePeakByPid = new Dictionary<int, ProcessRow>();
        private readonly HashSet<string> loadedMinifilterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool refreshing, analyzing, captureCompleting, closingAllowed, starting, shuttingDown;
        private Task activeRefresh;
        private Task activeCapture;
        private bool elevated;
        private readonly NumericUpDown captureSeconds = new NumericUpDown { Minimum = 5, Maximum = 120, Width = 55 };
        private readonly Button refresh = new Button { Text = "Refresh minifilters", AutoSize = true };
        private readonly Button export = new Button { Text = "Export CSV", AutoSize = true };
        private readonly Button selfTest = new Button { Text = "Run self-test", AutoSize = true };
        private readonly Button demoButton = new Button { Text = "Demo", AutoSize = true };
        private const string WindowCaption = "KernelTraceScout - Kernel CPU Analysis";
        private readonly System.Windows.Forms.Timer demoTitleTimer = new System.Windows.Forms.Timer { Interval = 650 };
        private bool demoTitleVisible;
        private bool demoActive;
        private bool demoWasViewingSavedResults;
        private string demoPreviousInformation;
        private string demoPreviousDetails;
        private NativeEtwAnalyzer.AnalysisResult demoPreviousAnalysis;
        private List<ProcessRow> demoPreviousRows;
        private readonly Dictionary<int, string> demoPreviousDriverDetails = new Dictionary<int, string>();
        private readonly Button saveResults = new Button { Text = "Save results", AutoSize = true };
        private readonly Button loadResults = new Button { Text = "Load results", AutoSize = true };
        private readonly Button aboutButton = new Button { Text = "?", Width = 28, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        private readonly ToolTip buttonHints = new ToolTip { AutoPopDelay = 12000, InitialDelay = 450, ReshowDelay = 150, ShowAlways = true };
        private bool viewingSavedResults;
        private readonly Dictionary<int, string> savedDriverDetails = new Dictionary<int, string>();
        private readonly NumericUpDown threshold = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 5, Width = 55 };
        private readonly AppSettings settings;


        internal MainWindow()
        {
            settings = SettingsStore.Load();
            Text = WindowCaption;
            ShowIcon = true;
            ApplyApplicationIcon();
            Width = 1250;
            Height = 780;
            MinimumSize = new Size(900, 550);
            // Allow the buttons to wrap into the available width instead of scrolling
            // prematurely. The progress display has its own row and reserves no
            // horizontal space while the action buttons are being laid out.
            var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 145,
                Padding = new Padding(8, 6, 8, 4), ColumnCount = 2, RowCount = 3, AutoSize = false };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true,
                AutoScroll = false, Margin = Padding.Empty };
            actions.Controls.Add(traceButton);
            actions.Controls.Add(manualRefresh);
            actions.Controls.Add(autoRefresh);
            actions.Controls.Add(refresh);
            actions.Controls.Add(export);
            actions.Controls.Add(selfTest);
            actions.Controls.Add(saveResults);
            actions.Controls.Add(loadResults);
            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, Margin = Padding.Empty };
            options.Controls.Add(new Label { Text = "Capture duration (s):", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            captureSeconds.Value = settings.CaptureSeconds;
            options.Controls.Add(captureSeconds);
            options.Controls.Add(new Label { Text = "Refresh interval (s):", AutoSize = true, Margin = new Padding(12, 7, 3, 3) });
            refreshSeconds.Value = settings.RefreshIntervalSeconds;
            timer.Interval = settings.RefreshIntervalSeconds * 1000;
            options.Controls.Add(refreshSeconds);
            options.Controls.Add(new Label { Text = "Kernel CPU threshold (% system capacity):", AutoSize = true, Margin = new Padding(12, 7, 3, 3) });
            threshold.Value = (decimal)settings.MinimumKernelCpuPercent;
            options.Controls.Add(threshold);
            toolbar.Controls.Add(actions, 0, 0);
            toolbar.Controls.Add(options, 0, 1);
            progressPanel.Controls.Add(progressLabel);
            progressPanel.Controls.Add(analysisProgress);
            progressPanel.Dock = DockStyle.Right;
            progressPanel.Width = 280;
            toolbar.Controls.Add(progressPanel, 0, 2);
            var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = false,
                WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
                Margin = Padding.Empty, Padding = Padding.Empty };
            demoButton.Margin = new Padding(0, 0, 5, 0);
            aboutButton.Anchor = AnchorStyles.None;
            rightButtons.Controls.Add(demoButton);
            rightButtons.Controls.Add(aboutButton);
            toolbar.Controls.Add(rightButtons, 1, 0);
            aboutButton.Click += (sender, e) => ShowAbout();
            demoTitleTimer.Tick += (sender, e) =>
            {
                demoTitleVisible = !demoTitleVisible;
                Text = WindowCaption + (demoTitleVisible ? " - Demo-Mode" : "");
            };
            buttonHints.SetToolTip(traceButton, "Record a short Windows kernel trace and check which drivers use CPU time. Requires administrator rights.");
            buttonHints.SetToolTip(manualRefresh, "Update the process CPU measurements once.");
            buttonHints.SetToolTip(autoRefresh, "Start or stop automatic updates of the process CPU measurements.");
            buttonHints.SetToolTip(refresh, "Update the list of file-system minifilter drivers installed on this computer.");
            buttonHints.SetToolTip(export, "Export the current table data to a CSV file.");
            buttonHints.SetToolTip(selfTest, "Run a built-in check using simulated data. This does not test your real drivers.");
            buttonHints.SetToolTip(demoButton, "Show an example where a fictional antivirus driver increases kernel CPU load in two processes. No real measurements are used.");
            buttonHints.SetToolTip(saveResults, "Save the currently displayed results to a file so you can review them later.");
            buttonHints.SetToolTip(loadResults, "Open a previously saved set of results. Saved results are not live measurements.");
            buttonHints.SetToolTip(aboutButton, "Show the version, author, license and project website.");
            buttonHints.SetToolTip(captureSeconds, "Set how many seconds an ETW recording lasts.");
            buttonHints.SetToolTip(refreshSeconds, "Set the time between automatic process updates, in seconds.");
            buttonHints.SetToolTip(threshold, "Set the minimum kernel CPU percentage used when assessing processes.");
            captureSeconds.ValueChanged += (sender, e) => { settings.CaptureSeconds = (int)captureSeconds.Value; SaveSettings(); };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var ptab = new TabPage("Processes");
            var ftab = new TabPage("File System Minifilters");
            var etab = new TabPage("Driver / Stack Analysis");
            ptab.Controls.Add(processes);
            ftab.Controls.Add(filters);
            etab.Controls.Add(stackEvidence);
            etab.Controls.Add(details);
            tabs.TabPages.Add(ptab);
            tabs.TabPages.Add(ftab);
            tabs.TabPages.Add(etab);
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(0) };
            bottomPanel.Controls.Add(information);

            Controls.Add(tabs);
            Controls.Add(bottomPanel);
            Controls.Add(toolbar);
            processes.Columns.Add("name", "Process");
            processes.Columns.Add("pid", "PID");
            processes.Columns.Add("cpu", "Total CPU % system");
            processes.Columns.Add("kernel", "Kernel CPU % system");
            processes.Columns.Add("cores", "Kernel core equivalent");
            processes.Columns.Add("kernelms", "Kernel time / interval");
            processes.Columns.Add("user", "User-CPU %");
            processes.Columns.Add("ratio", "Kernel : User");
            processes.Columns.Add("status", "Assessment / reason");
            processes.Columns.Add("cause", "ETW driver evidence per process");
            stackEvidence.Columns.Add("module", "Driver / kernel module");
            stackEvidence.Columns.Add("dpc", "DPC time (ms)");
            stackEvidence.Columns.Add("isr", "ISR time (ms)");
            stackEvidence.Columns.Add("longdpc", "DPC > 100 µs");
            stackEvidence.Columns.Add("longisr", "ISR > 25 µs");
            stackEvidence.Columns.Add("samples", "Kernel stack samples");
            stackEvidence.Columns.Add("level", "Evidence");
            stackEvidence.Columns[0].Width = 350;
            stackEvidence.SelectionChanged += (sender, e) => DisplayDriverDetails();
            ConfigureCopyMenu(processes);
            ConfigureCopyMenu(filters);
            ConfigureCopyMenu(stackEvidence);
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
            selfTest.Click += (sender, e) =>
            {
                try
                {
                    string result = LocalSelfTest.Run();
                    MessageBox.Show(this, result, "Synthetic self-test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Local self-test failed: " + ex);
                    MessageBox.Show(this, ex.Message, "Synthetic self-test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            saveResults.Click += (sender, e) => SaveResults();
            loadResults.Click += (sender, e) => LoadResults();
            demoButton.Click += (sender, e) => ShowDemo();
            refresh.Click += (sender, e) => { LeaveSavedView(); RefreshFilters(); };
            export.Click += (sender, e) => ExportCsv(tabs.SelectedIndex == 0 ? processes : tabs.SelectedIndex == 1 ? filters : stackEvidence);
            threshold.ValueChanged += (sender, e) =>
            {
                settings.MinimumKernelCpuPercent = (double)threshold.Value;
                SaveSettings();
            };
            // SEMANTIC ANCHOR: Auto-refresh is opt-in; one manual refresh makes one measurement.
            // Attribution remains unknown without ETW kernel stack evidence.
            manualRefresh.Click += (sender, e) => { LeaveSavedView(); RefreshProcesses(); };
            autoRefresh.Click += (sender, e) =>
            {
                LeaveSavedView();
                if (timer.Enabled)
                {
                    timer.Stop();
                    autoRefresh.Text = "Start auto-refresh";
                }
                else
                {
                    RefreshProcesses();
                    timer.Start();
                    autoRefresh.Text = "Stop auto-refresh";
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
                LeaveSavedView();
                if (analyzing || captureCompleting || starting || closing.IsCancellationRequested) return;
                activeCapture = ToggleCapture();
            };
            trace.Completed += (file, error) =>
            {
                if (IsDisposed || closing.IsCancellationRequested) return;
                try { BeginInvoke((Action)(() => { if (!closing.IsCancellationRequested && !captureCompleting && !analyzing) activeCapture = FinishCapture(file, error, tabs); })); }
                catch (InvalidOperationException) { }
            };
            progressTimer.Tick += (sender, e) => UpdateCaptureProgress();
            captureTimer.Tick += (sender, e) => RefreshProcesses();
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
                timer.Stop(); captureTimer.Stop(); progressTimer.Stop(); demoTitleTimer.Stop(); progressWatch.Stop(); closing.Cancel();
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
                if (trace.Running) { await Task.Run(() => trace.Complete()); return; }
                await RefreshProcessesAsync();
                if (closing.IsCancellationRequested) return;
                captureIdentities.Clear(); captureSuspicious.Clear(); capturePeakByPid.Clear();
                foreach (var row in currentRows.Where(r => r.Start != default(DateTime))) captureIdentities[row.Pid] = row.Start;
                lastAnalysis = null; DisplayStackEvidence();
                string file = Path.Combine(AppFiles.LogDirectory, "KernelTraceScout_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".etl");
                int seconds = settings.CaptureSeconds, mb = settings.MaximumTraceMegabytes;
                await Task.Run(() => trace.Start(file, seconds, mb));
                if (closing.IsCancellationRequested) { await Task.Run(() => trace.Dispose()); return; }
                traceButton.Text = "Stop ETW capture"; captureTimer.Start();
                captureDuration = seconds;
                progressAnalyzing = false;
                progressWatch.Restart();
                progressPanel.Visible = true;
                analysisProgress.Style = ProgressBarStyle.Continuous;
                analysisProgress.Value = 0;
                UpdateCaptureProgress();
                progressTimer.Start();
                information.Text = "Kernel CPU, DPC and ISR events are being recorded for up to " + seconds + " seconds. File limit: " + mb + " MiB.";
            }
            catch (Exception ex)
            {
                if (!trace.Running) HideCaptureProgress();
                ShowError("ETW capture failed", ex);
            }
            finally { starting = false; traceButton.Enabled = elevated && !analyzing && !captureCompleting && !closing.IsCancellationRequested; }
        }

        private void ShowError(string title, Exception ex)
        {
            AppLog.Write(title + ": " + ex);
            information.Text = title + ": " + ex.Message;
            if (!closing.IsCancellationRequested) MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private async Task FinishCapture(string file, Exception error, TabControl tabs)
        {
            captureCompleting = true; captureTimer.Stop(); traceButton.Enabled = false;
            if (progressPanel.Visible)
            {
                progressTimer.Stop();
                progressLabel.Text = "Preparing analysis...";
                analysisProgress.Style = ProgressBarStyle.Marquee;
                analysisProgress.MarqueeAnimationSpeed = 24;
            }
            try
            {
                if (error != null) throw error;
                AppLog.Write("ETW capture completed: " + Path.GetFileName(file));
                await RefreshProcessesAsync();
                analyzing = true;
                var suspicious = new HashSet<int>(captureSuspicious);
                SetAnalysisProgress(true, "Analyzing ETW capture...");
                lastAnalysis = await Task.Run(() => NativeEtwAnalyzer.Analyze(file, suspicious, trace.EventsLost, closing.Token));
                AppLog.Write("ETW analysis completed: " + lastAnalysis.Quality);
                AppLog.Write("ETW analysis summary: file=" + Path.GetFileName(file) +
                    "; CPU samples=" + lastAnalysis.ProfileCount +
                    "; correlated stack observations=" + lastAnalysis.StackCount +
                    "; DPC events=" + lastAnalysis.DpcEventCount +
                    "; ISR events=" + lastAnalysis.IsrEventCount +
                    "; unmapped interrupts=" + lastAnalysis.UnmappedInterruptCount +
                    "; lost events/buffers=" + lastAnalysis.Lost +
                    "; metadata decode warnings=" + lastAnalysis.MetadataDecodeWarnings +
                    "; driver modules=" + lastAnalysis.Drivers.Count +
                    "; report=" + lastAnalysis.ReportPath);
                if (closing.IsCancellationRequested) return;
                DisplayStackEvidence();
                SetAnalysisProgress(false, "ETW analysis complete. The Processes tab shows the strongest driver evidence per process; Driver / Stack Analysis shows details. Stack evidence is statistical and does not establish causality.");
                tabs.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                SetAnalysisProgress(false, information.Text);
                ShowError("ETW analysis failed", ex);
            }
            finally
            {
                SetAnalysisProgress(false, information.Text);
                HideCaptureProgress();
                analyzing = false; captureCompleting = false; traceButton.Text = trace.Running ? "Stop ETW capture" : "Start ETW capture"; traceButton.Enabled = elevated && !closing.IsCancellationRequested;
            }
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
            traceButton.Enabled = elevated && Environment.Is64BitProcess && Environment.OSVersion.Version >= new Version(6, 2);
            if (!traceButton.Enabled) traceButton.Text = elevated ? "ETW requires Windows 8 / Server 2012+" : "ETW requires administrator";
            if (elevated) return;
            DialogResult choice = MessageBox.Show(this,
                "KernelTraceScout is running without administrator privileges. ETW capture is disabled in this mode.\r\n\r\nYes = restart as administrator\r\nNo = continue with limited functionality\r\nCancel = exit",
                "Limited diagnostic permissions",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
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

        private void ShowAbout()
        {
            using (var dialog = new Form())
            {
                dialog.Text = "About KernelTraceScout";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(510, 285);
                dialog.Font = Font;

                var content = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(18, 14, 18, 8),
                    ColumnCount = 1,
                    RowCount = 7
                };
                content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
                content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                var title = new Label { Text = "KernelTraceScout  |  Version 0.9.0.0", Dock = DockStyle.Fill,
                    Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
                content.Controls.Add(title, 0, 0);
                content.Controls.Add(new Label { Text = "Author: Daniel Capilla", Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
                content.Controls.Add(new Label { Text = "Copyright © 2026 Daniel Capilla", Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
                content.Controls.Add(new Label { Text = "License: GNU General Public License v3.0 (GPL-3.0-only)",
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
                content.Controls.Add(MakeAboutLink("Read the GPL v3 license", "https://www.gnu.org/licenses/gpl-3.0.html"), 0, 4);
                content.Controls.Add(new Label { Text = "Third-party packages: None", Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
                content.Controls.Add(MakeAboutLink("GitHub: github.com/UncleRiot/KernelTraceScout",
                    "https://github.com/UncleRiot/KernelTraceScout"), 0, 6);

                var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK,
                    Anchor = AnchorStyles.Top | AnchorStyles.Right, Size = new Size(88, 28) };
                footer.Resize += (sender, args) => ok.Location = new Point(footer.ClientSize.Width - ok.Width - 12, 9);
                footer.Controls.Add(ok);
                dialog.AcceptButton = ok;
                dialog.CancelButton = ok;
                dialog.Controls.Add(content);
                dialog.Controls.Add(footer);
                dialog.ShowDialog(this);
            }
        }

        private static LinkLabel MakeAboutLink(string caption, string address)
        {
            var link = new LinkLabel { Text = caption, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, LinkBehavior = LinkBehavior.HoverUnderline };
            link.LinkClicked += (sender, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("The link could not be opened: " + ex.Message,
                        "Open link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            return link;
        }

        private void ApplyApplicationIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) Icon = icon;
            }
            catch (Exception ex)
            {
                AppLog.Write("Application icon could not be loaded: " + ex.Message);
            }
        }

        private void SetAnalysisProgress(bool visible, string statusText)
        {
            information.Text = statusText;
            if (!visible) return;
            progressAnalyzing = true;
            progressWatch.Restart();
            progressPanel.Visible = true;
            analysisProgress.Style = ProgressBarStyle.Marquee;
            analysisProgress.MarqueeAnimationSpeed = 24;
            UpdateCaptureProgress();
            progressTimer.Start();
        }

        private void UpdateCaptureProgress()
        {
            if (!progressPanel.Visible) return;
            int elapsed = (int)progressWatch.Elapsed.TotalSeconds;
            if (progressAnalyzing)
            {
                progressLabel.Text = "Analyzing ETW: " + elapsed.ToString(CultureInfo.InvariantCulture) + " s elapsed";
            }
            else
            {
                int seconds = Math.Min(elapsed, captureDuration);
                progressLabel.Text = "ETW capture: " + seconds.ToString(CultureInfo.InvariantCulture) + " / " + captureDuration.ToString(CultureInfo.InvariantCulture) + " s";
                analysisProgress.Value = Math.Min(1000, (int)(progressWatch.Elapsed.TotalSeconds * 1000 / Math.Max(1, captureDuration)));
            }
        }

        private void HideCaptureProgress()
        {
            progressTimer.Stop();
            progressWatch.Stop();
            progressPanel.Visible = false;
            progressAnalyzing = false;
            analysisProgress.Style = ProgressBarStyle.Continuous;
            analysisProgress.Value = 0;
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
            if (refreshing || closing.IsCancellationRequested || viewingSavedResults) return;
            activeRefresh = RefreshProcessesAsync();
        }

        private async Task RefreshProcessesAsync()
        {
            if (refreshing) { if (activeRefresh != null) await activeRefresh; return; }
            if (closing.IsCancellationRequested || viewingSavedResults) return;
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
                    bool suspect = r.Status.StartsWith("Suspicious", StringComparison.Ordinal) || r.Status.StartsWith("Strongly suspicious", StringComparison.Ordinal);
                    DateTime capturedStart;
                    if ((trace.Running || captureCompleting) && r.Valid &&
                        captureIdentities.TryGetValue(r.Pid, out capturedStart) && capturedStart == r.Start)
                    {
                        ProcessRow prior;
                        if (!capturePeakByPid.TryGetValue(r.Pid, out prior) || r.Kernel > prior.Kernel)
                            capturePeakByPid[r.Pid] = r;
                        if (suspect) captureSuspicious.Add(r.Pid);
                    }
                    if (r.Pid == 4 || r.Pid == ownPid) continue;
                    string ratio = !r.Valid ? "N/A" : double.IsPositiveInfinity(r.Ratio) ? "no user CPU" : F(r.Ratio) + " : 1";
                    int index = processes.Rows.Add(r.Name, r.Pid, r.Valid ? F(r.Cpu) : "N/A", r.Valid ? F(r.Kernel) : "N/A",
                        r.Valid ? F2(r.KernelCores) : "N/A", r.Valid ? F(r.KernelMilliseconds) + " ms / " + F(r.Elapsed) + " s" : "N/A",
                        r.Valid ? F(r.User) : "N/A", ratio, r.Status, GetStackEvidenceSummary(r.Pid));
                    ApplyProcessHighlight(processes.Rows[index], r, suspect);
                }
                if (!trace.Running && !analyzing && !captureCompleting)
                    information.Text = "Sorted by kernel time. Suspicious at " + F(settings.MinimumKernelCpuPercent) + "% of system capacity, at least " + F(settings.MinimumKernelMilliseconds) + " ms per interval, kernel share " + settings.KernelThresholdPercent + "% and ratio " + F(settings.MinimumKernelUserRatio) + ":1 for at least " + settings.MinimumObservationSeconds + " s.\r\nSystem (PID 4) and KernelTraceScout are excluded from process ranking. Thresholds are heuristic, not Windows defaults.";
            }
            catch (Exception ex) { ShowError("Process refresh failed", ex); }
            finally { refreshing = false; manualRefresh.Enabled = !closing.IsCancellationRequested; }
        }

        // Red requires a suspicious process, matching capture identity and repeated
        // direct instruction samples. Unattributed invalid records remain reported
        // globally, but cannot invalidate already decoded, matched CPU samples.
        // Actual ETW loss or metadata decode warnings still prevent red attribution.
        private bool HasUsableEtwEvidence()
        {
            return lastAnalysis != null && lastAnalysis.Lost == 0 &&
                !lastAnalysis.MetadataDecodeWarnings;
        }

        private bool MatchesCapturedProcess(ProcessRow process)
        {
            DateTime start;
            return process != null && process.Valid &&
                captureIdentities.TryGetValue(process.Pid, out start) && start == process.Start;
        }

        private bool IsStrongProcessDriverLink(ProcessRow process, NativeEtwAnalyzer.ModuleEvidence evidence)
        {
            if (!HasUsableEtwEvidence() || !MatchesCapturedProcess(process) ||
                evidence == null || evidence.Pid != process.Pid) return false;
            // Judge each affected process independently using its own measurements
            // taken during capture, not an unrelated process or a later quiet snapshot.
            ProcessRow captured;
            if (!capturePeakByPid.TryGetValue(process.Pid, out captured) ||
                captured.Start != process.Start || !captureSuspicious.Contains(process.Pid)) return false;
            return captured.Kernel >= settings.MinimumKernelCpuPercent &&
                (double.IsPositiveInfinity(captured.Ratio) || captured.Ratio >= settings.MinimumKernelUserRatio) &&
                evidence.LeafCount >= 100 && evidence.TimeBuckets >= 10 &&
                evidence.ShareOfProcessCpuSamples >= 5.0;
        }

        private bool IsStrongDriverLink(NativeEtwAnalyzer.RankedDriver driver)
        {
            return driver != null && driver.Processes != null &&
                driver.Processes.Any(e => IsStrongProcessDriverLink(
                    currentRows.FirstOrDefault(r => r.Pid == e.Pid), e));
        }

        private void ApplyProcessHighlight(DataGridViewRow gridRow, ProcessRow process, bool suspicious)
        {
            bool strong = lastAnalysis != null && lastAnalysis.Drivers.Any(d =>
                d.Processes != null && d.Processes.Any(e => IsStrongProcessDriverLink(process, e)));
            if (strong)
            {
                gridRow.DefaultCellStyle.ForeColor = Color.DarkRed;
                gridRow.DefaultCellStyle.BackColor = Color.MistyRose;
            }
            else if (suspicious || (lastAnalysis != null && MatchesCapturedProcess(process) &&
                captureSuspicious.Contains(process.Pid)))
            {
                gridRow.DefaultCellStyle.ForeColor = Color.DarkGoldenrod;
                gridRow.DefaultCellStyle.BackColor = Color.LightYellow;
            }
        }

        private string GetStackEvidenceSummary(int pid)
        {
            if (lastAnalysis == null) return "No ETW analysis yet";
            DateTime identity;
            var row = currentRows.FirstOrDefault(r => r.Pid == pid);
            if (row == null || !captureIdentities.TryGetValue(pid, out identity) || identity != row.Start) return "No matching process identity in capture";
            var best = lastAnalysis.Evidence.Where(e => e.Pid == pid)
                .OrderByDescending(e => IsStrongProcessDriverLink(row, e) ? 1 : 0)
                .ThenByDescending(e => e.LeafCount > 0 ? 1 : 0)
                .ThenByDescending(e => e.ShareOfProcessCpuSamples)
                .ThenByDescending(e => e.LeafCount)
                .ThenByDescending(e => e.TimeBuckets)
                .ThenByDescending(e => e.SampleCount).FirstOrDefault();
            if (best == null) return "No correlated kernel driver evidence in samples";
            string moduleName = Path.GetFileName(best.ModuleName);
            string minifilterMatch = loadedMinifilterNames.Contains(Path.GetFileNameWithoutExtension(moduleName)) ? " | matches loaded minifilter name" : "";
            if (best.LeafCount > 0)
                return (IsStrongProcessDriverLink(row, best) ? "Probable driver involvement: " : "Driver evidence: ") + moduleName + minifilterMatch + " | CPU instruction in module: " + best.LeafCount + " samples (" + F(best.ShareOfProcessCpuSamples) + "% of correlated process samples); " + best.SampleCount + " stack samples; " + best.TimeBuckets + " time buckets; indication, not proven cause";
            return moduleName + minifilterMatch + " | kernel stack presence only; no direct CPU instruction sample in module";
        }

        private void DisplayStackEvidence()
        {
            stackEvidence.Rows.Clear(); details.Clear();
            if (lastAnalysis == null) return;
            foreach (var driver in lastAnalysis.Drivers)
            {
                bool linkedToSuspiciousProcess = IsStrongDriverLink(driver);
                string displayEvidence = linkedToSuspiciousProcess ? "Strong process correlation (not proof)" : driver.Level;
                int index = stackEvidence.Rows.Add(Path.GetFileName(driver.Name), driver.DpcMilliseconds.ToString("F3", CultureInfo.CurrentCulture), driver.IsrMilliseconds.ToString("F3", CultureInfo.CurrentCulture), driver.LongDpcCount, driver.LongIsrCount, driver.Samples, displayEvidence);
                stackEvidence.Rows[index].Tag = driver;
                // Red requires high suspicion with usable trace evidence, not merely
                // a DPC over a fixed threshold or high activity on a healthy machine.
                bool highConfidence = linkedToSuspiciousProcess;
                bool needsReview = driver.LongDpcCount > 0 || driver.LongIsrCount > 0 ||
                    driver.Level == "Moderate stack evidence" || driver.Level == "Moderate CPU sample evidence" ||
                    driver.Level.StartsWith("Incomplete", StringComparison.OrdinalIgnoreCase) ||
                    driver.Level == "High" || driver.Level == "Moderate CPU sample evidence";
                if (highConfidence)
                {
                    stackEvidence.Rows[index].DefaultCellStyle.ForeColor = Color.DarkRed;
                    stackEvidence.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose;
                }
                else if (needsReview)
                {
                    stackEvidence.Rows[index].DefaultCellStyle.ForeColor = Color.DarkGoldenrod;
                    stackEvidence.Rows[index].DefaultCellStyle.BackColor = Color.LightYellow;
                }
            }
            foreach (DataGridViewRow row in processes.Rows)
            {
                int pid = Convert.ToInt32(row.Cells[1].Value, CultureInfo.InvariantCulture);
                row.Cells[9].Value = GetStackEvidenceSummary(pid);
                var process = currentRows.FirstOrDefault(r => r.Pid == pid);
                if (process != null)
                {
                    bool suspicious = process.Status.StartsWith("Suspicious", StringComparison.Ordinal) ||
                        process.Status.StartsWith("Strongly suspicious", StringComparison.Ordinal);
                    ApplyProcessHighlight(row, process, suspicious);
                }
            }
            DisplayDriverDetails();
            if (lastAnalysis.Drivers.Count == 0) details.Text = "No DPC/ISR driver time or usable kernel stack evidence captured. Cause unknown. " + lastAnalysis.Quality;
        }

        private void DisplayDriverDetails()
        {
            if (stackEvidence.CurrentRow == null) return;
            if (viewingSavedResults)
            {
                string saved;
                details.Text = savedDriverDetails.TryGetValue(stackEvidence.CurrentRow.Index, out saved) ? saved : "No stored driver details.";
                return;
            }
            var driver = stackEvidence.CurrentRow.Tag as NativeEtwAnalyzer.RankedDriver;
            if (driver == null) return;
            details.Text = FormatDriverDetails(driver);
        }

        private string FormatDriverDetails(NativeEtwAnalyzer.RankedDriver driver)
        {
            if (driver == null || lastAnalysis == null) return "";
            int ownPid;
            using (var self = Process.GetCurrentProcess()) ownPid = self.Id;
            var text = new StringBuilder(driver.Explanation).AppendLine().AppendLine();
            foreach (var e in driver.Processes.OrderByDescending(e => e.SampleCount))
            {
                var process = currentRows.FirstOrDefault(r => r.Pid == e.Pid);
                DateTime identity;
                string name = e.Pid == 4 ? "System kernel context (no user-process attribution)" : e.Pid == ownPid ? "KernelTraceScout measurement overhead" :
                    process != null && captureIdentities.TryGetValue(e.Pid, out identity) && identity == process.Start ? process.Name : "captured process (identity unavailable)";
                text.AppendLine("Kernel stack | " + name + " | PID " + e.Pid + " | samples " + e.SampleCount + " | share within this process " + F(e.ShareOfProcessStacks) + "% | instruction hits " + e.LeafCount + " (" + F(e.ShareOfProcessCpuSamples) + "% of correlated process samples) | time buckets " + e.TimeBuckets);
                text.AppendLine("Example kernel frames (leaf toward callers): " + e.ExampleStack);
                if (text.Length > 60000) { text.AppendLine("Display limit reached; see report."); break; }
            }
            text.AppendLine();
            text.AppendLine("Data quality: " + lastAnalysis.Quality);
            return text.ToString();
        }

        private static string F(double number) { return number.ToString("F1", CultureInfo.CurrentCulture); }
        private static string F2(double number) { return number.ToString("F2", CultureInfo.CurrentCulture); }

        private async void RefreshFilters()
        {
            if (!refresh.Enabled || closing.IsCancellationRequested) return;
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
                    filters.Rows.Add(f.Name, f.Instances, f.Frame, "Loaded; activity or causality not established");
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

        // Snapshots persist rendered, read-only results, not ETW sessions or live process identities.
        private void LeaveSavedView()
        {
            if (demoActive) HideDemo();
            demoTitleTimer.Stop();
            demoTitleVisible = false;
            Text = WindowCaption;
            if (!viewingSavedResults) return;
            viewingSavedResults = false;
            savedDriverDetails.Clear();
            lastAnalysis = null;
            captureIdentities.Clear();
            captureSuspicious.Clear();
            capturePeakByPid.Clear();
            details.Clear();
            information.Text = "Saved results closed. Refresh or capture to collect live measurements.";
        }

        private static IEnumerable<DataGridViewColumn> OrderedColumns(DataGridView grid)
        {
            return grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex);
        }

        private static string CellText(DataGridViewCell cell)
        {
            return Convert.ToString(cell.FormattedValue, CultureInfo.CurrentCulture) ?? "";
        }

        private static string ClipboardText(DataGridView grid, bool selectedOnly, bool csv)
        {
            var columns = OrderedColumns(grid).ToArray();
            var rows = grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && (!selectedOnly || r.Selected)).ToArray();
            if (selectedOnly && rows.Length == 0 && grid.CurrentRow != null) rows = new[] { grid.CurrentRow };
            if (rows.Length == 0) return "";
            var matrix = new List<string[]> { columns.Select(c => c.HeaderText).ToArray() };
            matrix.AddRange(rows.Select(r => columns.Select(c => CellText(r.Cells[c.Index])).ToArray()));
            if (csv)
                return string.Join("\r\n", matrix.Select(r => string.Join(",", r.Select(CsvCell))));
            var widths = Enumerable.Range(0, columns.Length).Select(i => matrix.Max(r => CleanPlain(r[i]).Length)).ToArray();
            var result = new StringBuilder();
            for (int i = 0; i < matrix.Count; i++)
            {
                if (i == 1)
                    result.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));
                result.AppendLine(string.Join(" | ", matrix[i].Select((v, j) =>
                {
                    string text = CleanPlain(v);
                    return text.PadRight(widths[j]);
                })));
            }
            return result.ToString();
        }

        private static string CleanPlain(string value)
        {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }

        private static string CsvCell(string value)
        {
            value = value ?? "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private void ConfigureCopyMenu(DataGridView grid)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Copy selected row as text", null, (s, e) => CopyGrid(grid, true, false));
            menu.Items.Add("Copy selected row as CSV (comma-separated)", null, (s, e) => CopyGrid(grid, true, true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy entire table as text", null, (s, e) => CopyGrid(grid, false, false));
            menu.Items.Add("Copy entire table as CSV (comma-separated)", null, (s, e) => CopyGrid(grid, false, true));
            grid.ContextMenuStrip = menu;
            grid.CellMouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                if (!grid.Rows[e.RowIndex].Selected)
                {
                    grid.ClearSelection();
                    grid.Rows[e.RowIndex].Selected = true;
                    if (e.ColumnIndex >= 0) grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                }
            };
        }

        private void CopyGrid(DataGridView grid, bool selectedOnly, bool csv)
        {
            try
            {
                string value = ClipboardText(grid, selectedOnly, csv);
                if (value.Length > 0) Clipboard.SetText(value, TextDataFormat.UnicodeText);
            }
            catch (Exception ex) { ShowError("Copy failed", ex); }
        }

        private static ResultTable CaptureTable(DataGridView grid)
        {
            var result = new ResultTable();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                var entry = new ResultRow { Foreground = row.DefaultCellStyle.ForeColor.IsEmpty ? 0 : row.DefaultCellStyle.ForeColor.ToArgb(), Background = row.DefaultCellStyle.BackColor.IsEmpty ? 0 : row.DefaultCellStyle.BackColor.ToArgb() };
                foreach (DataGridViewColumn col in grid.Columns)
                    entry.Cells.Add(CellText(row.Cells[col.Index]));
                result.Rows.Add(entry);
            }
            return result;
        }

        private static void RestoreTable(DataGridView grid, ResultTable table)
        {
            grid.Rows.Clear();
            if (table == null || table.Rows == null || table.Rows.Count > 20000) throw new InvalidDataException("Invalid result table.");
            foreach (ResultRow data in table.Rows)
            {
                if (data.Cells == null || data.Cells.Count != grid.Columns.Count) throw new InvalidDataException("Result column count mismatch.");
                int index = grid.Rows.Add(data.Cells.Cast<object>().ToArray());
                if (data.Foreground != 0) grid.Rows[index].DefaultCellStyle.ForeColor = Color.FromArgb(data.Foreground);
                if (data.Background != 0) grid.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(data.Background);
            }
            grid.ClearSelection();
        }

        // Keep existing rows as a snapshot; add fictional examples without modifying the ETW data.
        private void ShowDemo()
        {
            if (refreshing || analyzing || captureCompleting || starting || trace.Running)
            {
                MessageBox.Show(this, "Finish the current measurement before opening the demo.",
                    "Demo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (demoActive)
            {
                HideDemo();
                return;
            }

            timer.Stop();
            captureTimer.Stop();
            autoRefresh.Text = "Start auto-refresh";

            demoWasViewingSavedResults = viewingSavedResults;
            demoPreviousInformation = information.Text;
            demoPreviousDetails = details.Text;
            demoPreviousAnalysis = lastAnalysis;
            demoPreviousRows = currentRows;
            demoPreviousDriverDetails.Clear();
            foreach (var item in savedDriverDetails) demoPreviousDriverDetails[item.Key] = item.Value;

            // Preserve the details for every already displayed driver before freezing the view.
            var priorDetails = new Dictionary<int, string>();
            for (int i = 0; i < stackEvidence.Rows.Count; i++)
            {
                string content;
                if (viewingSavedResults)
                    savedDriverDetails.TryGetValue(i, out content);
                else
                    content = FormatDriverDetails(stackEvidence.Rows[i].Tag as NativeEtwAnalyzer.RankedDriver);
                priorDetails[i + 1] = content ?? ""; // The fictional driver is inserted at index 0.
            }
            savedDriverDetails.Clear();
            foreach (var item in priorDetails) savedDriverDetails[item.Key] = item.Value;
            viewingSavedResults = true; // Freeze both real snapshot rows and fictional rows.
            demoActive = true;
            demoButton.BackColor = Color.Firebrick;
            demoButton.ForeColor = Color.White;
            demoButton.UseVisualStyleBackColor = false;
            demoTitleVisible = true;
            Text = WindowCaption + " - Demo-Mode";
            demoTitleTimer.Start();
            lastAnalysis = null;
            currentRows = new List<ProcessRow>();

            const string module = "AntivirusFilter.sys";
            // Insert the example rows at the top, leaving all pre-existing rows unchanged.
            processes.Rows.Insert(0, "powershell.exe", 8240, "28.0", "24.0", "0.96",
                "960.0 ms / 4.0 s", "4.0", "6.0 : 1",
                "High kernel CPU load", module +
                " | 490 CPU instruction samples; 43 time buckets; strong simulated correlation");
            processes.Rows.Insert(0, "Zabbix2.exe", 5512, "45.0", "40.0", "1.60",
                "1600.0 ms / 4.0 s", "5.0", "8.0 : 1",
                "High kernel CPU load", module +
                " | 820 CPU instruction samples; 48 time buckets; strong simulated correlation");
            for (int i = 0; i < 2; i++)
            {
                processes.Rows[i].DefaultCellStyle.BackColor = Color.MistyRose;
                processes.Rows[i].DefaultCellStyle.ForeColor = Color.DarkRed;
            }
            filters.Rows.Insert(0, "AntivirusFilter", 3, 0, "File-system minifilter");
            stackEvidence.Rows.Insert(0, module, "0.000", "0.000", 0, 0,
                2400, "Strong simulated process correlation");
            stackEvidence.Rows[0].DefaultCellStyle.BackColor = Color.MistyRose;
            stackEvidence.Rows[0].DefaultCellStyle.ForeColor = Color.DarkRed;
            savedDriverDetails[0] =
                "Simulated driver involvement" + Environment.NewLine + Environment.NewLine +
                "Suspected driver: " + module + Environment.NewLine +
                "Affected process: Zabbix2.exe (PID 5512) | Total CPU 45.0% | Kernel CPU 40.0% | 820 simulated instruction samples" + Environment.NewLine +
                "Affected process: powershell.exe (PID 8240) | Total CPU 28.0% | Kernel CPU 24.0% | 490 simulated instruction samples" + Environment.NewLine + Environment.NewLine +
                "Both links and all values are simulated for illustration. They are not evidence of any real driver fault.";
            information.Text = "Demo-Mode: fictional AntivirusFilter.sys example added to the existing, frozen results. " +
                "Only the two new process rows and the AntivirusFilter driver/filter rows are simulated; other entries are unchanged snapshots.";
            processes.ClearSelection();
            filters.ClearSelection();
            stackEvidence.ClearSelection();
            if (stackEvidence.Rows.Count > 0) stackEvidence.CurrentCell = stackEvidence.Rows[0].Cells[0];
            DisplayDriverDetails();
        }

        private void HideDemo()
        {
            if (!demoActive) return;
            // Only the first entries were inserted by ShowDemo. Never remove live rows.
            if (processes.Rows.Count >= 2)
            {
                processes.Rows.RemoveAt(0);
                processes.Rows.RemoveAt(0);
            }
            if (filters.Rows.Count > 0) filters.Rows.RemoveAt(0);
            if (stackEvidence.Rows.Count > 0) stackEvidence.Rows.RemoveAt(0);

            savedDriverDetails.Clear();
            foreach (var item in demoPreviousDriverDetails) savedDriverDetails[item.Key] = item.Value;
            demoPreviousDriverDetails.Clear();
            viewingSavedResults = demoWasViewingSavedResults;
            lastAnalysis = demoPreviousAnalysis;
            currentRows = demoPreviousRows;
            information.Text = demoPreviousInformation;
            details.Text = demoPreviousDetails;
            demoActive = false;
            demoTitleTimer.Stop();
            demoTitleVisible = false;
            Text = WindowCaption;
            demoButton.BackColor = SystemColors.Control;
            demoButton.ForeColor = SystemColors.ControlText;
            demoButton.UseVisualStyleBackColor = true;
            processes.ClearSelection();
            filters.ClearSelection();
            stackEvidence.ClearSelection();
        }

        private void SaveResults()
        {
            if (refreshing || analyzing || captureCompleting || starting || trace.Running)
            {
                MessageBox.Show(this, "Finish the current measurement before saving results.", "Save results", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new SaveFileDialog { Filter = "KernelTraceScout results (*.kts.xml)|*.kts.xml", FileName = "KernelTraceScout_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".kts.xml" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var snapshot = new ResultSnapshot { FormatVersion = 1, SavedUtc = DateTime.UtcNow, Status = information.Text,
                        Processes = CaptureTable(processes), Minifilters = CaptureTable(filters), Drivers = CaptureTable(stackEvidence) };
                    for (int i = 0; i < stackEvidence.Rows.Count; i++)
                    {
                        string content;
                        if (viewingSavedResults) savedDriverDetails.TryGetValue(i, out content);
                        else content = FormatDriverDetails(stackEvidence.Rows[i].Tag as NativeEtwAnalyzer.RankedDriver);
                        snapshot.DriverDetails.Add(content ?? "");
                    }
                    var serializer = new XmlSerializer(typeof(ResultSnapshot));
                    using (var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None)) serializer.Serialize(stream, snapshot);
                    AppLog.Write("Result snapshot saved: " + dialog.FileName);
                }
                catch (Exception ex) { ShowError("Save results failed", ex); }
            }
        }

        private void LoadResults()
        {
            if (refreshing || analyzing || captureCompleting || starting || trace.Running)
            {
                MessageBox.Show(this, "Finish the current measurement before loading results.", "Load results", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new OpenFileDialog { Filter = "KernelTraceScout results (*.kts.xml)|*.kts.xml" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (new FileInfo(dialog.FileName).Length > 16 * 1024 * 1024) throw new InvalidDataException("Snapshot exceeds the 16 MiB limit.");
                    ResultSnapshot snapshot;
                    using (var reader = XmlReader.Create(dialog.FileName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }))
                        snapshot = (ResultSnapshot)new XmlSerializer(typeof(ResultSnapshot)).Deserialize(reader);
                    if (snapshot == null || snapshot.FormatVersion != 1 || snapshot.Processes == null || snapshot.Minifilters == null || snapshot.Drivers == null || snapshot.DriverDetails == null || snapshot.DriverDetails.Count != snapshot.Drivers.Rows.Count ||
                        snapshot.Processes.Rows.Any(r => r.Cells == null || r.Cells.Count != processes.Columns.Count) ||
                        snapshot.Minifilters.Rows.Any(r => r.Cells == null || r.Cells.Count != filters.Columns.Count) ||
                        snapshot.Drivers.Rows.Any(r => r.Cells == null || r.Cells.Count != stackEvidence.Columns.Count))
                        throw new InvalidDataException("Invalid or incompatible KernelTraceScout result file.");
                    timer.Stop(); captureTimer.Stop(); autoRefresh.Text = "Start auto-refresh";
                    LeaveSavedView();
                    viewingSavedResults = true;
                    currentRows.Clear();
                    lastAnalysis = null;
                    savedDriverDetails.Clear();
                    for (int i = 0; i < snapshot.DriverDetails.Count; i++) savedDriverDetails[i] = snapshot.DriverDetails[i] ?? "";
                    RestoreTable(processes, snapshot.Processes);
                    RestoreTable(filters, snapshot.Minifilters);
                    RestoreTable(stackEvidence, snapshot.Drivers);
                    information.Text = "Saved results (not live) | " + snapshot.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " | " + snapshot.Status;
                    details.Text = savedDriverDetails.Count > 0 ? savedDriverDetails[0] : "";
                    AppLog.Write("Result snapshot loaded: " + dialog.FileName);
                }
                catch (Exception ex) { ShowError("Load results failed", ex); }
            }
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
