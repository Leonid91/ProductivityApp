using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

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
    private readonly Action<Exception, string> _log;

    public Bridge(CoreWebView2 web, Control ui, Action<Exception, string> log)
    {
        _web = web;
        _ui = ui;
        _log = log;
        _web.WebMessageReceived += OnMessage;
    }

    public void RegisterAsync(string method, Func<JsonElement, Task<object?>> handler) => _methods[method] = handler;

    public void Register(string method, Func<JsonElement, object?> handler) => _methods[method] = p => Task.FromResult(handler(p));

    /// <summary>Runs on a worker thread, for anything that reads files or PDFs.</summary>
    public void RegisterBackground(string method, Func<JsonElement, Task<object?>> handler) => _methods[method] = p => Task.Run(() => handler(p));

    public void Emit(string name, object? data)
    {
        var json = JsonSerializer.Serialize(new { @event = name, data }, Json);
        if (_ui.InvokeRequired) _ui.BeginInvoke(() => _web.PostWebMessageAsJson(json));
        else _web.PostWebMessageAsJson(json);
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the app's own page may talk to the engine.
        if (!e.Source.StartsWith(MainForm.AppOrigin, StringComparison.OrdinalIgnoreCase) && !MainForm.IsDevSource(e.Source)) return;
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString();
            var method = root.GetProperty("method").GetString() ?? "";
            var args = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            if (!_methods.TryGetValue(method, out var handler)) throw new InvalidOperationException($"Unknown method '{method}'.");
            var result = await handler(args);
            Reply(JsonSerializer.Serialize(new { id, ok = true, result }, Json));
        }
        catch (Exception ex)
        {
            var expected = ex is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException;
            if (!expected) _log(ex, "bridge");
            var message = expected ? ex.Message : "Something went wrong. The details were written to the log.";
            Reply(JsonSerializer.Serialize(new { id, ok = false, error = new { message } }, Json));
        }
    }

    private void Reply(string json)
    {
        if (_ui.IsDisposed) return;
        if (_ui.InvokeRequired) _ui.BeginInvoke(() => _web.PostWebMessageAsJson(json));
        else _web.PostWebMessageAsJson(json);
    }
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
