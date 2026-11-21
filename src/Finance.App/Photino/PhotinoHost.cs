using System.Security.Cryptography;
using System.Text;
using Photino.NET;

namespace ProductivityApp.Host;

/// <summary>
/// The window on macOS and Linux: a Photino window with the system's web view (WKWebView, WebKitGTK) and the system's
/// own title bar. The interface is served from the executable under app://localhost/; only that page can talk to the
/// engine: each of its messages carries the key written into the page when it was served.
/// </summary>
internal sealed class PhotinoHost : IAppWindow, IPage
{
    /// <summary>
    /// The page's scheme: app:// on macOS and Linux. Photino's WebView2 (Windows, only used to test this window) cannot
    /// navigate to a scheme of its own, so the page is served there under http://app.localhost/, which it serves from
    /// the same handler before anything reaches the network.
    /// </summary>
    private static readonly string Scheme = OperatingSystem.IsWindows() ? "http" : "app";
    public static readonly string AppOrigin = OperatingSystem.IsWindows() ? "http://app.localhost/" : "app://localhost/";

    private readonly AppOptions _options;
    private readonly AppServices _services;
    private readonly PhotinoWindow _window;
    private readonly Bridge _bridge;
    /// <summary>Written into the served page; a message without it is not the app's page.</summary>
    private readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private int _uiThread;
    private volatile bool _closed;
    /// <summary>
    /// What was sent or asked of the window's thread before the page first spoke: the web view cannot take messages before
    /// its page is there (Photino ends the process), so they wait for it.
    /// </summary>
    private readonly List<Action> _early = [];
    private bool _created;

    public PhotinoHost(AppOptions options, AppServices services)
    {
        _options = options;
        _services = services;
        _bridge = new Bridge(this, services.Logs);
        var logsChanged = () => _bridge.Emit("logs.changed", null);
        services.Logs.Changed += logsChanged;
        _bridge.Closed += _ =>
        {
            services.Logs.Changed -= logsChanged;
            services.Logs.Watched = false;
        };

        var debug = false;
#if DEBUG || E2E
        debug = true;
#endif
        _window = new PhotinoWindow
        {
            Title = "Finances",
            Resizable = true,
            ContextMenuEnabled = false,
            DevToolsEnabled = debug,
            MinSize = new System.Drawing.Point(1024, 680),
            UseOsDefaultSize = false,
            UseOsDefaultLocation = false,
            // Only for the test build on Windows (WebView2's own switches): lets the end-to-end check drive the page.
            BrowserControlInitParameters = OperatingSystem.IsWindows() ? TestSwitches(options) : null!,
            TemporaryFilesPath = Path.Combine(options.DataDir, "WebView"),
        };
        // Linux shows this icon on the window (macOS takes the .app's own).
        foreach (var icon in new[] { "/usr/share/icons/hicolor/256x256/apps/productivityapp.png", Path.Combine(AppContext.BaseDirectory, "productivityapp.png") })
            if (File.Exists(icon)) { _window.IconFile = icon; break; }
        Place();
#if DEBUG || E2E
        if (options.Offscreen && OperatingSystem.IsWindows()) KeepInBackground();
#endif
        _window.RegisterCustomSchemeHandler(Scheme, Serve);
        _window.RegisterWebMessageReceivedHandler((_, message) => OnMessage(message));
        _window.RegisterWindowCreatedHandler((_, _) => services.Logs.Info("window", "Created", $"Location: {_window.Location}\nSize: {_window.Size}\nos: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"));
        _window.RegisterMaximizedHandler((_, _) => { services.Logs.Info("window", "Maximized", $"Size: {_window.Size}"); Changed(); });
        _window.RegisterRestoredHandler((_, _) => { services.Logs.Info("window", "Restored", $"Location: {_window.Location}\nSize: {_window.Size}"); Changed(); });
        _window.RegisterWindowClosingHandler((_, _) => { Closing(); return false; });
        Api.Register(_bridge, services, this, nonClientRegions: false, nativeFrame: true);
    }

    /// <summary>Shows the window and returns when it is closed (on the thread that called it, the window's thread).</summary>
    public void Run()
    {
        _uiThread = Environment.CurrentManagedThreadId;
        var start = AppOrigin + "index.html";
#if DEBUG
        if (_options.DevUrl is { } dev) start = dev;
#endif
        // As an address (a text is taken for a web address or a file).
        _window.Load(new Uri(start));
        _window.WaitForClose();
        _closed = true;
    }

    private void Place()
    {
        var wanted = _options.Size ?? new System.Drawing.Size(1440, 940);
        if (_options.Offscreen)
        {
            // Automated checks: a window that is never on screen.
            _window.Size = wanted;
            _window.Location = new System.Drawing.Point(-20000, -20000);
            return;
        }
        var saved = _options.IsolatedRun ? null : _services.Placement();
        _window.Size = saved is { Width: >= 400, Height: >= 300 } ? new System.Drawing.Size(Math.Max(saved.Width, 1024), Math.Max(saved.Height, 680)) : wanted;
        _window.Centered = true;
        if (saved is null) return;
        // Where it was last, once the screens are known: only when that place is still on a screen (one may be gone).
        _window.RegisterWindowCreatedHandler((_, _) =>
        {
            var r = new System.Drawing.Rectangle(saved.X, saved.Y, _window.Width, _window.Height);
            if (_window.Monitors.Any(m => System.Drawing.Rectangle.Intersect(m.WorkArea, r) is { Width: > 200, Height: > 100 }))
                _window.MoveTo(saved.X, saved.Y, false);
            if (saved.Maximized) _window.Maximized = true;
        });
    }

    private void Changed() => _bridge.Emit("window.state", new { maximized = Maximized });

    private void Closing()
    {
        _services.Logs.Info("window", "WindowClosing", $"Location: {_window.Location}\nSize: {_window.Size}\nMaximized: {_window.Maximized}");
        if (!_options.IsolatedRun)
            _services.SavePlacement(new AppServices.WindowPlacement(_window.Left, _window.Top, _window.Width, _window.Height, _window.Maximized));
        _bridge.Close("the window was closed");
        _closed = true;
    }

    private void OnMessage(string message)
    {
        if (!_created)
        {
            List<Action> early;
            lock (_early) { _created = true; early = [.. _early]; _early.Clear(); }
            foreach (var action in early) Safely(action);
        }
        // "<key>\n<request JSON>" from the app's page.
        var cut = message.IndexOf('\n');
        if (cut < 0 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(message[..cut]), Encoding.ASCII.GetBytes(_key)))
        {
            _bridge.Refuse("a page without the app's key", cut < 0 ? message : message[(cut + 1)..]);
            return;
        }
        _bridge.Receive(message[(cut + 1)..]);
    }

    /// <summary>Embedded interface files by URL path ("assets/index.js"); resource names may carry Windows separators.</summary>
    private static readonly Dictionary<string, string> Assets = typeof(PhotinoHost).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("ui/", StringComparison.Ordinal))
        .ToDictionary(n => n[3..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    private Stream Serve(object sender, string scheme, string url, out string contentType)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/') : "";
        if (path.Length == 0) path = "index.html";
        var asm = typeof(PhotinoHost).Assembly;
        var stream = path.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase) ? asm.GetManifestResourceStream("app.ico")
            : Assets.TryGetValue(path, out var resource) ? asm.GetManifestResourceStream(resource) : null;
        if (stream is null)
        {
            if (Path.HasExtension(path))
            {
                _services.Logs.Write("error", "window", "Serve", DateTime.Now, $"Uri: {url}\nResponse: not found");
                contentType = "text/plain";
                return new MemoryStream();
            }
            stream = asm.GetManifestResourceStream(Assets["index.html"])!;
            path = "index.html";
        }
        contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".js" or ".mjs" => "text/javascript",
            ".css" => "text/css",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
        if (path != "index.html") return stream;
        // The page loaded (first or again): the window's own navigation, written to the log.
        _services.Logs.Info("window", "Load", $"Uri: {url}");
        // The page gets the key of its messages and the same content policy as on Windows (as a tag: this web view
        // takes no headers from the app).
        string html;
        using (var reader = new StreamReader(stream, Encoding.UTF8)) html = reader.ReadToEnd();
        const string policy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'";
        html = html.Replace("<head>", $"<head>\n    <meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\" />\n    <meta name=\"bridge-key\" content=\"{_key}\" />", StringComparison.Ordinal);
        return new MemoryStream(Encoding.UTF8.GetBytes(html));
    }

#if DEBUG || E2E
    /// <summary>
    /// Automated checks of this window on Windows (the test build): the window off-screen is never made the active window,
    /// so it never takes the keyboard or comes in front of what the person is using, and has no taskbar button. Done as the
    /// window is made (a hook on this thread's windows): set afterwards, Windows would already have activated it once.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void KeepInBackground()
    {
        _cbt = (code, w, l) =>
        {
            const int HCBT_CREATEWND = 3, HCBT_ACTIVATE = 5;
            if (code == HCBT_ACTIVATE) return 1;
            if (code == HCBT_CREATEWND && IntPtr.Size == 8)
            {
                // CBT_CREATEWND.lpcs -> CREATESTRUCTW: hwndParent at 24, dwExStyle at 72 (64-bit layout).
                var cs = System.Runtime.InteropServices.Marshal.ReadIntPtr(l);
                if (System.Runtime.InteropServices.Marshal.ReadIntPtr(cs, 24) == 0)
                {
                    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
                    var ex = System.Runtime.InteropServices.Marshal.ReadInt32(cs, 72);
                    System.Runtime.InteropServices.Marshal.WriteInt32(cs, 72, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                }
            }
            return CallNextHookEx(0, code, w, l);
        };
        const int WH_CBT = 5;
        SetWindowsHookExW(WH_CBT, _cbt, 0, GetCurrentThreadId());
    }

    /// <summary>The test build's WebView2 switches: DevTools for the end-to-end check; off-screen, drawn as if in front.</summary>
    private static string TestSwitches(AppOptions options)
    {
        var args = new List<string>();
        if (options.Offscreen) args.AddRange(["--disable-backgrounding-occluded-windows", "--disable-renderer-backgrounding", "--disable-features=CalculateNativeWinOcclusion"]);
        if (options.CdpPort is { } port) args.Add($"--remote-debugging-port={port}");
        return args.Count == 0 ? null! : string.Join(' ', args);
    }

    private delegate nint HookProc(int code, nint w, nint l);
    private static HookProc? _cbt;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
#else
    private static string TestSwitches(AppOptions options) => null!;
#endif

    // ---------------------------------------------------------------- IPage

    public bool Gone => _closed;

    public bool OnUiThread => Environment.CurrentManagedThreadId == _uiThread;

    public bool TryInvoke(Action action)
    {
        if (_closed) return false;
        lock (_early) if (!_created) { _early.Add(action); return true; }
        _window.Invoke(() => Safely(action));
        return true;
    }

    /// <summary>
    /// What runs inside the web view's own calls: an exception there would end the process (there is no window error
    /// handler as on Windows), so it is written to the log.
    /// </summary>
    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception e) { _services.Log(e, "window"); }
    }

    /// <summary>A message the web view refuses closes the bridge (the page is gone), written to the log.</summary>
    public void Send(string json)
    {
        lock (_early) if (!_created) { _early.Add(() => Send(json)); return; }
        try { _window.SendWebMessage(json); }
        catch (Exception e)
        {
            _services.Log(e, "window");
            _bridge.Close($"a message could not be sent ({e.GetType().Name})");
        }
    }

    // ---------------------------------------------------------------- IAppWindow (the system's title bar does the rest)

    public bool Maximized => !_closed && _uiThread != 0 && _window.Maximized;

    public void Minimize() => _window.Minimized = true;

    public void ToggleMaximize() => _window.Maximized = !_window.Maximized;

    public void CloseWindow() => _window.Close();

    public void StartDrag() { }

    public void StartResize(string edge) { }

    /// <summary>The system's title bar follows the system's light or dark appearance.</summary>
    public void ApplyTheme(bool dark) { }

    public void BringForward()
    {
        if (_closed) return;
        _window.Invoke(() =>
        {
            if (_window.Minimized) _window.Minimized = false;
            _window.Topmost = true;
            _window.Topmost = false;
        });
    }
}
