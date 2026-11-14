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
}
