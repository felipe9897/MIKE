using System.Configuration;
using System.Data;
using System.Windows;
using System.Threading;

namespace Mike.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool translator = e.Args.Any(arg => arg.Equals("--view=translator", StringComparison.OrdinalIgnoreCase) ||
                                            arg.Equals("--view=manga", StringComparison.OrdinalIgnoreCase));
        string mutexName = translator ? "Local\\MikeLocal.Desktop.Translator" : "Local\\MikeLocal.Desktop";
        instanceMutex = new Mutex(true, mutexName, out bool firstInstance);
        if (!firstInstance)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            Shutdown(0);
            return;
        }
        base.OnStartup(e);
        MainWindow = translator ? new TranslationWindow() : new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

