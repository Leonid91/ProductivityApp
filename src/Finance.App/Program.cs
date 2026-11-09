namespace ProductivityApp.Host;

internal static class Program
{
    private const string InstanceName = @"Local\ProductivityApp.Finances";

    [STAThread]
    private static int Main(string[] args)
    {
        AppOptions options;
        try { options = AppOptions.Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // One window per data folder: a second launch brings the first one forward.
        using var mutex = new Mutex(true, InstanceName + (options.IsolatedRun ? "." + Environment.ProcessId : ""), out var first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Activate" + (options.IsolatedRun ? "." + Environment.ProcessId : ""));
        if (!first)
        {
            activate.Set();
            return 0;
        }

        // Copies of earlier builds unpacked on drive C: are deleted in the background.
        new Thread(UnpackedCopies.RemoveOld) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();

        ApplicationConfiguration.Initialize();
        using var services = new AppServices(options.DataDir);
        Application.ThreadException += (_, e) => services.Log(e.Exception, "ui");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) services.Log(ex, "fatal"); };
        TaskScheduler.UnobservedTaskException += (_, e) => { services.Log(e.Exception, "task"); e.SetObserved(); };

        using var form = new MainForm(options, services);
        var watcher = new Thread(() =>
        {
            while (activate.WaitOne())
            {
                if (form.IsDisposed) return;
                form.BeginInvoke(form.BringToFront2);
            }
        }) { IsBackground = true };
        watcher.Start();
        Application.Run(form);
        return 0;
    }
}
