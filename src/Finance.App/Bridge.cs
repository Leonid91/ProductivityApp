using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Host;

/// <summary>
/// Request/response channel between the interface and the engine over WebView2 web messages.
/// The page sends {id, method, params}; the host answers {id, ok, result} or {id, ok:false, error}.
/// The host can also push {event, data}.
/// </summary>
internal sealed class Bridge
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly CoreWebView2 _web;
    private readonly Control _ui;
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _methods = new(StringComparer.Ordinal);
    private readonly ActionLog _actions;

    /// <summary>
    /// Requests that only read (or move the window by hand): not written to the log when they succeed, so it holds the
    /// actions (what changes something) and every failure. The log's own requests are never written.
    /// </summary>
    private static readonly HashSet<string> Reads = [
        "logs.read", "logs.entry", "logs.write", "logs.watch", "app.init", "app.ready", "window.drag", "window.resize",
        "periods.index", "periods.report", "periods.operations", "periods.summary",
        "holidays.calendars", "holidays.presets", "holidays.total", "holidays.list",
        "rates.status", "rates.sources", "rates.banks", "rates.currencies", "rates.pair",
        "tx.get", "tx.sameOnDay", "brands.list", "tags.list", "banks.list", "accounts.list", "statements.list", "templates.list",
        "fs.places", "fs.list", "report.defaults", "import.page", "import.preview"];
    private static readonly HashSet<string> Unlogged = ["logs.read", "logs.entry", "logs.write", "logs.watch"];

    public Bridge(CoreWebView2 web, Control ui, ActionLog actions)
    {
        _actions = actions;
        _web = web;
        _ui = ui;
        _web.WebMessageReceived += OnMessage;
    }

    public void RegisterAsync(string method, Func<JsonElement, Task<object?>> handler) => _methods[method] = handler;

    public void Register(string method, Func<JsonElement, object?> handler) => _methods[method] = p => Task.FromResult(handler(p));

    /// <summary>Runs on a worker thread, for anything that reads files or PDFs.</summary>
    public void RegisterBackground(string method, Func<JsonElement, Task<object?>> handler) => _methods[method] = p => Task.Run(() => handler(p));

    public void Emit(string name, object? data) => Post(JsonSerializer.Serialize(new { @event = name, data }, Json), "event " + name);

    /// <summary>False once the window closes: its page goes with it (the browser control is shut down).</summary>
    private bool _open = true;

    /// <summary>The window is closing (called on its thread): nothing more is sent to the page.</summary>
    public void Close() => _open = false;

    /// <summary>
    /// Sends a message to the page, from the window's thread. Whether the page is still there is checked when the message
    /// is sent, not when it is asked for: an answer finished on a worker thread, or an event, may wait in the window's queue
    /// while the window closes. Such a message has no page left to reach: it is written to the log (debug), not sent.
    /// </summary>
    private void Post(string json, string what)
    {
        // Who asked for it, read (to text) only if the message cannot be sent.
        var asked = new System.Diagnostics.StackTrace(1, false);
        void Send()
        {
            if (_open && !_ui.IsDisposed) _web.PostWebMessageAsJson(json);
            else NotSent(what, asked);
        }
        if (!_ui.InvokeRequired) { Send(); return; }
        try { _ui.BeginInvoke(Send); }
        // The window is already gone (its handle destroyed): nothing can run on its thread any more.
        catch (InvalidOperationException) when (_ui.IsDisposed || !_ui.IsHandleCreated) { NotSent(what, asked); }
    }

    private void NotSent(string what, System.Diagnostics.StackTrace asked) =>
        _actions.WriteWithStack("debug", "engine", "Not sent to the page", DateTime.Now, asked.ToString().TrimEnd(), what, ": the window was closed before it could be sent.");

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the app's own page may talk to the engine.
        if (!e.Source.StartsWith(MainForm.AppOrigin, StringComparison.OrdinalIgnoreCase) && !MainForm.IsDevSource(e.Source))
        {
            _actions.Write("warning", "engine", "message refused", DateTime.Now, "source: ", e.Source, "\nmessage: ", e.WebMessageAsJson);
            return;
        }
        // The message as received, kept for the log (read here: it can only be read while the event is raised).
        var raw = e.WebMessageAsJson;
        var started = DateTime.Now;
        string? id = null;
        string method = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString();
            method = root.GetProperty("method").GetString() ?? "";
            var args = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            if (!_methods.TryGetValue(method, out var handler)) throw new InvalidOperationException($"Unknown method '{method}'.");
            // Work done on a worker thread is also turned into text there: the window's thread only passes the answer on.
            var result = await handler(args).ConfigureAwait(false);
            var reply = JsonSerializer.Serialize(new { id, ok = true, result }, Json);
            Post(reply, "answer to " + method);
            // In full: the request as received and the answer as sent.
            if (!Reads.Contains(method)) _actions.Write("info", "engine", method, DateTime.Now, Started(started), "\nrequest: ", raw, "\nanswer: ", reply);
        }
        catch (Exception ex)
        {
            var expected = ex is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException;
            var message = expected ? ex.Message : "Something went wrong. The details were written to the log.";
            var reply = JsonSerializer.Serialize(new { id, ok = false, error = new { message } }, Json);
            Post(reply, "answer to " + (method.Length > 0 ? method : "an unreadable message"));
            // The full exception: its type, message, inner exceptions and call stack.
            if (!Unlogged.Contains(method) || !expected)
                _actions.Write(expected ? "warning" : "error", "engine", method.Length > 0 ? method : "unreadable message", DateTime.Now,
                    Started(started), "\nrequest: ", raw, "\nanswer: ", reply, "\nexception: ", ex.ToString());
        }
    }

    private static string Started(DateTime t) => "started: " + t.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

}

internal static class JsonArgs
{
    public static T Get<T>(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new ArgumentException($"Missing '{name}'.");
        return v.Deserialize<T>(Bridge.Json) ?? throw new ArgumentException($"Missing '{name}'.");
    }

    public static T? Opt<T>(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        return v.Deserialize<T>(Bridge.Json);
    }
}
