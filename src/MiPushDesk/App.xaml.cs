using Microsoft.UI.Xaml;
using MiPushDesk.Core;
using System.Runtime.InteropServices;

namespace MiPushDesk;

public partial class App : Application
{
    private MainWindow? _window;
    private Mutex? _instance;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, arguments) => AtomicFile.Write(Path.Combine(new AppPaths().Data, "application-error.log"), arguments.Exception.ToString());
    }
    protected override void OnLaunched(LaunchActivatedEventArgs arguments)
    {
        var paths = new AppPaths();
        _instance = new Mutex(true, "Local\\MiPushDesk-" + RichNotification.Identifier(paths.Data.ToUpperInvariant()), out var created);
        if (!created)
        {
            var existing = FindWindow(null, "MiPush Desk");
            if (existing != 0) PostMessage(existing, RegisterWindowMessage("MiPushDesk.Show"), 0, 0);
            Exit();
            return;
        }
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Services.NotificationService.AppId);
            _window = new MainWindow(paths);
            _window.Activate();
            if (Environment.GetCommandLineArgs().Contains("--tray")) _window.AppWindow.Hide();
        }
        catch (Exception error)
        {
            AtomicFile.Write(Path.Combine(paths.Data, "startup-error.log"), error.ToString());
            throw;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint word, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
