namespace ProductivityApp.Host;

/// <summary>The page of the window, as the bridge reaches it (WebView2 on Windows, Photino on macOS and Linux).</summary>
internal interface IPage
{
    /// <summary>The window is gone: nothing can be sent any more.</summary>
    bool Gone { get; }

    /// <summary>Called on the window's thread (messages are sent from it).</summary>
    bool OnUiThread { get; }

    /// <summary>Runs on the window's thread later; false when the window is already gone.</summary>
    bool TryInvoke(Action action);

    /// <summary>Sends a JSON message to the page (throws when the web view refuses it).</summary>
    void Send(string json);
}

/// <summary>What the interface can ask of its window.</summary>
internal interface IAppWindow
{
    bool Maximized { get; }
    void Minimize();
    void ToggleMaximize();
    void CloseWindow();
    void StartDrag();
    void StartResize(string edge);
    void ApplyTheme(bool dark);
}
