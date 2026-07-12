using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
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
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _trayIcon;
    private bool _serviceRunning = true;
    private bool _allowClose;
    private bool _lastWowDetected;

    public MainWindow()
    {
        InitializeComponent();

        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AshenVoice");

        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");

        LoadSettings();
        ConfigureTrayIcon();

        _processTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _processTimer.Tick += (_, _) => CheckForWow();
        _processTimer.Start();

        Loaded += (_, _) =>
        {
            AddLog("Ashen Voice started.");
            AddLog("Phase 1 is active. Overlay and Discord integration are intentionally disabled until later phases.");
            CheckForWow(true);
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
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;
    }

    private void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);

            if (File.Exists(_settingsPath))
            {
                _settings =
                    JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(_settingsPath))
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
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(_settingsDirectory);

        File.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(
                _settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        _settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;

        SetStartupRegistration(_settings.StartWithWindows);

        SaveSettings();
    }

    private void ProcessNamesTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var names = ProcessNamesTextBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            names.Add("Wow");

        _settings.ProcessNames = names;

        ProcessNamesTextBox.Text = string.Join(", ", names);

        SaveSettings();

        AddLog("Updated WoW process names.");

        CheckForWow(true);
    }

    private static void SetStartupRegistration(bool enabled)
    {
        using var key =
            Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                true);

        if (key == null)
            return;

        if (enabled)
        {
            key.SetValue(
                "AshenVoice",
                $"\"{Environment.ProcessPath}\" --minimized");
        }
        else
        {
            key.DeleteValue("AshenVoice", false);
        }
    }

    private void CheckForWow(bool forceLog = false)
    {
        bool detected = _settings.ProcessNames.Any(name =>
        {
            try
            {
                return Process.GetProcessesByName(name).Length > 0;
            }
            catch
            {
                return false;
            }
        });

        WowStatusText.Text =
            detected
                ? "Detected"
                : "Not detected";

        WowStatusText.Foreground =
            detected
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.LightGray;

        if (forceLog || detected != _lastWowDetected)
        {
            AddLog(
                detected
                    ? "World of Warcraft client detected."
                    : "World of Warcraft client not detected.");
        }

        _lastWowDetected = detected;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _serviceRunning = true;

        _processTimer.Start();

        AddLog("Ashen Voice monitoring started.");

        CheckForWow(true);

        UpdateControls();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _serviceRunning = false;

        _processTimer.Stop();

        WowStatusText.Text = "Monitoring stopped";

        WowStatusText.Foreground =
            System.Windows.Media.Brushes.LightGray;

        AddLog("Ashen Voice monitoring stopped.");

        UpdateControls();
    }

    private void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForWow(true);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void UpdateControls()
    {
        AppStatusText.Text =
            _serviceRunning
                ? "Running"
                : "Stopped";

        AppStatusText.Foreground =
            _serviceRunning
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.OrangeRed;

        StartButton.IsEnabled = !_serviceRunning;
        StopButton.IsEnabled = _serviceRunning;
    }

    private void AddLog(string message)
    {
        LogTextBox.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");

        LogTextBox.ScrollToEnd();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized &&
            _settings.MinimizeToTray)
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
        _trayIcon?.Dispose();

        System.Windows.Application.Current.Shutdown();
    }
}

public sealed class AppSettings
{
    public bool MinimizeToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public List<string> ProcessNames { get; set; } =
        new()
        {
            "Wow",
            "WoW",
            "OctoWoW"
        };
}