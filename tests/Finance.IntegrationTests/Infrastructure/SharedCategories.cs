namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// The categories in use are held by the engine for the whole process (every database that opens replaces them), so the
/// tests that make, change or delete categories run in this collection: alone, after the tests that run in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedCategories
{
    public const string Name = "Shared categories";
}
