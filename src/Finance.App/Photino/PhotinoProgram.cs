using System.Net.Sockets;

namespace ProductivityApp.Host;

/// <summary>
/// Start on macOS and Linux: one window per data folder (a second launch brings the first one forward), the engine,
/// then the Photino window until it closes.
/// </summary>
internal static class Program
{
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
        if (options.Capture is not null)
        {
            Console.Error.WriteLine("--capture is only available on Windows.");
            return 2;
        }

        Directory.CreateDirectory(options.DataDir);
        using var instance = SingleInstance.Claim(options);
        if (instance is null) return 0;

        new Thread(UnpackedCopies.RemoveOld) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();

        using var services = new AppServices(options.DataDir, $"version: {typeof(Program).Assembly.GetName().Version}\nexe: {Environment.ProcessPath}\nprocessId: {Environment.ProcessId}\nargs: {string.Join(' ', args)}\ndataDir: {options.DataDir}\nos: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nruntime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        _ = Task.Run(services.Warm);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) services.Log(ex, "fatal"); };
        TaskScheduler.UnobservedTaskException += (_, e) => { services.Log(e.Exception, "task"); e.SetObserved(); };

        var window = new PhotinoHost(options, services);
        instance.Activated += () =>
        {
            services.Logs.Info("app", "SecondInstance", "a second launch asked this window to come forward");
            window.BringForward();
        };
        try { window.Run(); }
        catch (Exception ex)
        {
            services.Log(ex, "startup");
            Console.Error.WriteLine("The window could not start. The details were written to " + services.LogPath);
            return 1;
        }
        return 0;
    }
}

/// <summary>
/// One running window per data folder, through a local socket in that folder: the first launch listens on it, a second
/// one connects, asks the first to come forward and ends.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;

    public event Action? Activated;

    private SingleInstance(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
        new Thread(Listen) { IsBackground = true, Name = "SingleInstance" }.Start();
    }

    /// <summary>This launch's claim on its data folder, or null when another window already has it (asked forward).</summary>
    public static SingleInstance? Claim(AppOptions options)
    {
        var path = Path.Combine(options.DataDir, options.IsolatedRun && options.DataDir == AppOptions.DefaultDataDir ? $"window-{Environment.ProcessId}.sock" : "window.sock");
        var endpoint = new UnixDomainSocketEndPoint(path);
        if (File.Exists(path))
        {
            try
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(endpoint);
                client.Send([1]);
                return null;
            }
            // Nobody listens: a window that ended without removing it.
            catch (SocketException) { File.Delete(path); }
        }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        listener.Listen(4);
        return new SingleInstance(listener, path);
    }

    private void Listen()
    {
        while (true)
        {
            try
            {
                using var other = _listener.Accept();
                Activated?.Invoke();
            }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        try { File.Delete(_path); }
        catch (IOException e) { ProductivityApp.Finance.Services.Diagnostics.Report("SingleInstance.Dispose", e); }
    }
}
