namespace ProductivityApp.Finance.Services;

/// <summary>
/// Exceptions the engine catches and turns into a message or a fallback (an address that cannot be reached, a file
/// that is not what it says): each one is reported here in full, for the log, before it is handled.
/// </summary>
public static class Diagnostics
{
    /// <summary>Raised with where the exception was caught and the exception itself.</summary>
    public static event Action<string, Exception>? Caught;

    public static void Report(string where, Exception e)
    {
        try { Caught?.Invoke(where, e); }
        catch (Exception) { /* reporting must never change what the engine does */ }
    }

    /// <summary>Raised with where and what the engine chose on its own (no failure), for the log's debug entries.</summary>
    public static event Action<string, string>? Noted;

    public static void Note(string where, string what)
    {
        try { Noted?.Invoke(where, what); }
        catch (Exception) { /* reporting must never change what the engine does */ }
    }
}
