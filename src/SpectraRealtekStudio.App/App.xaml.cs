using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace SpectraRealtekStudio.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Local\SpectraRealtekStudio_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Spectra Realtek Studio is already running.\nCheck your Windows System Tray to open the control window.",
                "Spectra Realtek Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        bool startMinimized = e.Args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        var mainWindow = new MainWindow(startMinimized);
        MainWindow = mainWindow;

        if (startMinimized)
        {
            // Initialize HWND and tray icon without flashing the main window on startup
            mainWindow.WindowState = WindowState.Minimized;
            mainWindow.ShowInTaskbar = false;
            mainWindow.Show();
            mainWindow.Hide();
            mainWindow.ShowInTaskbar = true;
        }
        else
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
        }
        base.OnExit(e);
    }
}
