using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ProductivityApp.Host;

/// <summary>
/// Borderless window: the whole surface is the web interface, including its own title bar. The native frame
/// is removed in WM_NCCALCSIZE but the window keeps its resize/caption styles, so Windows still gives it a
/// shadow, rounded corners, snapping, Win+arrow keys and the minimise/maximise animations.
/// </summary>
internal sealed class MainForm : Form
{
    public const string AppHost = "finances.localhost";
    public const string AppOrigin = "https://" + AppHost + "/";

    private readonly AppOptions _options;
    private readonly AppServices _services;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private Bridge? _bridge;
    private static string? _devOrigin;

    public MainForm(AppOptions options, AppServices services)
    {
        _options = options;
        _services = services;
        Text = "Finances";
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) if (s is not null) Icon = new Icon(s);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(1024, 680);
        StartPosition = FormStartPosition.Manual;
        var dark = services.Setting("theme") == "dark";
        BackColor = dark ? Color.FromArgb(0x16, 0x16, 0x15) : Color.FromArgb(0xF6, 0xF5, 0xF2);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        PlaceWindow();
        Resize += (_, _) => _bridge?.Emit("window.state", new { maximized = WindowState == FormWindowState.Maximized });
    }

    public static bool IsDevSource(string source) => _devOrigin is not null && source.StartsWith(_devOrigin, StringComparison.OrdinalIgnoreCase);

    private void PlaceWindow()
    {
        if (_options.Offscreen)
        {
            var size = _options.Size ?? new Size(1440, 920);
            var vs = SystemInformation.VirtualScreen;
            Bounds = new Rectangle(vs.Left - size.Width - 400, vs.Top, size.Width, size.Height);
            ShowInTaskbar = false;
            return;
        }
        var saved = _options.IsolatedRun ? null : _services.Placement();
        var area = Screen.PrimaryScreen!.WorkingArea;
        var wanted = _options.Size ?? new Size(Math.Min(1440, area.Width - 80), Math.Min(940, area.Height - 60));
        var bounds = new Rectangle(area.Left + (area.Width - wanted.Width) / 2, area.Top + (area.Height - wanted.Height) / 2, wanted.Width, wanted.Height);
        if (saved is not null)
        {
            var r = new Rectangle(saved.X, saved.Y, Math.Max(saved.Width, MinimumSize.Width), Math.Max(saved.Height, MinimumSize.Height));
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r) && Rectangle.Intersect(sc.WorkingArea, r).Width > 200)) bounds = r;
            if (saved.Maximized) WindowState = FormWindowState.Maximized;
        }
        Bounds = bounds;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Also called by the base constructor, before the options are set.
            if (_options?.Offscreen == true) cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => _options?.Offscreen == true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var margins = new Native.MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        ApplyTheme(_services.Setting("theme") == "dark");
    }

    public void ApplyTheme(bool dark)
    {
        var flag = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int));
        // COLORREF is 0x00BBGGRR.
        var border = dark ? 0x00302F2D : 0x00D9DAD6;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        BackColor = dark ? Color.FromArgb(0x16, 0x16, 0x15) : Color.FromArgb(0xF6, 0xF5, 0xF2);
        _web.DefaultBackgroundColor = BackColor;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                if (WindowState == FormWindowState.Maximized)
                {
                    // A maximised window hangs its frame outside the monitor; clip the client to the work area.
                    var p = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
                    var info = new Native.MONITORINFO { CbSize = Marshal.SizeOf<Native.MONITORINFO>() };
                    if (Native.GetMonitorInfo(Native.MonitorFromWindow(Handle, Native.MONITOR_DEFAULTTONEAREST), ref info))
                    {
                        p.Rgrc0 = info.Work;
                        Marshal.StructureToPtr(p, m.LParam, false);
                    }
                }
                m.Result = IntPtr.Zero;
                return;
            case Native.WM_NCACTIVATE:
                // Keep Windows from painting a native frame when focus changes.
                m.LParam = new IntPtr(-1);
                base.WndProc(ref m);
                return;
            case Native.WM_SYSCOMMAND when ((int)m.WParam & 0xFFF0) is Native.SC_KEYMENU or Native.SC_MOUSEMENU:
            case Native.WM_NCRBUTTONUP:
            case 0x007B: // WM_CONTEXTMENU: no native system menu anywhere.
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFatal("This app needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10 and 11. Install it from Microsoft, then start the app again.");
        }
        catch (Exception ex)
        {
            _services.Log(ex, "startup");
            ShowFatal("The interface could not start. The details were written to " + _services.LogPath);
        }
    }

    private string? _fatal;

    /// <summary>Last-resort message painted on the window itself (no system dialog).</summary>
    private void ShowFatal(string message)
    {
        _fatal = message;
        _web.Visible = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_fatal is null) return;
        using var font = new Font("Segoe UI", 11f);
        TextRenderer.DrawText(e.Graphics, _fatal, font, ClientRectangle with { X = 48, Y = 48, Width = ClientSize.Width - 96, Height = ClientSize.Height - 96 },
            ForeColor, TextFormatFlags.WordBreak);
    }

    private async Task InitializeWebViewAsync()
    {
        var args = new List<string>();
        if (_options.Offscreen)
        {
            // The window is off-screen; keep the page rendering as if it were in front.
            args.Add("--disable-backgrounding-occluded-windows");
            args.Add("--disable-renderer-backgrounding");
            args.Add("--disable-features=CalculateNativeWinOcclusion");
        }
        if (_options.CdpPort is { } port) args.Add($"--remote-debugging-port={port}");

        var userData = _options.IsolatedRun
            ? Path.Combine(_options.DataDir, "WebView2")
            : Path.Combine(Path.GetDirectoryName(_options.DataDir)!, "WebView2");
        var envOptions = new CoreWebView2EnvironmentOptions(string.Join(' ', args)) { AreBrowserExtensionsEnabled = false };
        var env = await CoreWebView2Environment.CreateAsync(null, userData, envOptions);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;

        var s = core.Settings;
        var debug = false;
#if DEBUG || E2E
        debug = true;
#endif
        s.AreDevToolsEnabled = debug;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;
        s.AreDefaultScriptDialogsEnabled = false;
        var nonClientRegions = true;
        try { s.IsNonClientRegionSupportEnabled = true; }
        catch (NotImplementedException) { nonClientRegions = false; }

        core.AddWebResourceRequestedFilter(AppOrigin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => ServeAsset(env, e);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase) && !IsDevSource(e.Uri)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;

        _bridge = new Bridge(core, this, _services.Log);
        Api.Register(_bridge, _services, this, nonClientRegions);

        if (_options.Capture is not null)
        {
            _bridge.RegisterAsync("app.ready", async _ =>
            {
                await Task.Delay(600);
                await using (var file = File.Create(_options.Capture))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                BeginInvoke(Close);
                return null;
            });
        }

        var start = AppOrigin + "index.html";
#if DEBUG
        if (_options.DevUrl is { } dev)
        {
            _devOrigin = new Uri(dev).GetLeftPart(UriPartial.Authority) + "/";
            start = dev;
        }
#endif
        core.Navigate(start);
    }

    /// <summary>Embedded interface files by URL path ("assets/index.js"); resource names may carry Windows separators.</summary>
    private static readonly Dictionary<string, string> Assets = typeof(MainForm).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("ui/", StringComparison.Ordinal))
        .ToDictionary(n => n[3..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    private static void ServeAsset(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (path.Length == 0) path = "index.html";
        var asm = typeof(MainForm).Assembly;
        var stream = Assets.TryGetValue(path, out var resource) ? asm.GetManifestResourceStream(resource) : null;
        if (stream is null)
        {
            // Unknown paths fall back to the page itself; unknown files with an extension are a 404.
            if (Path.HasExtension(path)) { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
            stream = asm.GetManifestResourceStream(Assets["index.html"])!;
            path = "index.html";
        }
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
        var headers = $"Content-Type: {mime}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                      "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        e.Response = env.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    // ---------------------------------------------------------------- window commands used by the title bar

    public void StartDrag()
    {
        if (!Native.LeftButtonDown) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
    }

    public void StartResize(string edge)
    {
        if (WindowState != FormWindowState.Normal || !Native.LeftButtonDown) return;
        var ht = edge switch
        {
            "left" => Native.HTLEFT, "right" => Native.HTRIGHT, "top" => Native.HTTOP, "bottom" => Native.HTBOTTOM,
            "top-left" => Native.HTTOPLEFT, "top-right" => Native.HTTOPRIGHT, "bottom-left" => Native.HTBOTTOMLEFT, "bottom-right" => Native.HTBOTTOMRIGHT,
            _ => throw new ArgumentException("Unknown edge."),
        };
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, ht, IntPtr.Zero);
    }

    public void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    public bool Maximized => WindowState == FormWindowState.Maximized;

    public void BringToFront2()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_options.IsolatedRun)
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _services.SavePlacement(new AppServices.WindowPlacement(r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized));
        }
        base.OnFormClosing(e);
    }
}
