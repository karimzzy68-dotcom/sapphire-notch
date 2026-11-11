using System.IO;
using System.Windows;

namespace SapphireNotch.Windows;

public partial class App : Application
{
    private static Mutex? instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One copy only: two copies could both pause the same apps and fight over the hotkeys.
        if (e.Args.Contains("--fps-helper"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Task.Run(() =>
            {
                try { FpsMonitor.RunHelper(); } catch (Exception) { }
                Dispatcher.BeginInvoke(new Action(Shutdown));
            });
            return;
        }
        instanceLock = new Mutex(true, @"Local\SapphireNotchSingleInstance", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }
        // Keep the island alive if a UI callback throws, and leave a trace to diagnose it.
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "errors.log"), $"{DateTime.Now:s} {args.Exception}\r\n\r\n");
            }
            catch (IOException)
            {
            }
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
