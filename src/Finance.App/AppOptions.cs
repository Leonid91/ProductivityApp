namespace ProductivityApp.Host;

/// <summary>Command line switches. Without any, the app opens normally with its data in the user's local app data.</summary>
internal sealed class AppOptions
{
    /// <summary>--data-dir &lt;folder&gt;: keep the database somewhere else (tests use a throw-away folder).</summary>
    public string DataDir { get; init; } = DefaultDataDir;

    /// <summary>--offscreen: run without ever showing a window on screen (automated checks).</summary>
    public bool Offscreen { get; init; }

    /// <summary>--size WxH: window size in pixels, mostly for screenshots.</summary>
    public Size? Size { get; init; }

    /// <summary>--capture &lt;file.png&gt;: save a picture of the interface once it is ready, then quit.</summary>
    public string? Capture { get; init; }

    /// <summary>--cdp-port &lt;port&gt;: debug builds only, lets automated checks drive the interface.</summary>
    public int? CdpPort { get; init; }

    /// <summary>--dev-url &lt;url&gt;: debug builds only, load the interface from the Vite dev server.</summary>
    public string? DevUrl { get; init; }

    public static string DefaultDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductivityApp", "Finances");

    public bool IsolatedRun => Offscreen || Capture is not null || DataDir != DefaultDataDir;

    public static AppOptions Parse(string[] args)
    {
        string? dataDir = null, capture = null, devUrl = null;
        int? port = null;
        Size? size = null;
        var offscreen = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--data-dir": dataDir = Path.GetFullPath(Next()); break;
                case "--offscreen": offscreen = true; break;
                case "--capture": capture = Path.GetFullPath(Next()); break;
                case "--size":
                    var parts = Next().Split('x');
                    size = new Size(int.Parse(parts[0]), int.Parse(parts[1]));
                    break;
#if DEBUG
                case "--cdp-port": port = int.Parse(Next()); break;
                case "--dev-url": devUrl = Next(); break;
#endif
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }
        return new AppOptions { DataDir = dataDir ?? DefaultDataDir, Offscreen = offscreen, Capture = capture, Size = size, CdpPort = port, DevUrl = devUrl };
    }
}
