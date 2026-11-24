using System.Collections.Concurrent;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// A class fixture for tests that only read what one action gave (a file read by the import, a database filled then
/// reported): the action runs once per key, in a database of its own, and every test of the class that asks for that
/// key reads the same result. For actions whose result the tests never change; a test that changes data makes its own
/// <see cref="TestStore"/>.
/// </summary>
public sealed class ReadOnce : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<(TestStore Store, Task<object> Result)>> _done = new(StringComparer.Ordinal);

    /// <summary>The result of <paramref name="act"/> for <paramref name="key"/>, run once in a database of its own.</summary>
    public async Task<T> GetAsync<T>(string key, Func<TestStore, Task<T>> act) where T : notnull
    {
        var (_, result) = _done.GetOrAdd(key, _ => new Lazy<(TestStore, Task<object>)>(() =>
        {
            var store = new TestStore();
            return (store, Run(store, act));
        })).Value;
        return (T)await result;
    }

    /// <summary>The database <paramref name="key"/>'s action ran in (to read what it saved).</summary>
    public async Task<TestStore> StoreAsync(string key)
    {
        var (store, result) = _done[key].Value;
        await result;
        return store;
    }

    private static async Task<object> Run<T>(TestStore store, Func<TestStore, Task<T>> act) where T : notnull =>
        await act(store).ConfigureAwait(false);

    public void Dispose()
    {
        // Every key's database was made when it was first asked for (GetAsync reads the value at once).
        _done.Values.Select(d => d.Value.Store).ToList().ForEach(s => s.Dispose());
    }
}
