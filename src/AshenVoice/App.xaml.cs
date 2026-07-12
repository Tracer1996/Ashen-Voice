using System.Threading;
using System.Windows;

namespace AshenVoice;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\AshenVoice_SingleInstance",
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Ashen Voice is already running. Check the system tray.",
                "Ashen Voice",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
