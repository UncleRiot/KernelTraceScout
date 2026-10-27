using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace KernelTraceScout
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppSettings settings;
        private readonly ProcessMonitor monitor = new ProcessMonitor();
        private readonly NativeEtwCapture trace = new NativeEtwCapture();
        private readonly ObservableCollection<ProcessItem> processItems = new ObservableCollection<ProcessItem>();
        private readonly ObservableCollection<FilterItem> filterItems = new ObservableCollection<FilterItem>();
        private readonly ObservableCollection<DriverItem> driverItems = new ObservableCollection<DriverItem>();
        private readonly ObservableCollection<AssociationItem> associationItems = new ObservableCollection<AssociationItem>();
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer refreshTimer;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer progressTimer;
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly HashSet<int> suspiciousPids = new HashSet<int>();
        private readonly Dictionary<int, DateTime> captureIdentities = new Dictionary<int, DateTime>();
        private List<ProcessRow> currentRows = new List<ProcessRow>();
        private NativeEtwAnalyzer.AnalysisResult lastAnalysis;
        private bool refreshing;
        private bool analyzing;
        private bool starting;
        private bool demoMode;
        private bool elevated;
        private bool darkMode;
        private DateTime captureStartedUtc;
        private int durationSeconds;

        public MainWindow()
        {
            InitializeComponent();
            settings = SettingsStore.Load();
            ProcessList.ItemsSource = processItems;
            FilterList.ItemsSource = filterItems;
            DriverList.ItemsSource = driverItems;
            AssociationList.ItemsSource = associationItems;
            Threshold.Value = settings.MinimumKernelCpuPercent;
            RefreshInterval.Value = settings.RefreshIntervalSeconds;
            CaptureDuration.Value = settings.CaptureSeconds;
            refreshTimer = DispatcherQueue.CreateTimer();
            refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
            refreshTimer.Tick += async (sender, args) => await RefreshProcessesAsync();
            progressTimer = DispatcherQueue.CreateTimer();
            progressTimer.Interval = TimeSpan.FromMilliseconds(200);
            progressTimer.Tick += (sender, args) => UpdateProgress();
            trace.Completed += (file, error) => DispatcherQueue.TryEnqueue(() => { _ = FinishCaptureAsync(file, error); });
            Navigator.SelectedItem = Navigator.MenuItems[0];
            ConfigureWindow();
            ApplyTheme(settings.DarkMode);
            Threshold.ValueChanged += DiagnosticSettings_ValueChanged;
            RefreshInterval.ValueChanged += DiagnosticSettings_ValueChanged;
            CaptureDuration.ValueChanged += DiagnosticSettings_ValueChanged;
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) { AppLog.Write("Administrator check failed: " + ex); }
            Closed += (sender, args) =>
            {
                closing.Cancel();
                refreshTimer.Stop();
                progressTimer.Stop();
                // The original bounded ETW watchdog remains responsible for stopping a session
                // if this window closes while a capture is still active.
                if (!trace.Running) trace.Dispose();
            };
            // Prompt only after the WinUI window is loaded; never require Visual Studio elevation.
            Root.Loaded += Root_Loaded;
        }


        private void ConfigureWindow()
        {
            // Keep the native Windows caption dark, even when the app content uses Light mode.
            try
            {
                var titleBar = AppWindow.TitleBar;
                if (titleBar != null && Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    var darkBackground = Windows.UI.Color.FromArgb(255, 27, 29, 33);
                    var hoverBackground = Windows.UI.Color.FromArgb(255, 55, 59, 65);
                    var pressedBackground = Windows.UI.Color.FromArgb(255, 73, 78, 85);
                    var lightForeground = Windows.UI.Color.FromArgb(255, 245, 245, 245);
                    titleBar.BackgroundColor = darkBackground;
                    titleBar.ForegroundColor = lightForeground;
                    titleBar.InactiveBackgroundColor = darkBackground;
                    titleBar.InactiveForegroundColor = lightForeground;
                    titleBar.ButtonBackgroundColor = darkBackground;
                    titleBar.ButtonForegroundColor = lightForeground;
                    titleBar.ButtonInactiveBackgroundColor = darkBackground;
                    titleBar.ButtonInactiveForegroundColor = lightForeground;
                    titleBar.ButtonHoverBackgroundColor = hoverBackground;
                    titleBar.ButtonHoverForegroundColor = lightForeground;
                    titleBar.ButtonPressedBackgroundColor = pressedBackground;
                    titleBar.ButtonPressedForegroundColor = lightForeground;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Title bar colors could not be set: " + ex.Message);
            }

            try
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 820));
            }
            catch (Exception ex)
            {
                AppLog.Write("Window size could not be set: " + ex.Message);
            }

            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Icons", "KernelTraceScout.ico");
                if (File.Exists(iconPath))
                {
                    IntPtr hwnd = WindowNative.GetWindowHandle(this);
                    var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    AppWindow.GetFromWindowId(id).SetIcon(iconPath);
                }
                else
                {
                    AppLog.Write("Window icon not found: " + iconPath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Window icon could not be set: " + ex.Message);
            }
        }

        private async void Root_Loaded(object sender, RoutedEventArgs args)
        {
            Root.Loaded -= Root_Loaded;
            if (!elevated)
            {
                var prompt = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = "Administrator access",
                    Content = "KernelTraceScout needs administrator rights for ETW recording and some system diagnostics. Restart the application as administrator? You can also continue with limited functionality.",
                    PrimaryButtonText = "Restart as administrator",
                    CloseButtonText = "Continue without administrator",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await prompt.ShowAsync() == ContentDialogResult.Primary)
                {
                    try
                    {
                        string executable = Environment.ProcessPath;
                        if (string.IsNullOrWhiteSpace(executable))
                            throw new InvalidOperationException("Application executable path is unavailable.");
                        Process.Start(new ProcessStartInfo(executable)
                        {
                            UseShellExecute = true,
                            Verb = "runas",
                            WorkingDirectory = AppContext.BaseDirectory
                        });
                        Close();
                        return;
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    {
                        ShowNotice("Administrator request canceled. Continuing with limited functionality.");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("Administrator restart failed: " + ex);
                        ShowNotice("Could not restart as administrator: " + ex.Message);
                    }
                }
            }
            await InitializeDataAsync();
        }

        private async Task InitializeDataAsync()
        {
            if (!elevated)
                Status.Text = "Limited mode: ETW capture requires administrator rights.";
            await RefreshProcessesAsync();
            await RefreshFiltersAsync();
        }

        private void ApplyTheme(bool dark)
        {
            darkMode = dark;
            Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            // Button displays the theme to which it will switch.
            ThemeGlyph.Glyph = dark ? "\uE706" : "\uE708";
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ThemeButton, dark ? "Switch to light mode" : "Switch to dark mode");
        }

        // Single source of truth: all persistent options are stored in the executable directory.
        private void SaveSettings()
        {
            try { SettingsStore.Save(settings); }
            catch (Exception ex)
            {
                AppLog.Write("Settings save failed: " + ex);
                ShowNotice("Cannot save settings to " + AppFiles.ConfigPath + ": " + ex.Message);
            }
        }

        private void IncrementSetting_Click(object sender, RoutedEventArgs args)
        {
            AdjustSetting(sender, 1);
        }

        private void DecrementSetting_Click(object sender, RoutedEventArgs args)
        {
            AdjustSetting(sender, -1);
        }

        private void AdjustSetting(object sender, int amount)
        {
            var button = sender as RepeatButton;
            if (button == null) return;
            Microsoft.UI.Xaml.Controls.NumberBox target = null;
            switch (button.Tag as string)
            {
                case "Threshold": target = Threshold; break;
                case "RefreshInterval": target = RefreshInterval; break;
                case "CaptureDuration": target = CaptureDuration; break;
            }
            if (target == null) return;
            double current = double.IsFinite(target.Value) ? target.Value : target.Minimum;
            target.Value = Math.Max(target.Minimum, Math.Min(target.Maximum, current + amount));
            // ValueChanged persists the setting to KernelTraceScout.config.xml.
        }

        private void DiagnosticSettings_ValueChanged(Microsoft.UI.Xaml.Controls.NumberBox sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs args)
        {
            if (double.IsFinite(Threshold.Value) && Threshold.Value >= 1 && Threshold.Value <= 99)
                settings.MinimumKernelCpuPercent = Threshold.Value;
            if (double.IsFinite(RefreshInterval.Value) && RefreshInterval.Value >= 1 && RefreshInterval.Value <= 300)
            {
                settings.RefreshIntervalSeconds = (int)RefreshInterval.Value;
                refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
            }
            if (double.IsFinite(CaptureDuration.Value) && CaptureDuration.Value >= 5 && CaptureDuration.Value <= 120)
                settings.CaptureSeconds = (int)CaptureDuration.Value;
            SaveSettings();
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(!darkMode);
            settings.DarkMode = darkMode;
            SaveSettings();
        }

        private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            string name = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "Processes";
            ProcessList.Visibility = name == "Processes" ? Visibility.Visible : Visibility.Collapsed;
            CauseEffectPanel.Visibility = name == "CauseEffect" ? Visibility.Visible : Visibility.Collapsed;
            FilterList.Visibility = name == "Filters" ? Visibility.Visible : Visibility.Collapsed;
            FiltersButton.Visibility = name == "Filters" ? Visibility.Visible : Visibility.Collapsed;
            DriverPanel.Visibility = name == "Drivers" ? Visibility.Visible : Visibility.Collapsed;
            HelpPanel.Visibility = name == "Help" ? Visibility.Visible : Visibility.Collapsed;
            AboutPanel.Visibility = name == "About" ? Visibility.Visible : Visibility.Collapsed;
        }

        private static SolidColorBrush AlertBrush()
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(95, 190, 40, 45));
        }

        private void ShowNotice(string text)
        {
            Notice.Message = text;
            Notice.IsOpen = true;
            Status.Text = text;
        }

        private void LeaveDemo()
        {
            if (!demoMode) return;
            demoMode = false;
            processItems.Clear();
            filterItems.Clear();
            driverItems.Clear();
            associationItems.Clear();
            AssociationSummary.Text = "No ETW association yet.";
            lastAnalysis = null;
            DriverExplanation.Text = string.Empty;
            Status.Text = "Demo ended. Showing live data.";
        }

        // Column sorting is presentation-only. Source measurements and evidence remain untouched.
        private readonly Dictionary<string, bool> sortDirections = new Dictionary<string, bool>();

        private void SortColumn_Click(object sender, RoutedEventArgs args)
        {
            if (!(sender is Button button) || !(button.Tag is string tag)) return;
            var parts = tag.Split(':');
            if (parts.Length != 2) return;
            bool ascending = !sortDirections.TryGetValue(tag, out bool previous) || !previous;
            sortDirections[tag] = ascending;
            switch (parts[0])
            {
                case "process": SortCollection(processItems, parts[1], ascending); break;
                case "filter": SortCollection(filterItems, parts[1], ascending); break;
                case "driver": SortCollection(driverItems, parts[1], ascending); break;
                case "association": SortCollection(associationItems, parts[1], ascending); break;
            }
        }

        private static void SortCollection<T>(ObservableCollection<T> items, string property, bool ascending)
        {
            var info = typeof(T).GetProperty(property);
            if (info == null) return;
            var reordered = ascending
                ? items.OrderBy(item => SortKey(info.GetValue(item)?.ToString())).ToList()
                : items.OrderByDescending(item => SortKey(info.GetValue(item)?.ToString())).ToList();
            // Move existing objects to retain binding identities, selection and highlight state.
            for (int index = 0; index < reordered.Count; index++)
            {
                int previous = items.IndexOf(reordered[index]);
                if (previous != index && previous >= 0) items.Move(previous, index);
            }
        }

        private static (int Kind, double Number, string Text) SortKey(string value)
        {
            string text = (value ?? string.Empty).Trim();
            string number = text.EndsWith(":1", StringComparison.Ordinal) ? text.Substring(0, text.Length - 2) : text.TrimEnd('%');
            if (double.TryParse(number, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double parsed)
                || double.TryParse(number, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
                return (0, parsed, string.Empty);
            return (1, 0, text.ToUpperInvariant());
        }

        private void Check_Click(object sender, RoutedEventArgs args) { _ = RefreshProcessesAsync(); }
        private async Task RefreshProcessesAsync()
        {
            if (refreshing || closing.IsCancellationRequested) return;
            LeaveDemo();
            refreshing = true;
            CheckButton.IsEnabled = false;
            try
            {
                if (double.IsFinite(Threshold.Value) && Threshold.Value >= 1 && Threshold.Value <= 99)
                    settings.MinimumKernelCpuPercent = Threshold.Value;
                var rows = await Task.Run(() => monitor.Read(settings));
                if (closing.IsCancellationRequested) return;
                currentRows = rows;
                processItems.Clear();
                int ownPid = Environment.ProcessId;
                foreach (var row in rows)
                {
                    if (row.Pid == 4 || row.Pid == ownPid) continue;
                    if (trace.Running && row.Valid && row.Status.Contains("Suspicious") &&
                        captureIdentities.TryGetValue(row.Pid, out DateTime start) && start == row.Start)
                        suspiciousPids.Add(row.Pid);
                    processItems.Add(new ProcessItem
                    {
                        Name = row.Name,
                        Pid = row.Pid.ToString(CultureInfo.InvariantCulture),
                        Cpu = row.Valid ? row.Cpu.ToString("F1", CultureInfo.CurrentCulture) + "%" : "—",
                        Kernel = row.Valid ? row.Kernel.ToString("F1", CultureInfo.CurrentCulture) + "%" : "—",
                        User = row.Valid ? row.User.ToString("F1", CultureInfo.CurrentCulture) + "%" : "—",
                        Ratio = row.Valid ? (row.User > 0.001 ? row.Ratio.ToString("F1", CultureInfo.CurrentCulture) + ":1" : "N/A") : "—",
                        Status = row.Status,
                        Clue = FindDriverClue(row),
                        HighlightBrush = row.Valid && row.Status.Contains("Suspicious", StringComparison.OrdinalIgnoreCase) ? AlertBrush() : null
                    });
                }
                SortCollection(processItems, nameof(ProcessItem.Cpu), ascending: false);
                Status.Text = "Live process measurements updated. CPU percentages are relative to all logical processors.";
            }
            catch (Exception ex) { AppLog.Write("Process refresh failed: " + ex); ShowNotice("Process measurement failed: " + ex.Message); }
            finally { refreshing = false; CheckButton.IsEnabled = !closing.IsCancellationRequested; }
        }

        private string FindDriverClue(ProcessRow row)
        {
            if (lastAnalysis == null) return "No ETW analysis yet";
            if (!captureIdentities.TryGetValue(row.Pid, out DateTime start) || start != row.Start)
                return "Process identity differs from recording";
            var evidence = lastAnalysis.Evidence.Where(x => x.Pid == row.Pid)
                .OrderByDescending(x => x.LeafCount).ThenByDescending(x => x.SampleCount).FirstOrDefault();
            return evidence == null ? "No correlated driver samples" : Path.GetFileName(evidence.ModuleName) + " (" + evidence.SampleCount + " samples; clue only)";
        }

        private void Auto_Click(object sender, RoutedEventArgs args)
        {
            if (refreshTimer.IsRunning)
            {
                refreshTimer.Stop();
                AutoButton.Icon = new SymbolIcon(Symbol.Play);
                AutoButton.Label = "Auto refresh";
                Status.Text = "Automatic updates stopped.";
            }
            else
            {
                if (double.IsFinite(RefreshInterval.Value) && RefreshInterval.Value >= 1 && RefreshInterval.Value <= 300)
                    settings.RefreshIntervalSeconds = (int)RefreshInterval.Value;
                refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
                refreshTimer.Start();
                AutoButton.Icon = new SymbolIcon(Symbol.Pause);
                AutoButton.Label = "Pause";
                _ = RefreshProcessesAsync();
            }
        }

        private void Filters_Click(object sender, RoutedEventArgs args) { _ = RefreshFiltersAsync(); }
        private async Task RefreshFiltersAsync()
        {
            LeaveDemo();
            FiltersButton.IsEnabled = false;
            try
            {
                var filters = await Task.Run(() => NativeFilters.Enumerate());
                if (closing.IsCancellationRequested) return;
                filterItems.Clear();
                foreach (var filter in filters)
                    filterItems.Add(new FilterItem { Name = filter.Name, Instances = filter.Instances.ToString(), Frame = filter.Frame.ToString(), Note = "Loaded; not proof of high CPU usage" });
                Status.Text = "Loaded file system filters refreshed.";
            }
            catch (Exception ex) { AppLog.Write("Filter refresh failed: " + ex); ShowNotice("Filter enumeration failed: " + ex.Message); }
            finally { FiltersButton.IsEnabled = !closing.IsCancellationRequested; }
        }

        // DEMO DATA BOUNDARY: Only display models are modified. No CPU load, driver operation,
        // ETW evidence, or process state is fabricated outside the clearly marked demo view.
        private async void Demo_Click(object sender, RoutedEventArgs args)
        {
            if (trace.Running || analyzing || starting || refreshing)
            {
                ShowNotice("Stop the active capture or refresh before opening demo mode.");
                return;
            }
            if (refreshTimer.IsRunning) Auto_Click(sender, args);
            DemoButton.IsEnabled = false;
            try
            {
                var rows = await Task.Run(() => monitor.Read(settings));
                var filters = await Task.Run(() => NativeFilters.Enumerate());
                if (closing.IsCancellationRequested) return;

                int ownPid = Environment.ProcessId;
                var candidates = rows.Where(r => r.Valid && r.Pid > 4 && r.Pid != ownPid &&
                    !string.IsNullOrWhiteSpace(r.Name) && double.IsFinite(r.User) && r.User >= 0)
                    .GroupBy(r => r.Pid).Select(g => g.First()).ToList();
                if (candidates.Count == 0 || filters.Count == 0)
                {
                    ShowNotice("Demo requires at least one measurable process and one loaded file-system filter.");
                    return;
                }

                // Select real, currently observed identities. Only the displayed CPU measurements
                // and hypothetical driver/process correlation are simulated.
                var random = new Random();
                var selectedFilter = filters[random.Next(filters.Count)];
                var selectedProcesses = candidates.OrderBy(_ => random.Next()).Take(Math.Min(2, candidates.Count)).ToList();
                string driverName = selectedFilter.Name + ".sys";
                demoMode = true;
                lastAnalysis = null;
                currentRows = rows;
                processItems.Clear(); filterItems.Clear(); driverItems.Clear(); associationItems.Clear();

                foreach (var filter in filters.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    filterItems.Add(new FilterItem {
                        Name = filter.Name,
                        Instances = filter.Instances.ToString(CultureInfo.InvariantCulture),
                        Frame = filter.Frame.ToString(CultureInfo.InvariantCulture),
                        Note = filter.Name == selectedFilter.Name ? "SIMULATED correlation; real loaded filter" : "Real loaded filter"
                    });

                var affected = new List<string>();
                foreach (var row in candidates.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    int index = selectedProcesses.FindIndex(p => p.Pid == row.Pid);
                    bool simulated = index >= 0;
                    double kernel = simulated ? (index == 0 ? 53.0 : 32.0) : row.Kernel;
                    double user = simulated ? (index == 0 ? 3.0 : 2.0) : row.User;
                    double cpu = simulated ? kernel + user : row.Cpu;
                    string ratio = user > 0.001 ? (kernel / user).ToString("F1", CultureInfo.CurrentCulture) + ":1" : "N/A";
                    processItems.Add(new ProcessItem {
                        Name = row.Name,
                        Pid = row.Pid.ToString(CultureInfo.InvariantCulture),
                        Cpu = cpu.ToString("F1", CultureInfo.CurrentCulture) + "%",
                        Kernel = kernel.ToString("F1", CultureInfo.CurrentCulture) + "%",
                        User = user.ToString("F1", CultureInfo.CurrentCulture) + "%",
                        Ratio = ratio,
                        Status = simulated ? "SIMULATED: High kernel CPU" : "LIVE BASELINE: " + row.Status,
                        Clue = simulated ? selectedFilter.Name + " (SIMULATED association)" : "No simulated association",
                        HighlightBrush = simulated ? AlertBrush() : null
                    });
                    if (simulated)
                        affected.Add(row.Name + " (PID " + row.Pid.ToString(CultureInfo.InvariantCulture) +
                            ", kernel " + kernel.ToString("F1", CultureInfo.CurrentCulture) + "%, K/U " + ratio + ")");
                }

                SortCollection(processItems, nameof(ProcessItem.Cpu), ascending: false);

                driverItems.Add(new DriverItem {
                    Name = driverName,
                    Dpc = "N/A", Isr = "N/A", Samples = "N/A", Level = "SIMULATED",
                    Explanation = "Real loaded filter; its CPU involvement and process association are simulated, not measured.",
                    HighlightBrush = AlertBrush()
                });
                associationItems.Add(new AssociationItem {
                    Driver = selectedFilter.Name + "  →  " + affected.Count + " simulated process context(s)",
                    Processes = string.Join("  ·  ", affected),
                    Evidence = "SIMULATED ONLY: random real filter and real process identities; hypothetical CPU load. No ETW evidence.",
                    HighlightBrush = AlertBrush()
                });
                AssociationSummary.Text = "SIMULATION: Real process and filter identities; invented CPU measurements and association. NOT an actual diagnosis.";
                DriverExplanation.Text = "SIMULATION: Driver involvement is fictional. No ETW analysis performed.";
                Navigator.SelectedItem = Navigator.MenuItems[0];
                Status.Text = "DEMO MODE: Real process/filter names; CPU figures and cause/effect links are simulated. Check once returns to live data.";
            }
            catch (Exception ex)
            {
                AppLog.Write("Live-identity demo failed: " + ex);
                ShowNotice("Could not prepare demo from real process/filter data: " + ex.Message);
            }
            finally { DemoButton.IsEnabled = !closing.IsCancellationRequested; }
        }

        private void Capture_Click(object sender, RoutedEventArgs args) { _ = ToggleCaptureAsync(); }
        private async Task ToggleCaptureAsync()
        {
            if (starting || analyzing || closing.IsCancellationRequested) return;
            if (!elevated)
            {
                ShowNotice("Administrator rights are required for ETW recording. Restart the application as administrator.");
                return;
            }
            starting = true;
            CaptureButton.IsEnabled = false;
            try
            {
                if (trace.Running)
                {
                    await Task.Run(() => trace.Complete());
                    return;
                }
                // NumberBox.Value may still hold the previous value while the user is typing.
                // Read the currently visible text even when Enter was not pressed.
                string enteredDuration = CaptureDuration.Text?.Trim() ?? string.Empty;
                if (!int.TryParse(enteredDuration, NumberStyles.Integer, CultureInfo.CurrentCulture, out int requestedDuration)
                    || requestedDuration < 5 || requestedDuration > 120)
                {
                    ShowNotice("Capture duration must be a whole number between 5 and 120 seconds.");
                    return;
                }
                durationSeconds = requestedDuration;
                settings.CaptureSeconds = requestedDuration;
                CaptureDuration.Value = requestedDuration;
                SaveSettings();
                await RefreshProcessesAsync();
                if (closing.IsCancellationRequested) return;
                captureIdentities.Clear(); suspiciousPids.Clear();
                foreach (var row in currentRows.Where(x => x.Start != default(DateTime)))
                    captureIdentities[row.Pid] = row.Start;
                lastAnalysis = null;
                driverItems.Clear(); DriverExplanation.Text = string.Empty;
                string path = Path.Combine(AppFiles.LogDirectory, "KernelTraceScout_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".etl");
                await Task.Run(() => trace.Start(path, durationSeconds, settings.MaximumTraceMegabytes));
                if (closing.IsCancellationRequested) return;
                captureStartedUtc = DateTime.UtcNow;
                CaptureProgress.Value = 0;
                CaptureProgress.Visibility = Visibility.Visible;
                CaptureProgressText.Visibility = Visibility.Visible;
                progressTimer.Start();
                CaptureButton.Icon = new SymbolIcon(Symbol.Stop);
                CaptureButton.Label = "Stop capture";
                Status.Text = "Recording ETW driver and kernel CPU data. Capture will end automatically.";
            }
            catch (Exception ex) { AppLog.Write("ETW capture failed: " + ex); ShowNotice("ETW capture failed: " + ex.Message); }
            finally { starting = false; CaptureButton.IsEnabled = !analyzing && !closing.IsCancellationRequested; }
        }

        private void UpdateProgress()
        {
            double elapsed = Math.Max(0, (DateTime.UtcNow - captureStartedUtc).TotalSeconds);
            CaptureProgress.Value = Math.Max(0, Math.Min(100, 100 * elapsed / Math.Max(1, durationSeconds)));
            CaptureProgressText.Text = Math.Min(durationSeconds, (int)elapsed) + " / " + durationSeconds + " s";
        }

        private async Task FinishCaptureAsync(string file, Exception error)
        {
            if (closing.IsCancellationRequested || analyzing) return;
            progressTimer.Stop();
            analyzing = true;
            CaptureButton.IsEnabled = false;
            CaptureProgress.Value = 100;
            CaptureProgressText.Text = "Analyzing...";
            try
            {
                if (error != null) throw error;
                var suspicious = new HashSet<int>(suspiciousPids);
                uint lost = trace.EventsLost;
                var result = await Task.Run(() => NativeEtwAnalyzer.Analyze(file, suspicious, lost, closing.Token));
                if (closing.IsCancellationRequested) return;
                lastAnalysis = result;
                driverItems.Clear();
                foreach (var d in result.Drivers)
                {
                    string moduleName = Path.GetFileName(d.Name);
                    bool correlated = result.Evidence.Any(e => suspicious.Contains(e.Pid)
                        && string.Equals(Path.GetFileName(e.ModuleName), moduleName, StringComparison.OrdinalIgnoreCase));
                    driverItems.Add(new DriverItem { Name = moduleName, Dpc = d.DpcMilliseconds.ToString("F3", CultureInfo.CurrentCulture), Isr = d.IsrMilliseconds.ToString("F3", CultureInfo.CurrentCulture), Samples = d.Samples.ToString(), Level = d.Level, Explanation = d.Explanation, HighlightBrush = correlated ? AlertBrush() : null });
                }
                BuildAssociations(result);
                Status.Text = "ETW analysis complete. Driver matches are clues, not proof of causation. " + result.Quality;
                Navigator.SelectedItem = Navigator.MenuItems[3];
            }
            catch (Exception ex) { AppLog.Write("ETW analysis failed: " + ex); ShowNotice("ETW analysis failed: " + ex.Message); }
            finally
            {
                analyzing = false;
                CaptureProgress.Visibility = Visibility.Collapsed;
                CaptureProgressText.Visibility = Visibility.Collapsed;
                CaptureButton.Icon = new SymbolIcon(Symbol.Find);
                CaptureButton.Label = "Analyze (ETW)";
                CaptureButton.IsEnabled = !closing.IsCancellationRequested;
            }
        }

        // Only display observed process-specific ETW stack associations. No causal attribution is inferred.
        private void BuildAssociations(NativeEtwAnalyzer.AnalysisResult result)
        {
            associationItems.Clear();
            foreach (var group in result.Evidence.GroupBy(e => Path.GetFileName(e.ModuleName), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(e => e.SampleCount)))
            {
                var affected = new List<string>();
                int samples = 0;
                foreach (var evidence in group)
                {
                    var row = currentRows.FirstOrDefault(r => r.Pid == evidence.Pid &&
                        captureIdentities.TryGetValue(r.Pid, out DateTime start) && r.Start == start);
                    if (row == null || !row.Valid) continue;
                    samples += evidence.SampleCount;
                    affected.Add(row.Name + " (PID " + row.Pid + ", kernel " + row.Kernel.ToString("F1", CultureInfo.CurrentCulture)
                        + "%, K/U " + (row.User > 0.001 ? row.Ratio.ToString("F1", CultureInfo.CurrentCulture) + ":1" : "N/A") + ")");
                }
                if (affected.Count == 0) continue;
                bool suspect = group.Any(e => suspiciousPids.Contains(e.Pid));
                associationItems.Add(new AssociationItem {
                    Driver = group.Key + "  →  " + affected.Count + " associated process(es)",
                    Processes = string.Join("  ·  ", affected),
                    Evidence = samples + " process-specific stack samples; " + (suspect ? "high kernel CPU observed" : "no high kernel CPU flag") + "; causality unconfirmed",
                    HighlightBrush = suspect ? AlertBrush() : null
                });
            }
            AssociationSummary.Text = associationItems.Count == 0
                ? "No verified process-specific stack associations in this capture. Cause unknown."
                : associationItems.Count + " driver associations observed in process stacks. Select a driver for technical details.";
        }

        private void Driver_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            DriverExplanation.Text = (DriverList.SelectedItem as DriverItem)?.Explanation ?? string.Empty;
        }

        private async void Export_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("CSV file", new List<string> { ".csv" });
                picker.SuggestedFileName = "KernelTraceScout_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                var csv = new StringBuilder();
                if (FilterList.Visibility == Visibility.Visible)
                {
                    csv.AppendLine("Name;Instances;Frame;Note");
                    foreach (var f in filterItems) csv.AppendLine(Csv(f.Name, f.Instances, f.Frame, f.Note));
                }
                else if (DriverPanel.Visibility == Visibility.Visible)
                {
                    csv.AppendLine("Driver;DPC ms;ISR ms;Samples;Evidence");
                    foreach (var d in driverItems) csv.AppendLine(Csv(d.Name, d.Dpc, d.Isr, d.Samples, d.Level));
                }
                else
                {
                    csv.AppendLine("Process;PID;CPU;Kernel;User;Status;Driver clue");
                    foreach (var p in processItems) csv.AppendLine(Csv(p.Name, p.Pid, p.Cpu, p.Kernel, p.User, p.Status, p.Clue));
                }
                await Windows.Storage.FileIO.WriteTextAsync(file, csv.ToString(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
                Status.Text = "CSV saved: " + file.Path;
            }
            catch (Exception ex) { AppLog.Write("CSV export failed: " + ex); ShowNotice("CSV export failed: " + ex.Message); }
        }

        private async void Repository_Click(object sender, RoutedEventArgs e)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/UncleRiot/KernelTraceScout")); }
            catch (Exception ex) { AppLog.Write("GitHub link failed: " + ex); ShowNotice("Could not open the project repository: " + ex.Message); }
        }

        private static string Csv(params string[] values)
        {
            return string.Join(";", values.Select(v =>
            {
                string t = v ?? string.Empty;
                if (t.Length > 0 && "=+-@\t\r".Contains(t[0])) t = "'" + t;
                return "\"" + t.Replace("\"", "\"\"") + "\"";
            }));
        }
    }

    // A muted red tint denotes an alert or correlated clue, not confirmed causality.
    public sealed class ProcessItem
    {
        public string Name { get; set; }
        public string Pid { get; set; }
        public string Cpu { get; set; }
        public string Kernel { get; set; }
        public string User { get; set; }
        public string Ratio { get; set; }
        public string Status { get; set; }
        public string Clue { get; set; }
        public SolidColorBrush HighlightBrush { get; set; }
    }
    public sealed class FilterItem
    {
        public string Name { get; set; }
        public string Instances { get; set; }
        public string Frame { get; set; }
        public string Note { get; set; }
    }
    public sealed class DriverItem
    {
        public string Name { get; set; }
        public string Dpc { get; set; }
        public string Isr { get; set; }
        public string Samples { get; set; }
        public string Level { get; set; }
        public string Explanation { get; set; }
        public SolidColorBrush HighlightBrush { get; set; }
    }
    public sealed class AssociationItem
    {
        public string Driver { get; set; }
        public string Processes { get; set; }
        public string Evidence { get; set; }
        public SolidColorBrush HighlightBrush { get; set; }
    }

}
