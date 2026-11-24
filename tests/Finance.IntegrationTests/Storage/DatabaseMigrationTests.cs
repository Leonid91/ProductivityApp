using Microsoft.Data.Sqlite;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.IntegrationTests.Storage;

/// <summary>A database file made by an earlier version of the app, opened by this one: it is brought up to date, once.</summary>
public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "finances-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_folder, "test.db");

    public DatabaseMigrationTests() => new Database(DatabasePath).Dispose();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    /// <summary>Writes to the file as an earlier version of the app left it.</summary>
    private void AsEarlierVersion(string sql)
    {
        using var cn = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private T Opened<T>(Func<Database, T> read)
    {
        using var db = new Database(DatabasePath);
        return read(db);
    }

    [Fact]
    public void Constructor_VersionWithoutTheFederalReserve_AddsItAfterTheBanksAlreadyThere()
    {
        AsEarlierVersion("DELETE FROM rate_sources WHERE id = 'fed'; UPDATE rate_sources SET position = 5 WHERE id = 'ecb'; PRAGMA user_version = 14;");

        var sources = Opened(db => new ExchangeRates(db).Sources().Select(x => x.Id).ToList());

        Assert.Equal(["boc", "ecb", "fed"], sources);
    }

    [Fact]
    public void Constructor_VersionWithoutTheFederalReserveOpenedTwice_HasItOnce()
    {
        AsEarlierVersion("DELETE FROM rate_sources WHERE id = 'fed'; PRAGMA user_version = 14;");
        Opened(db => db.Banks());

        var sources = Opened(db => new ExchangeRates(db).Sources());

        Assert.Single(sources, x => x.Format == "fed");
    }

    [Fact]
    public void Constructor_VersionMadeBeforeHolidays_GetsTheFourCalendarsToStartWith()
    {
        AsEarlierVersion("DROP TABLE holiday_calendars; DROP TABLE IF EXISTS holiday_sets; PRAGMA user_version = 15;");

        var calendars = Opened(db => new Holidays(db).Calendars().Select(c => c.Id).ToList());

        Assert.Equal(["ca", "qc", "us", "fr"], calendars);
    }

    /// <summary>Version 16: three calendars, Québec downloaded from its old address into the database, France renamed by the person.</summary>
    private void AsVersion16() => AsEarlierVersion("""
        DROP TABLE holiday_calendars; DROP TABLE IF EXISTS holiday_sets;
        CREATE TABLE holiday_sets (calendar TEXT NOT NULL, slot INTEGER NOT NULL, fetched TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY (calendar, slot));
        CREATE TABLE holiday_calendars (id TEXT PRIMARY KEY, location TEXT NOT NULL, url TEXT NOT NULL, active INTEGER NOT NULL, position INTEGER NOT NULL);
        INSERT INTO holiday_calendars VALUES ('qc', 'Canada - Quebec', 'https://canada-holidays.ca/ics/QC/{year}', 1, 0),
            ('us', 'United States', 'https://www.opm.gov/policy-data-oversight/pay-leave/federal-holidays/holidays.ics', 0, 1),
            ('fr', 'France métropolitaine', 'https://calendrier.api.gouv.fr/jours-feries/metropole.json', 0, 2);
        INSERT INTO holiday_sets VALUES ('qc', 0, '2026-11-10', '[["2026-12-25","Christmas Day"]]');
        PRAGMA user_version = 16;
        """);

    [Fact]
    public void Constructor_Version16_AddsCanadaWithQuebecUnderItAndKeepsThePersonsNames()
    {
        AsVersion16();

        var calendars = Opened(db => new Holidays(db).Calendars());

        Assert.Equal(["Canada (Federal)", "Québec", "United States (Federal)", "France métropolitaine"], calendars.Select(c => c.Location));
        Assert.Equal([true, true, false, false], calendars.Select(c => c.Active));
        Assert.Equal(("ca", HolidayCalendar.Quebec.Url), (calendars[1].Parent, calendars[1].Url));
    }

    [Fact]
    public void Constructor_Version16_DropsTheCopyFromTheOldAddressSoItIsDownloadedAgain()
    {
        AsVersion16();

        var (copies, updated) = Opened(db =>
        {
            var holidays = new Holidays(db);
            return (holidays.Copies(holidays.Find("qc")!).Count, db.GetSetting("holidays.qc.updatedAt"));
        });

        Assert.Equal((0, (string?)null), (copies, updated));
    }

    [Fact]
    public void Constructor_Version16OpenedTwice_HasTheFourCalendarsOnce()
    {
        AsVersion16();
        Opened(db => db.Banks());

        var count = Opened(db => new Holidays(db).Calendars().Count);

        Assert.Equal(4, count);
    }
}
