using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly string _overlaySettingsPath;
    private readonly string _avatarDirectory;
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, DiscordVoiceMember> _voiceMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveSpeaker> _activeSpeakers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _speakerGenerations = new(StringComparer.Ordinal);

    private AppSettings _settings = new();
    private Forms.NotifyIcon? _trayIcon;
    private DiscordRpcClient? _discordRpc;
    private CancellationTokenSource? _discordConnectCancellation;
    private bool _serviceRunning = true;
    private bool _allowClose;
    private bool _lastWowDetected;
    private bool _overlayActive;
    private bool _overlayStarting;
    private bool _discordConnected;
    private bool _discordConnecting;
    private int? _detectedWowProcessId;
    private int? _overlayProcessId;
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();

        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AshenVoice");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        _tokenPath = Path.Combine(_settingsDirectory, "discord-oauth.bin");
        _speakerStatePath = Path.Combine(_settingsDirectory, "speakers.tsv");
        _overlaySettingsPath = Path.Combine(_settingsDirectory, "overlay-settings.ini");
        _avatarDirectory = Path.Combine(_settingsDirectory, "avatars");

        Directory.CreateDirectory(_settingsDirectory);
        Directory.CreateDirectory(_avatarDirectory);
        ClearSpeakerState();
        LoadSettings();
        RemoveLegacyStartupRegistration();
        if (_settings.StartWithWindows)
        {
            TrySetStartupRegistration(true, out _);
        }
        WriteOverlaySettings();
        ConfigureTrayIcon();

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _processTimer.Tick += (_, _) => CheckForWow();
        _processTimer.Start();

        Loaded += async (_, _) =>
        {
            AddLog("Ashen Voice started.");
            AddLog("Ready.");
            CheckForWow(forceLog: true);
            UpdateControls();
            await TryAutoConnectDiscordAsync();

            if (Environment.GetCommandLineArgs().Any(argument =>
                    string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                Hide();
            }
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

        if (_settings.ProcessNames is null || _settings.ProcessNames.Count == 0)
        {
            _settings.ProcessNames = new List<string> { "Wow", "WoW", "OctoWoW" };
        }

        _loadingSettings = true;
        try
        {
            MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
            StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
            ProcessNamesTextBox.Text = string.Join(", ", _settings.ProcessNames);
            ApplyOverlaySettingsToUi();
        }
        finally
        {
            _loadingSettings = false;
        }
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

    private void ApplyOverlaySettingsToUi()
    {
        PositionComboBox.SelectedIndex = _settings.OverlayPosition switch
        {
            "TopLeft" => 1,
            "BottomRight" => 2,
            "BottomLeft" => 3,
            _ => 0
        };
        HorizontalOffsetTextBox.Text = _settings.HorizontalOffset.ToString(CultureInfo.InvariantCulture);
        VerticalOffsetTextBox.Text = _settings.VerticalOffset.ToString(CultureInfo.InvariantCulture);
        OverlayScaleSlider.Value = _settings.OverlayScale;
        CardWidthSlider.Value = _settings.CardWidth;
        AvatarSizeSlider.Value = _settings.AvatarSize;
        FontSizeSlider.Value = _settings.FontSize;
        BackgroundOpacitySlider.Value = _settings.BackgroundOpacity;
        RingThicknessSlider.Value = _settings.RingThickness;
        MaximumSpeakersSlider.Value = _settings.MaximumSpeakers;
        HideDelaySlider.Value = _settings.SpeakerHideDelayMs / 1000.0;
        ShowAvatarCheckBox.IsChecked = _settings.ShowAvatars;
        ShowNameCheckBox.IsChecked = _settings.ShowNames;
        UpdateOverlaySettingLabels();
    }

    private void OverlaySettingChanged(object sender, RoutedEventArgs e)
    {
        SaveOverlaySettingsFromUi();
    }

    private void OverlayNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveOverlaySettingsFromUi();
    }

    private void SaveOverlaySettingsFromUi()
    {
        if (!IsLoaded || _loadingSettings)
        {
            return;
        }

        if (PositionComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedPosition &&
            selectedPosition.Tag is string position)
        {
            _settings.OverlayPosition = position;
        }

        _settings.HorizontalOffset = ParseClampedInteger(HorizontalOffsetTextBox.Text, _settings.HorizontalOffset, 0, 1000);
        _settings.VerticalOffset = ParseClampedInteger(VerticalOffsetTextBox.Text, _settings.VerticalOffset, 0, 1000);
        _settings.OverlayScale = Math.Clamp(OverlayScaleSlider.Value, 0.75, 1.50);
        _settings.CardWidth = Math.Clamp((int)Math.Round(CardWidthSlider.Value), 150, 300);
        _settings.AvatarSize = Math.Clamp((int)Math.Round(AvatarSizeSlider.Value), 24, 52);
        _settings.FontSize = Math.Clamp((int)Math.Round(FontSizeSlider.Value), 12, 22);
        _settings.BackgroundOpacity = Math.Clamp((int)Math.Round(BackgroundOpacitySlider.Value), 0, 100);
        _settings.RingThickness = Math.Clamp((int)Math.Round(RingThicknessSlider.Value), 1, 5);
        _settings.MaximumSpeakers = Math.Clamp((int)Math.Round(MaximumSpeakersSlider.Value), 1, 10);
        _settings.SpeakerHideDelayMs = Math.Clamp((int)Math.Round(HideDelaySlider.Value * 1000.0), 200, 3000);
        _settings.ShowAvatars = ShowAvatarCheckBox.IsChecked == true;
        _settings.ShowNames = ShowNameCheckBox.IsChecked == true;

        if (!_settings.ShowAvatars && !_settings.ShowNames)
        {
            _settings.ShowNames = true;
            _loadingSettings = true;
            ShowNameCheckBox.IsChecked = true;
            _loadingSettings = false;
        }

        HorizontalOffsetTextBox.Text = _settings.HorizontalOffset.ToString(CultureInfo.InvariantCulture);
        VerticalOffsetTextBox.Text = _settings.VerticalOffset.ToString(CultureInfo.InvariantCulture);
        UpdateOverlaySettingLabels();
        SaveSettings();
        WriteOverlaySettings();
        WriteSpeakerState();
    }

    private static int ParseClampedInteger(string value, int fallback, int minimum, int maximum)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private void UpdateOverlaySettingLabels()
    {
        OverlayScaleValueText.Text = $"{Math.Round(_settings.OverlayScale * 100.0):0}%";
        CardWidthValueText.Text = $"{_settings.CardWidth} px";
        AvatarSizeValueText.Text = $"{_settings.AvatarSize} px";
        FontSizeValueText.Text = $"{_settings.FontSize} px";
        BackgroundOpacityValueText.Text = $"{_settings.BackgroundOpacity}%";
        RingThicknessValueText.Text = $"{_settings.RingThickness} px";
        MaximumSpeakersValueText.Text = _settings.MaximumSpeakers.ToString(CultureInfo.InvariantCulture);
        HideDelayValueText.Text = $"{_settings.SpeakerHideDelayMs / 1000.0:0.0} sec";
    }

    private void ResetOverlaySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        _settings.OverlayPosition = defaults.OverlayPosition;
        _settings.HorizontalOffset = defaults.HorizontalOffset;
        _settings.VerticalOffset = defaults.VerticalOffset;
        _settings.OverlayScale = defaults.OverlayScale;
        _settings.CardWidth = defaults.CardWidth;
        _settings.AvatarSize = defaults.AvatarSize;
        _settings.FontSize = defaults.FontSize;
        _settings.BackgroundOpacity = defaults.BackgroundOpacity;
        _settings.RingThickness = defaults.RingThickness;
        _settings.MaximumSpeakers = defaults.MaximumSpeakers;
        _settings.SpeakerHideDelayMs = defaults.SpeakerHideDelayMs;
        _settings.ShowAvatars = defaults.ShowAvatars;
        _settings.ShowNames = defaults.ShowNames;

        _loadingSettings = true;
        ApplyOverlaySettingsToUi();
        _loadingSettings = false;
        SaveSettings();
        WriteOverlaySettings();
        WriteSpeakerState();
        AddLog("Overlay settings reset.");
    }

    private void WriteOverlaySettings()
    {
        try
        {
            string[] lines =
            {
                $"Position={_settings.OverlayPosition}",
                $"HorizontalOffset={_settings.HorizontalOffset}",
                $"VerticalOffset={_settings.VerticalOffset}",
                $"Scale={_settings.OverlayScale.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"CardWidth={_settings.CardWidth}",
                $"AvatarSize={_settings.AvatarSize}",
                $"FontSize={_settings.FontSize}",
                $"BackgroundOpacity={_settings.BackgroundOpacity}",
                $"RingThickness={_settings.RingThickness}",
                $"MaximumSpeakers={_settings.MaximumSpeakers}",
                $"ShowAvatars={(_settings.ShowAvatars ? 1 : 0)}",
                $"ShowNames={(_settings.ShowNames ? 1 : 0)}"
            };

            string temporary = _overlaySettingsPath + ".tmp";
            File.WriteAllLines(temporary, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _overlaySettingsPath, overwrite: true);
        }
        catch (Exception exception)
        {
            AddLog($"Overlay settings could not be saved: {exception.Message}");
        }
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingSettings)
        {
            return;
        }

        _settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        bool requestedStartup = StartWithWindowsCheckBox.IsChecked == true;
        if (requestedStartup != _settings.StartWithWindows)
        {
            if (!TrySetStartupRegistration(requestedStartup, out string error))
            {
                _loadingSettings = true;
                StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
                _loadingSettings = false;
                AddLog($"Startup setting failed: {error}");
                System.Windows.MessageBox.Show(error, "Ashen Voice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.StartWithWindows = requestedStartup;
            AddLog(requestedStartup ? "Start with Windows enabled." : "Start with Windows disabled.");
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

    private static void RemoveLegacyStartupRegistration()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            key?.DeleteValue("AshenVoice", throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    private static bool TrySetStartupRegistration(bool enabled, out string error)
    {
        error = string.Empty;
        try
        {
            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                error = "Ashen Voice could not find its executable path.";
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (enabled)
            {
                startInfo.ArgumentList.Add("/Create");
                startInfo.ArgumentList.Add("/TN");
                startInfo.ArgumentList.Add("Ashen Voice");
                startInfo.ArgumentList.Add("/TR");
                startInfo.ArgumentList.Add($"\"{executablePath}\" --minimized");
                startInfo.ArgumentList.Add("/SC");
                startInfo.ArgumentList.Add("ONLOGON");
                startInfo.ArgumentList.Add("/RL");
                startInfo.ArgumentList.Add("HIGHEST");
                startInfo.ArgumentList.Add("/F");
            }
            else
            {
                startInfo.ArgumentList.Add("/Delete");
                startInfo.ArgumentList.Add("/TN");
                startInfo.ArgumentList.Add("Ashen Voice");
                startInfo.ArgumentList.Add("/F");
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                error = "Windows could not update the startup task.";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                error = process.StandardError.ReadToEnd().Trim();
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = process.StandardOutput.ReadToEnd().Trim();
                }
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = $"Windows returned startup task error {process.ExitCode}.";
                }
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
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
        await ConnectDiscordAsync(forceAuthorization: false);
    }

    private async Task TryAutoConnectDiscordAsync()
    {
        DiscordOAuthToken? token = LoadProtectedToken();
        if (token?.IsUsable != true)
        {
            return;
        }

        AddLog("Attempting to reconnect to the active Discord account...");
        await ConnectDiscordAsync(forceAuthorization: false);
    }

    private async Task ConnectDiscordAsync(bool forceAuthorization)
    {
        if (_discordConnected || _discordConnecting)
        {
            return;
        }

        if (!DiscordBuildConfig.IsConfigured)
        {
            System.Windows.MessageBox.Show(
                "Discord Application ID is missing. Add DISCORD_CLIENT_ID in GitHub and rebuild.",
                "Discord setup required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            AddLog("Discord Application ID is missing from the build.");
            return;
        }

        await DisconnectDiscordCoreAsync(writeLog: false);

        _discordConnecting = true;
        DiscordStatusText.Text = "Connecting...";
        DiscordStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        DiscordAccountText.Text = "Connecting to Discord...";
        DiscordVoiceText.Text = "Checking active voice channel...";
        UpdateControls();

        _discordConnectCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _discordConnectCancellation.Token;

        try
        {
            DiscordOAuthToken? token = forceAuthorization ? null : LoadProtectedToken();
            if (token?.IsUsable != true)
            {
                AddLog("Opening Discord authorization...");
                var oauth = new DiscordOAuthService();
                token = await oauth.AuthorizeAsync(cancellationToken);
                SaveProtectedToken(token);
            }

            var rpc = new DiscordRpcClient(DiscordBuildConfig.ClientId);
            WireDiscordClient(rpc);
            _discordRpc = rpc;
            await rpc.ConnectAndAuthenticateAsync(token.AccessToken, cancellationToken);

            _discordConnected = true;
            _discordConnecting = false;
            DiscordStatusText.Text = "Connected";
            DiscordStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            AddLog("Discord is connected directly through the local desktop client.");
        }
        catch (OperationCanceledException)
        {
            await DisposeDiscordRpcAsync();
            AddLog("Discord connection was canceled.");
            ResetDiscordUi();
        }
        catch (DiscordRpcException exception) when (exception.Code is 4006 or 4007)
        {
            await DisposeDiscordRpcAsync();
            DeleteProtectedToken();
            AddLog($"Discord authorization was rejected: {exception.Message}");
            ResetDiscordUi();
            System.Windows.MessageBox.Show(
                "Discord did not accept the saved authorization. Click Connect Discord again to approve Ashen Voice.",
                "Discord authorization",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            await DisposeDiscordRpcAsync();
            AddLog($"Discord connection failed: {exception.Message}");
            ResetDiscordUi(error: true);
            System.Windows.MessageBox.Show(
                BuildDiscordErrorMessage(exception),
                "Discord connection failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _discordConnecting = false;
            UpdateControls();
        }
    }

    private static string BuildDiscordErrorMessage(Exception exception)
    {
        string message = exception.Message;
        if (message.Contains("scope", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("approved", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access", StringComparison.OrdinalIgnoreCase))
        {
            return message +
                   "\n\nDuring development, your Discord account must be added as an application tester. Public use of Discord RPC voice scopes requires Discord approval.";
        }

        return message;
    }

    private void WireDiscordClient(DiscordRpcClient rpc)
    {
        rpc.Log += message => SafeDispatch(() => AddLog($"Discord: {message}"));
        rpc.Authenticated += user => SafeDispatch(() =>
        {
            DiscordAccountText.Text = $"Connected as {user.DisplayName}";
        });
        rpc.VoiceChannelChanged += channel => SafeDispatch(() => HandleVoiceChannelChanged(channel));
        rpc.VoiceMemberUpserted += member => SafeDispatch(() => _voiceMembers[member.UserId] = member);
        rpc.VoiceMemberRemoved += userId => SafeDispatch(() =>
        {
            _voiceMembers.Remove(userId);
            RemoveActiveSpeaker(userId);
        });
        rpc.SpeakingChanged += (userId, speaking) => SafeDispatch(() => HandleSpeakingChanged(userId, speaking));
        rpc.Disconnected += message => SafeDispatch(() =>
        {
            AddLog($"Discord disconnected: {message}");
            _voiceMembers.Clear();
            _activeSpeakers.Clear();
            _speakerGenerations.Clear();
            ClearSpeakerState();
            ResetDiscordUi(error: true);
            UpdateControls();
        });
    }

    private void SafeDispatch(Action action)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(action);
        }
    }

    private void HandleVoiceChannelChanged(DiscordVoiceChannel? channel)
    {
        _voiceMembers.Clear();
        _activeSpeakers.Clear();
        _speakerGenerations.Clear();
        WriteSpeakerState();

        if (channel is null)
        {
            DiscordVoiceText.Text = "Not currently in a Discord voice channel";
            AddLog("Join a Discord voice channel.");
            return;
        }

        foreach (DiscordVoiceMember member in channel.Members)
        {
            _voiceMembers[member.UserId] = member;
        }

        DiscordVoiceText.Text = string.IsNullOrWhiteSpace(channel.GuildName)
            ? $"Voice: {channel.Name}"
            : $"Voice: {channel.GuildName} — {channel.Name}";
        string voiceDescription = string.IsNullOrWhiteSpace(channel.GuildName)
            ? channel.Name
            : $"{channel.GuildName} — {channel.Name}";
        AddLog($"Following Discord voice channel: {voiceDescription}");
    }

    private void HandleSpeakingChanged(string userId, bool speaking)
    {
        int generation = _speakerGenerations.TryGetValue(userId, out int current) ? current + 1 : 1;
        _speakerGenerations[userId] = generation;

        if (speaking)
        {
            if (!_voiceMembers.TryGetValue(userId, out DiscordVoiceMember? member))
            {
                member = new DiscordVoiceMember(userId, "Discord user", null, false);
                _voiceMembers[userId] = member;
            }

            _ = AddOrUpdateActiveSpeakerAsync(member, generation);
            return;
        }

        _ = RemoveSpeakerAfterDelayAsync(userId, generation);
    }

    private async Task AddOrUpdateActiveSpeakerAsync(DiscordVoiceMember member, int generation)
    {
        string avatarPath = await GetAvatarPathAsync(member);
        await Dispatcher.InvokeAsync(() =>
        {
            if (!_speakerGenerations.TryGetValue(member.UserId, out int latest) || latest != generation)
            {
                return;
            }

            DateTimeOffset started = _activeSpeakers.TryGetValue(member.UserId, out ActiveSpeaker? existing)
                ? existing.StartedAt
                : DateTimeOffset.UtcNow;
            _activeSpeakers[member.UserId] = new ActiveSpeaker(member.UserId, member.DisplayName, avatarPath, started);
            WriteSpeakerState();
        });
    }

    private async Task RemoveSpeakerAfterDelayAsync(string userId, int generation)
    {
        await Task.Delay(_settings.SpeakerHideDelayMs);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_speakerGenerations.TryGetValue(userId, out int latest) && latest == generation)
            {
                RemoveActiveSpeaker(userId);
            }
        });
    }

    private void RemoveActiveSpeaker(string userId)
    {
        if (_activeSpeakers.Remove(userId))
        {
            WriteSpeakerState();
        }
    }

    private async Task<string> GetAvatarPathAsync(DiscordVoiceMember member)
    {
        if (string.IsNullOrWhiteSpace(member.AvatarHash))
        {
            return string.Empty;
        }

        string safeHash = string.Concat(member.AvatarHash.Where(char.IsLetterOrDigit));
        string path = Path.Combine(_avatarDirectory, $"{member.UserId}-{safeHash}-128.png");
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            string url = $"https://cdn.discordapp.com/avatars/{member.UserId}/{member.AvatarHash}.png?size=128";
            byte[] bytes = await _httpClient.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception exception)
        {
            SafeDispatch(() => AddLog($"Could not cache {member.DisplayName}'s avatar: {exception.Message}"));
            return string.Empty;
        }
    }

    private void WriteSpeakerState()
    {
        try
        {
            IEnumerable<string> lines = _activeSpeakers.Values
                .OrderBy(speaker => speaker.StartedAt)
                .Take(_settings.MaximumSpeakers)
                .Select(speaker => $"{CleanSpeakerField(speaker.DisplayName)}\t{CleanSpeakerField(speaker.AvatarPath)}");

            string temporary = _speakerStatePath + ".tmp";
            File.WriteAllLines(temporary, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _speakerStatePath, overwrite: true);
        }
        catch (Exception exception)
        {
            AddLog($"Could not update overlay speaker state: {exception.Message}");
        }
    }

    private static string CleanSpeakerField(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private async void DisconnectDiscordButton_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectDiscordAsync();
    }

    private async Task DisconnectDiscordAsync()
    {
        await DisconnectDiscordCoreAsync(writeLog: true);
        DeleteProtectedToken();
    }

    private async Task DisposeDiscordRpcAsync()
    {
        DiscordRpcClient? rpc = _discordRpc;
        _discordRpc = null;
        if (rpc is not null)
        {
            await rpc.DisposeAsync();
        }
    }

    private async Task DisconnectDiscordCoreAsync(bool writeLog)
    {
        _discordConnectCancellation?.Cancel();
        _discordConnectCancellation?.Dispose();
        _discordConnectCancellation = null;

        await DisposeDiscordRpcAsync();

        _discordConnecting = false;
        _discordConnected = false;
        _voiceMembers.Clear();
        _activeSpeakers.Clear();
        _speakerGenerations.Clear();
        ClearSpeakerState();
        ResetDiscordUi();
        if (writeLog)
        {
            AddLog("Discord disconnected.");
        }
        UpdateControls();
    }

    private void ResetDiscordUi(bool error = false)
    {
        _discordConnected = false;
        DiscordStatusText.Text = error ? "Error" : "Disconnected";
        DiscordStatusText.Foreground = error
            ? System.Windows.Media.Brushes.OrangeRed
            : System.Windows.Media.Brushes.LightGray;
        DiscordAccountText.Text = "Not connected";
        DiscordVoiceText.Text = "Join a voice channel.";
    }

    private void SaveProtectedToken(DiscordOAuthToken token)
    {
        try
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(token);
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_tokenPath, protectedBytes);
        }
        catch (Exception exception)
        {
            AddLog($"Could not securely save Discord authorization: {exception.Message}");
        }
    }

    private DiscordOAuthToken? LoadProtectedToken()
    {
        try
        {
            if (!File.Exists(_tokenPath))
            {
                return null;
            }

            byte[] protectedBytes = File.ReadAllBytes(_tokenPath);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            DiscordOAuthToken? token = JsonSerializer.Deserialize<DiscordOAuthToken>(plain);
            if (token?.IsUsable == true)
            {
                return token;
            }

            DeleteProtectedToken();
            return null;
        }
        catch
        {
            DeleteProtectedToken();
            return null;
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
        AddLog($"Loading overlay into PID {wowProcess.ProcessId}...");

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
                "Start the overlay first, then click Preview Overlay.",
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_discordConnected || _discordConnecting)
        {
            System.Windows.MessageBox.Show(
                "Disconnect Discord before previewing the overlay.",
                "Ashen Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        File.WriteAllText(
            _speakerStatePath,
            $"Methl\t{Environment.NewLine}Danari\t{Environment.NewLine}Poetry\t",
            Encoding.UTF8);
        AddLog("Overlay preview started.");
        PreviewOverlayButton.IsEnabled = false;
        await Task.Delay(TimeSpan.FromSeconds(8));
        ClearSpeakerState();
        PreviewOverlayButton.IsEnabled = true;
        AddLog("Overlay preview finished.");
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
            File.WriteAllText(_speakerStatePath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
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

        if (!_discordConnected && !_discordConnecting)
        {
            DiscordStatusText.Text = "Disconnected";
            DiscordStatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        }

        StartButton.IsEnabled = !_serviceRunning;
        StopButton.IsEnabled = _serviceRunning;
        StartOverlayButton.IsEnabled = !_overlayActive && !_overlayStarting && _detectedWowProcessId is not null;
        StopOverlayButton.IsEnabled = _overlayActive && !_overlayStarting;
        ConnectDiscordButton.IsEnabled = !_discordConnected && !_discordConnecting;
        DisconnectDiscordButton.IsEnabled = _discordConnected || _discordConnecting || _discordRpc is not null;
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
        _httpClient.Dispose();
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
        _httpClient.Dispose();
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
        _discordConnectCancellation?.Cancel();
        if (_discordRpc is not null)
        {
            _ = _discordRpc.DisposeAsync();
            _discordRpc = null;
        }
    }
}

public sealed record DetectedWowProcess(int ProcessId, string ProcessName);

internal sealed record ActiveSpeaker(string UserId, string DisplayName, string AvatarPath, DateTimeOffset StartedAt);

public sealed class AppSettings
{
    public bool MinimizeToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public List<string> ProcessNames { get; set; } = new()
    {
        "Wow",
        "WoW",
        "OctoWoW"
    };

    public string OverlayPosition { get; set; } = "TopRight";

    public int HorizontalOffset { get; set; } = 18;

    public int VerticalOffset { get; set; } = 34;

    public double OverlayScale { get; set; } = 1.0;

    public int CardWidth { get; set; } = 218;

    public int AvatarSize { get; set; } = 36;

    public int FontSize { get; set; } = 17;

    public int BackgroundOpacity { get; set; } = 70;

    public int RingThickness { get; set; } = 3;

    public int MaximumSpeakers { get; set; } = 5;

    public int SpeakerHideDelayMs { get; set; } = 500;

    public bool ShowAvatars { get; set; } = true;

    public bool ShowNames { get; set; } = true;
}
