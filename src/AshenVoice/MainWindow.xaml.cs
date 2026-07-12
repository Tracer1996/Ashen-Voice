using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AshenVoice;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _processTimer;
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly string _tokenPath;
    private readonly string _speakerStatePath;
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _trayIcon;
    private Process? _discordProcess;
    private bool _serviceRunning = true;
    private bool _allowClose;
    private bool _lastWowDetected;
    private bool _overlayActive;
    private bool _overlayStarting;
    private bool _discordConnected;
    private bool _discordConnecting;
    private int? _detectedWowProcessId;
    private int? _overlayProcessId;

    public MainWindow()
    {
        InitializeComponent();

        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AshenVoice");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        _tokenPath = Path.Combine(_settingsDirectory, "discord-token.bin");
        _speakerStatePath = Path.Combine(_settingsDirectory, "speakers.tsv");

        Directory.CreateDirectory(_settingsDirectory);
        ClearSpeakerState();
        LoadSettings();
        ConfigureTrayIcon();

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _processTimer.Tick += (_, _) => CheckForWow();
        _processTimer.Start();

        Loaded += (_, _) =>
        {
            AddLog("Ashen Voice started.");
            AddLog("Phase 3 compact overlay is active. Connect Discord, start the overlay, then speak in the configured voice channel.");
            CheckForWow(forceLog: true);
            UpdateControls();
        };
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Ashen Voice",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Ashen Voice", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Stop Overlay", null, async (_, _) => await StopOverlayAsync());
        menu.Items.Add("Disconnect Discord", null, async (_, _) => await DisconnectDiscordAsync());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                    ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }

        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        ProcessNamesTextBox.Text = string.Join(", ", _settings.ProcessNames);
        ServerIdTextBox.Text = _settings.DiscordGuildId;
        VoiceChannelIdTextBox.Text = _settings.DiscordChannelId;
        SaveTokenCheckBox.IsChecked = _settings.SaveDiscordToken;
        TokenPasswordBox.Password = _settings.SaveDiscordToken ? LoadProtectedToken() : string.Empty;
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(_settingsDirectory);
        File.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(
                _settings,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        SetStartupRegistration(_settings.StartWithWindows);
        SaveSettings();
    }

    private void DiscordSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _settings.SaveDiscordToken = SaveTokenCheckBox.IsChecked == true;
        if (!_settings.SaveDiscordToken)
        {
            DeleteProtectedToken();
        }
        SaveSettings();
    }

    private void ProcessNamesTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var names = ProcessNamesTextBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Path.GetFileNameWithoutExtension(name) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            names.Add("Wow");
        }

        _settings.ProcessNames = names;
        ProcessNamesTextBox.Text = string.Join(", ", names);
        SaveSettings();
        AddLog("Updated WoW process names.");
        CheckForWow(forceLog: true);
    }

    private static void SetStartupRegistration(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);

        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue("AshenVoice", $"\"{Environment.ProcessPath}\" --minimized");
        }
        else
        {
            key.DeleteValue("AshenVoice", throwOnMissingValue: false);
        }
    }

    private DetectedWowProcess? FindWowProcess()
    {
        foreach (string processName in _settings.ProcessNames)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                Process? selected = processes
                    .OrderByDescending(process => process.MainWindowHandle != IntPtr.Zero)
                    .ThenBy(process => process.Id)
                    .FirstOrDefault();

                foreach (Process process in processes)
                {
                    if (!ReferenceEquals(process, selected))
                    {
                        process.Dispose();
                    }
                }

                if (selected is not null)
                {
                    var result = new DetectedWowProcess(selected.Id, selected.ProcessName);
                    selected.Dispose();
                    return result;
                }
            }
            catch
            {
                // The client may close while it is being inspected. The next timer tick retries.
            }
        }

        return null;
    }

    private void CheckForWow(bool forceLog = false)
    {
        DetectedWowProcess? detectedProcess = FindWowProcess();
        bool detected = detectedProcess is not null;
        _detectedWowProcessId = detectedProcess?.ProcessId;

        WowStatusText.Text = detected
            ? $"Detected ({detectedProcess!.ProcessName})"
            : "Not detected";
        WowStatusText.Foreground = detected
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.LightGray;

        if (forceLog || detected != _lastWowDetected)
        {
            AddLog(detected
                ? $"World of Warcraft client detected. PID: {detectedProcess!.ProcessId}."
                : "World of Warcraft client not detected.");
        }

        _lastWowDetected = detected;

        if (_overlayActive && (_overlayProcessId is null || !IsProcessRunning(_overlayProcessId.Value)))
        {
            _overlayActive = false;
            _overlayProcessId = null;
            AddLog("The WoW client closed. Overlay status was reset.");
        }

        UpdateControls();
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _serviceRunning = true;
        _processTimer.Start();
        AddLog("Ashen Voice monitoring started.");
        CheckForWow(forceLog: true);
        UpdateControls();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _serviceRunning = false;
        _processTimer.Stop();
        WowStatusText.Text = "Monitoring stopped";
        WowStatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        AddLog("Ashen Voice monitoring stopped.");
        UpdateControls();
    }

    private void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForWow(forceLog: true);
    }

    private async void ConnectDiscordButton_Click(object sender, RoutedEventArgs e)
    {
        await ConnectDiscordAsync();
    }

    private async Task ConnectDiscordAsync()
    {
        if (_discordConnected || _discordConnecting)
        {
            return;
        }

        string token = TokenPasswordBox.Password.Trim();
        string guildId = ServerIdTextBox.Text.Trim();
        string channelId = VoiceChannelIdTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            ShowDiscordValidation("Paste your Discord bot token first.");
            return;
        }

        if (!IsDiscordSnowflake(guildId) || !IsDiscordSnowflake(channelId))
        {
            ShowDiscordValidation("The server ID and voice channel ID must be Discord IDs containing 17 to 20 digits.");
            return;
        }

        string discordDirectory = Path.Combine(AppContext.BaseDirectory, "discord");
        string nodePath = Path.Combine(discordDirectory, "node.exe");
        string companionPath = Path.Combine(discordDirectory, "companion.js");

        if (!File.Exists(nodePath) || !File.Exists(companionPath))
        {
            AddLog("Discord connection failed: the packaged companion files are missing.");
            System.Windows.MessageBox.Show(
                "The Discord companion files are missing. Reinstall the Phase 3 build.",
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _settings.DiscordGuildId = guildId;
        _settings.DiscordChannelId = channelId;
        _settings.SaveDiscordToken = SaveTokenCheckBox.IsChecked == true;
        SaveSettings();

        if (_settings.SaveDiscordToken)
        {
            SaveProtectedToken(token);
        }
        else
        {
            DeleteProtectedToken();
        }

        ClearSpeakerState();
        _discordConnecting = true;
        _discordConnected = false;
        DiscordStatusText.Text = "Connecting...";
        DiscordStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        UpdateControls();
        AddLog("Connecting the Ashen Voice bot to Discord...");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                WorkingDirectory = discordDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(companionPath);
            startInfo.Environment["ASHEN_DISCORD_TOKEN"] = token;
            startInfo.Environment["ASHEN_DISCORD_GUILD_ID"] = guildId;
            startInfo.Environment["ASHEN_DISCORD_CHANNEL_ID"] = channelId;
            startInfo.Environment["ASHEN_STATE_DIRECTORY"] = _settingsDirectory;

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += DiscordOutputReceived;
            process.ErrorDataReceived += DiscordErrorReceived;
            process.Exited += DiscordProcessExited;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Windows could not start the Discord companion.");
            }

            _discordProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.Delay(250);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The Discord companion exited with code {process.ExitCode}.");
            }
        }
        catch (Exception exception)
        {
            _discordConnecting = false;
            _discordConnected = false;
            AddLog($"Discord connection failed: {exception.Message}");
            DiscordStatusText.Text = "Error";
            DiscordStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            UpdateControls();
        }
    }

    private void DiscordOutputReceived(object sender, DataReceivedEventArgs e)
    {
        string? line = e.Data;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(() => HandleDiscordMessage(line));
        }
    }

    private void DiscordErrorReceived(object sender, DataReceivedEventArgs e)
    {
        string? line = e.Data;
        if (!string.IsNullOrWhiteSpace(line) && !Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(() => AddLog($"Discord companion: {line}"));
        }
    }

    private void HandleDiscordMessage(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            string type = document.RootElement.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? "status"
                : "status";
            string message = document.RootElement.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? line
                : line;

            switch (type)
            {
                case "connected":
                    _discordConnecting = false;
                    _discordConnected = true;
                    DiscordStatusText.Text = "Connected";
                    DiscordStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                    AddLog(message);
                    break;

                case "error":
                    AddLog($"Discord: {message}");
                    if (!_discordConnected)
                    {
                        DiscordStatusText.Text = "Error";
                        DiscordStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    }
                    break;

                case "speaker":
                    AddLog(message);
                    break;

                default:
                    AddLog($"Discord: {message}");
                    break;
            }
        }
        catch
        {
            AddLog($"Discord: {line}");
        }

        UpdateControls();
    }

    private void DiscordProcessExited(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (sender is Process process)
            {
                try
                {
                    AddLog($"Discord companion stopped with code {process.ExitCode}.");
                }
                catch
                {
                    AddLog("Discord companion stopped.");
                }
            }

            if (ReferenceEquals(_discordProcess, sender))
            {
                _discordProcess = null;
            }

            _discordConnecting = false;
            _discordConnected = false;
            DiscordStatusText.Text = "Disconnected";
            DiscordStatusText.Foreground = System.Windows.Media.Brushes.LightGray;
            ClearSpeakerState();
            UpdateControls();
        });
    }

    private async void DisconnectDiscordButton_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectDiscordAsync();
    }

    private async Task DisconnectDiscordAsync()
    {
        Process? process = _discordProcess;
        _discordProcess = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception exception)
            {
                AddLog($"Discord disconnect warning: {exception.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        _discordConnecting = false;
        _discordConnected = false;
        DiscordStatusText.Text = "Disconnected";
        DiscordStatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        ClearSpeakerState();
        AddLog("Discord disconnected.");
        UpdateControls();
    }

    private static bool IsDiscordSnowflake(string value)
    {
        return Regex.IsMatch(value, @"^\d{17,20}$", RegexOptions.CultureInvariant);
    }

    private static void ShowDiscordValidation(string message)
    {
        System.Windows.MessageBox.Show(
            message,
            "Discord setup",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void SaveProtectedToken(string token)
    {
        try
        {
            byte[] plain = Encoding.UTF8.GetBytes(token);
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_tokenPath, protectedBytes);
        }
        catch (Exception exception)
        {
            AddLog($"Could not securely save the Discord token: {exception.Message}");
        }
    }

    private string LoadProtectedToken()
    {
        try
        {
            if (!File.Exists(_tokenPath))
            {
                return string.Empty;
            }

            byte[] protectedBytes = File.ReadAllBytes(_tokenPath);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void DeleteProtectedToken()
    {
        try
        {
            File.Delete(_tokenPath);
        }
        catch
        {
            // A token file locked by antivirus can be removed on the next settings change.
        }
    }

    private async void StartOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        await StartOverlayAsync();
    }

    private async Task StartOverlayAsync()
    {
        if (_overlayActive || _overlayStarting)
        {
            return;
        }

        DetectedWowProcess? wowProcess = FindWowProcess();
        if (wowProcess is null)
        {
            AddLog("Start Overlay failed: no configured WoW process is running.");
            System.Windows.MessageBox.Show(
                "Launch OctoWoW first, wait for Ashen Voice to show Detected, and then click Start Overlay.",
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string nativeDirectory = Path.Combine(AppContext.BaseDirectory, "native");
        string injectorPath = Path.Combine(nativeDirectory, "AshenVoiceInjector.exe");
        string overlayPath = Path.Combine(nativeDirectory, "AshenVoiceOverlay.dll");

        if (!File.Exists(injectorPath) || !File.Exists(overlayPath))
        {
            AddLog("Start Overlay failed: the installed native files are missing.");
            System.Windows.MessageBox.Show(
                "The native overlay files are missing from the installation. Reinstall Ashen Voice.",
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string readyEventName = ReadyEventName(wowProcess.ProcessId);
        if (TryOpenSignaledEvent(readyEventName))
        {
            _overlayActive = true;
            _overlayProcessId = wowProcess.ProcessId;
            AddLog("Connected to an Ashen Voice overlay that was already running.");
            UpdateControls();
            return;
        }

        _overlayStarting = true;
        UpdateControls();
        AddLog($"Loading the compact DirectX 9 overlay into PID {wowProcess.ProcessId}...");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = injectorPath,
                Arguments = $"--pid {wowProcess.ProcessId} --dll \"{overlayPath}\"",
                WorkingDirectory = nativeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? injector = Process.Start(startInfo);
            if (injector is null)
            {
                throw new InvalidOperationException("Windows could not start the overlay injector.");
            }

            Task<string> outputTask = injector.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = injector.StandardError.ReadToEndAsync();
            await injector.WaitForExitAsync();

            string output = (await outputTask).Trim();
            string error = (await errorTask).Trim();

            if (!string.IsNullOrWhiteSpace(output))
            {
                AddLog(output);
            }

            if (injector.ExitCode != 0)
            {
                AddLog(string.IsNullOrWhiteSpace(error)
                    ? $"The overlay injector exited with code {injector.ExitCode}."
                    : error);

                System.Windows.MessageBox.Show(
                    "Ashen Voice could not load the overlay. Check the Activity Log. If WoW is running as administrator, run Ashen Voice as administrator too.",
                    "Overlay failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            bool ready = await WaitForNamedEventAsync(readyEventName, TimeSpan.FromSeconds(12));
            if (!ready)
            {
                AddLog("The DLL loaded, but the DirectX 9 hook did not report ready.");
                AddLog($"Native log: {Path.Combine(_settingsDirectory, "overlay-native.log")}");
                System.Windows.MessageBox.Show(
                    "The DLL loaded, but the DirectX 9 hook did not become ready. Check the native log shown in the Activity Log.",
                    "Overlay hook not ready",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _overlayActive = true;
            _overlayProcessId = wowProcess.ProcessId;
            AddLog("Compact overlay is active. It stays hidden until a Discord user speaks.");
        }
        catch (Exception exception)
        {
            AddLog($"Start Overlay failed: {exception.Message}");
            System.Windows.MessageBox.Show(
                exception.Message,
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _overlayStarting = false;
            UpdateControls();
        }
    }

    private async void StopOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        await StopOverlayAsync();
    }

    private async Task StopOverlayAsync()
    {
        int? processId = _overlayProcessId ?? _detectedWowProcessId;
        if (processId is null)
        {
            AddLog("No active overlay process was found.");
            _overlayActive = false;
            UpdateControls();
            return;
        }

        string eventName = StopEventName(processId.Value);
        if (!EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? stopEvent))
        {
            AddLog("The overlay stop signal was not found. It may already be stopped.");
            _overlayActive = false;
            _overlayProcessId = null;
            UpdateControls();
            return;
        }

        using (stopEvent)
        {
            stopEvent.Set();
        }

        AddLog("Stop signal sent to the DirectX 9 overlay.");
        await Task.Delay(750);
        _overlayActive = false;
        _overlayProcessId = null;
        UpdateControls();
    }

    private async void PreviewOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_overlayActive)
        {
            System.Windows.MessageBox.Show(
                "Start the overlay first, then run the compact preview.",
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_discordConnected || _discordConnecting)
        {
            System.Windows.MessageBox.Show(
                "Disconnect Discord before using the fake preview so live speaker data is not overwritten.",
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        File.WriteAllText(
            _speakerStatePath,
            $"Methl\t{Environment.NewLine}Danari\t{Environment.NewLine}Poetry\t",
            Encoding.UTF8);
        AddLog("Compact overlay preview started for eight seconds.");
        PreviewOverlayButton.IsEnabled = false;
        await Task.Delay(TimeSpan.FromSeconds(8));
        ClearSpeakerState();
        PreviewOverlayButton.IsEnabled = true;
        AddLog("Compact overlay preview finished.");
    }

    private static async Task<bool> WaitForNamedEventAsync(string eventName, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryOpenSignaledEvent(eventName))
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static bool TryOpenSignaledEvent(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? eventHandle))
        {
            return false;
        }

        using (eventHandle)
        {
            return eventHandle.WaitOne(0);
        }
    }

    private static string ReadyEventName(int processId) => $@"Local\AshenVoice_Ready_{processId}";

    private static string StopEventName(int processId) => $@"Local\AshenVoice_Stop_{processId}";

    private void ClearSpeakerState()
    {
        try
        {
            File.WriteAllText(_speakerStatePath, string.Empty, Encoding.UTF8);
        }
        catch
        {
            // The companion may briefly own the file while atomically replacing it.
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void UpdateControls()
    {
        AppStatusText.Text = _serviceRunning ? "Running" : "Stopped";
        AppStatusText.Foreground = _serviceRunning
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.OrangeRed;

        OverlayStatusText.Text = _overlayStarting
            ? "Starting..."
            : _overlayActive
                ? "Active"
                : "Ready";
        OverlayStatusText.Foreground = _overlayActive
            ? System.Windows.Media.Brushes.LightGreen
            : _overlayStarting
                ? System.Windows.Media.Brushes.Orange
                : System.Windows.Media.Brushes.LightGray;

        if (!_discordConnected && !_discordConnecting && _discordProcess is null)
        {
            DiscordStatusText.Text = "Disconnected";
            DiscordStatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        }

        StartButton.IsEnabled = !_serviceRunning;
        StopButton.IsEnabled = _serviceRunning;
        StartOverlayButton.IsEnabled = !_overlayActive && !_overlayStarting && _detectedWowProcessId is not null;
        StopOverlayButton.IsEnabled = _overlayActive && !_overlayStarting;
        ConnectDiscordButton.IsEnabled = !_discordConnected && !_discordConnecting;
        DisconnectDiscordButton.IsEnabled = _discordConnected || _discordConnecting || _discordProcess is not null;
        PreviewOverlayButton.IsEnabled = _overlayActive && !_discordConnected && !_discordConnecting;
    }

    private void AddLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();
            _trayIcon?.ShowBalloonTip(
                1500,
                "Ashen Voice",
                "Ashen Voice is still running in the system tray.",
                Forms.ToolTipIcon.Info);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SignalOverlayStopWithoutWaiting();
        StopDiscordWithoutWaiting();
        ClearSpeakerState();
        _trayIcon?.Dispose();
        base.OnClosing(e);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        SignalOverlayStopWithoutWaiting();
        StopDiscordWithoutWaiting();
        ClearSpeakerState();
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void SignalOverlayStopWithoutWaiting()
    {
        int? processId = _overlayProcessId;
        if (processId is null)
        {
            return;
        }

        if (EventWaitHandle.TryOpenExisting(StopEventName(processId.Value), out EventWaitHandle? stopEvent))
        {
            using (stopEvent)
            {
                stopEvent.Set();
            }
        }
    }

    private void StopDiscordWithoutWaiting()
    {
        try
        {
            if (_discordProcess is not null && !_discordProcess.HasExited)
            {
                _discordProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The companion may already be closing.
        }
    }
}

public sealed record DetectedWowProcess(int ProcessId, string ProcessName);

public sealed class AppSettings
{
    public bool MinimizeToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool SaveDiscordToken { get; set; } = true;

    public string DiscordGuildId { get; set; } = string.Empty;

    public string DiscordChannelId { get; set; } = string.Empty;

    public List<string> ProcessNames { get; set; } = new()
    {
        "Wow",
        "WoW",
        "OctoWoW"
    };
}
