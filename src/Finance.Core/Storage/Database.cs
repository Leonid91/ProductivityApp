using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Storage;

/// <summary>
/// Local SQLite store. Money is written as exact decimal text and only ever added up in C# <see cref="decimal"/>,
/// never by SQLite (which would use floating point).
/// </summary>
public sealed class Database : IDisposable
{
    private const int SchemaVersion = 19;
    private readonly SqliteConnection _cn;
    private readonly Lock _gate = new();

    public string Path { get; }

    /// <summary>The folder of the files downloaded for the exchange rates and the holidays, next to the database.</summary>
    public string DownloadsFolder => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "downloads");

    public Database(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _cn.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;");
        Migrate();
        SeedCategories();
    }

    public void Dispose()
    {
        foreach (var cmd in _commands.Values) cmd.Dispose();
        _commands.Clear();
        _cn.Dispose();
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidOperationException("The data file was created by a newer version of the app.");
        if (version < 1)
        {
            Exec("""
                CREATE TABLE accounts (
                    id INTEGER PRIMARY KEY, name TEXT NOT NULL, currency TEXT NOT NULL, kind INTEGER NOT NULL, bank TEXT, last4 TEXT);
                CREATE TABLE statements (
                    id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    file_name TEXT NOT NULL, file_hash TEXT NOT NULL, period_start TEXT, period_end TEXT,
                    opening TEXT, closing TEXT, verified INTEGER NOT NULL, method TEXT NOT NULL, imported_at TEXT NOT NULL);
                CREATE UNIQUE INDEX ux_statements_hash ON statements(file_hash);
                CREATE TABLE transactions (
                    id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    statement_id INTEGER REFERENCES statements(id) ON DELETE CASCADE,
                    date TEXT NOT NULL, amount TEXT NOT NULL, currency TEXT NOT NULL, raw_label TEXT NOT NULL,
                    merchant_key TEXT NOT NULL, brand TEXT NOT NULL, brand_source INTEGER NOT NULL,
                    category_id TEXT NOT NULL, category_source INTEGER NOT NULL, note TEXT NOT NULL DEFAULT '');
                CREATE INDEX ix_transactions_date ON transactions(date);
                CREATE INDEX ix_transactions_key ON transactions(merchant_key);
                CREATE TABLE brand_rules (
                    id INTEGER PRIMARY KEY, merchant_key TEXT NOT NULL, brand TEXT NOT NULL, category_id TEXT, updated_at TEXT NOT NULL);
                CREATE UNIQUE INDEX ux_brand_rules ON brand_rules(merchant_key, brand) WHERE merchant_key = '';
                CREATE UNIQUE INDEX ux_brand_rules_key ON brand_rules(merchant_key) WHERE merchant_key <> '';
                CREATE TABLE templates (
                    id INTEGER PRIMARY KEY, name TEXT NOT NULL, match_text TEXT NOT NULL, columns TEXT NOT NULL,
                    date_order INTEGER NOT NULL, kind INTEGER NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version = 1;
                """);
        }
        if (version < 2)
        {
            Exec("ALTER TABLE transactions ADD COLUMN bank_category TEXT NOT NULL DEFAULT ''; PRAGMA user_version = 2;");
        }
        if (version < 3)
        {
            // linked_id: the other half of a movement between own accounts.
            Exec("ALTER TABLE transactions ADD COLUMN linked_id INTEGER; PRAGMA user_version = 3;");
        }
        if (version < 4)
        {
            // An earlier version could add invented "estimated" lines: they are removed, with their column.
            var hasEstimated = Query("PRAGMA table_info(transactions)", r => r.GetString(1)).Contains("estimated");
            if (hasEstimated) Exec("DELETE FROM transactions WHERE estimated = 1; ALTER TABLE transactions DROP COLUMN estimated;");
            Exec("PRAGMA user_version = 4;");
        }
        if (version < 5)
        {
            // earned: the part of money back from an investment that the person says was gain (exact decimal text).
            Exec("ALTER TABLE transactions ADD COLUMN earned TEXT; PRAGMA user_version = 5;");
        }
        if (version < 6)
        {
            // Several banks, each with its banking system and currency; categories the person can nest and colour.
            Exec("""
                CREATE TABLE banks (id INTEGER PRIMARY KEY, name TEXT NOT NULL, country TEXT NOT NULL, system INTEGER NOT NULL, currency TEXT NOT NULL);
                ALTER TABLE accounts ADD COLUMN bank_id INTEGER REFERENCES banks(id) ON DELETE SET NULL;
                CREATE TABLE categories (id TEXT PRIMARY KEY, name TEXT NOT NULL, kind INTEGER NOT NULL, parent_id TEXT, color TEXT, seq INTEGER NOT NULL);
                UPDATE accounts SET bank = 'National Bank of Canada' WHERE bank = 'Banque Nationale';
                """);
            // The banks the accounts were imported from become the bank list, with the one banking system used so far.
            var system = GetSetting("bankingSystem") == "france" ? BankingSystem.France : BankingSystem.NorthAmerica;
            // The table as this version made it (its date format comes with version 19).
            void Add(string name, string country, BankingSystem s, string currency) =>
                Exec("INSERT INTO banks(name, country, system, currency) VALUES($n, $c, $s, $cu)", ("$n", name), ("$c", country), ("$s", (int)s), ("$cu", currency));
            Add(KnownBanks.National, "Canada", BankingSystem.NorthAmerica, "CAD");
            foreach (var (name, currency) in Query("SELECT bank, MIN(currency) FROM accounts WHERE bank IS NOT NULL AND bank <> $n GROUP BY bank ORDER BY MIN(id)",
                r => (r.GetString(0), r.GetString(1)), ("$n", KnownBanks.National)))
                Add(name, "", system, currency);
            Exec("UPDATE accounts SET bank_id = (SELECT b.id FROM banks b WHERE b.name = accounts.bank); PRAGMA user_version = 6;");
        }
        if (version < 7)
        {
            // Counting a statement's operations and deleting a statement or an account no longer read every operation.
            Exec("""
                CREATE INDEX IF NOT EXISTS ix_transactions_statement ON transactions(statement_id);
                CREATE INDEX IF NOT EXISTS ix_transactions_account ON transactions(account_id, date);
                PRAGMA user_version = 7;
                """);
        }
        if (version < 8)
        {
            // An account is always at a bank. Accounts without one (the former "Cash & manual entries") are removed
            // when empty; with operations, they go to the bank of the first account that has one (National Bank if none).
            Exec("""
                DELETE FROM accounts WHERE bank_id IS NULL AND NOT EXISTS (SELECT 1 FROM transactions t WHERE t.account_id = accounts.id);
                UPDATE accounts SET bank_id = COALESCE(
                    (SELECT bank_id FROM accounts a WHERE a.bank_id IS NOT NULL ORDER BY a.id LIMIT 1),
                    (SELECT id FROM banks WHERE name = $n ORDER BY id LIMIT 1))
                WHERE bank_id IS NULL;
                PRAGMA user_version = 8;
                """, ("$n", KnownBanks.National));
        }
        if (version < 9)
        {
            // "Exceptional" is gone: its categories are ordinary expenses, and "Other exceptional" joins "Other expenses".
            Exec("""
                UPDATE transactions SET category_id = 'other' WHERE category_id = 'exceptional';
                UPDATE brand_rules SET category_id = 'other' WHERE category_id = 'exceptional';
                UPDATE categories SET parent_id = NULL WHERE parent_id = 'exceptional';
                DELETE FROM categories WHERE id = 'exceptional';
                DELETE FROM settings WHERE key = 'section.exceptional';
                PRAGMA user_version = 9;
                """);
        }
        if (version < 10)
        {
            // Tags the person makes, on operations and on categories (every operation of the category carries them).
            Exec("""
                CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, color TEXT, seq INTEGER NOT NULL);
                CREATE TABLE operation_tags (operation_id INTEGER NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
                    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE, PRIMARY KEY (operation_id, tag_id));
                CREATE TABLE category_tags (category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
                    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE, PRIMARY KEY (category_id, tag_id));
                PRAGMA user_version = 10;
                """);
        }
        if (version < 11)
        {
            // Euro reference rates of the European Central Bank, one row per currency: "date=rate;..." (units per euro).
            Exec("CREATE TABLE rates (currency TEXT PRIMARY KEY, data TEXT NOT NULL); PRAGMA user_version = 11;");
            // Card categories named for both banking systems (France has no credit cards), unless the person renamed them.
            Exec("UPDATE categories SET name = 'Card statement payments' WHERE id = 'cardpayments' AND name = 'Credit card payments'");
            Exec("UPDATE categories SET name = 'Card statement spending' WHERE id = 'cardspending' AND name = 'Credit card spending'");
        }
        if (version < 12)
        {
            // Rates of several central banks, each with its latest copy (slot 0) and the one before (slot 1).
            Exec("""
                CREATE TABLE rate_sets (source TEXT NOT NULL, slot INTEGER NOT NULL, currency TEXT NOT NULL, data TEXT NOT NULL,
                    PRIMARY KEY (source, slot, currency));
                INSERT INTO rate_sets(source, slot, currency, data) SELECT 'ecb', 0, currency, data FROM rates;
                DROP TABLE rates;
                UPDATE settings SET key = 'rates.ecb.updatedAt' WHERE key = 'rates.updatedAt';
                UPDATE settings SET key = 'rates.ecb.error' WHERE key = 'rates.error';
                PRAGMA user_version = 12;
                """);
        }
        if (version < 13)
        {
            // The places the exchange rates are downloaded from, defined by the person; the two central banks to start with.
            Exec("CREATE TABLE rate_sources (id TEXT PRIMARY KEY, name TEXT NOT NULL, url TEXT NOT NULL, base TEXT, position INTEGER NOT NULL);");
            var position = 0;
            foreach (var s in RateSource.Defaults)
                Exec("INSERT INTO rate_sources(id, name, url, base, position) VALUES($i, $n, $u, $b, $p)", ("$i", s.Id), ("$n", s.Name), ("$u", s.Url), ("$b", s.Base), ("$p", position++));
            // (the layout of each source's file is added by version 14)
            Exec("PRAGMA user_version = 13;");
        }
        if (version < 14)
        {
            // Each source is one of the central banks the app reads (the layout of its file); each copy of rates keeps
            // the day it was downloaded (one copy per day: the day before and today).
            Exec("""
                ALTER TABLE rate_sources ADD COLUMN format TEXT NOT NULL DEFAULT 'ecb';
                UPDATE rate_sources SET format = CASE WHEN id = 'boc' OR base = 'CAD' THEN 'boc' WHEN base = 'USD' THEN 'fed' ELSE 'ecb' END;
                ALTER TABLE rate_sets ADD COLUMN fetched TEXT;
                UPDATE rate_sets SET fetched = (SELECT substr(value, 1, 10) FROM settings WHERE key = 'rates.' || rate_sets.source || '.updatedAt') WHERE slot = 0;
                PRAGMA user_version = 14;
                """);
        }
        if (version < 15)
        {
            // The US Federal Reserve joins the central banks every database has, after the ones already defined.
            var fed = RateSource.FederalReserve;
            Exec("""
                INSERT INTO rate_sources(id, name, url, base, position, format)
                SELECT $i, $n, $u, $b, COALESCE((SELECT MAX(position) + 1 FROM rate_sources), 0), $f
                WHERE NOT EXISTS (SELECT 1 FROM rate_sources WHERE format = $f OR id = $i)
                """, ("$i", fed.Id), ("$n", fed.Name), ("$u", fed.Url), ("$b", fed.Base), ("$f", fed.Format));
            Exec("PRAGMA user_version = 15;");
        }
        if (version < 16)
        {
            // Calendars of public holidays (Definitions > Holidays): Quebec's checked, the United States' and France's
            // ready to be; each with its latest copy (slot 0) and the one of the day before (slot 1).
            Exec("""
                CREATE TABLE IF NOT EXISTS holiday_calendars (id TEXT PRIMARY KEY, location TEXT NOT NULL, url TEXT NOT NULL, active INTEGER NOT NULL, position INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS holiday_sets (calendar TEXT NOT NULL, slot INTEGER NOT NULL, fetched TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY (calendar, slot));
                """);
            // (the calendars of version 16; version 17 puts Québec under Canada's federal calendar)
            if (Scalar("SELECT COUNT(*) FROM holiday_calendars") is 0L)
                Exec("""
                    INSERT INTO holiday_calendars(id, location, url, active, position) VALUES
                        ('qc', 'Canada - Quebec', 'https://canada-holidays.ca/ics/QC/{year}', 1, 0),
                        ('us', 'United States', 'https://www.opm.gov/policy-data-oversight/pay-leave/federal-holidays/holidays.ics', 0, 1),
                        ('fr', 'France', 'https://calendrier.api.gouv.fr/jours-feries/metropole.json', 0, 2);
                    """);
            Exec("PRAGMA user_version = 16;");
        }
        if (version < 17)
        {
            // A province's or a state's calendar sits under its country's federal one: Canada (Federal) first, checked
            // like Québec, with Québec under it (its holidays with the days off given for weekend ones); the US
            // calendar named as federal. Calendars the person changed keep their place and address.
            if (!Query("PRAGMA table_info(holiday_calendars)", r => r.GetString(1)).Contains("parent"))
                Exec("ALTER TABLE holiday_calendars ADD COLUMN parent TEXT;");
            var ca = HolidayCalendar.Canada;
            var qc = HolidayCalendar.Quebec;
            if (Scalar("SELECT COUNT(*) FROM holiday_calendars WHERE id = $i", ("$i", ca.Id)) is 0L)
            {
                Exec("UPDATE holiday_calendars SET position = position + 1;");
                Exec("INSERT INTO holiday_calendars(id, location, url, active, position) VALUES($i, $l, $u, COALESCE((SELECT active FROM holiday_calendars WHERE id = 'qc'), 1), 0)",
                    ("$i", ca.Id), ("$l", ca.Location), ("$u", ca.Url));
            }
            if (Scalar("SELECT url FROM holiday_calendars WHERE id = 'qc'") as string == "https://canada-holidays.ca/ics/QC/{year}")
            {
                Exec("UPDATE holiday_calendars SET url = $u, parent = $p, location = CASE WHEN location = 'Canada - Quebec' THEN $l ELSE location END WHERE id = 'qc'",
                    ("$u", qc.Url), ("$p", ca.Id), ("$l", qc.Location));
                Exec("DELETE FROM holiday_sets WHERE calendar = 'qc'; DELETE FROM settings WHERE key IN ('holidays.qc.updatedAt', 'holidays.qc.error');");
                _settings = null;
            }
            Exec("UPDATE holiday_calendars SET location = $l WHERE id = 'us' AND location = 'United States'", ("$l", HolidayCalendar.UnitedStates.Location));
            Exec("PRAGMA user_version = 17;");
        }
        if (version < 18)
        {
            // The downloaded rates and holidays are kept as files in their own format next to the database (see
            // DownloadStore), not in tables: the copies kept so far go, and every source is downloaded again.
            Exec("""
                DROP TABLE IF EXISTS rate_sets;
                DROP TABLE IF EXISTS holiday_sets;
                DELETE FROM settings WHERE key LIKE 'rates.%.updatedAt' OR key LIKE 'holidays.%.updatedAt';
                PRAGMA user_version = 18;
                """);
            _settings = null;
        }
        if (version < 19)
        {
            // Each bank has the date format its files are read with, set by the person; to start with, the one of its
            // country and banking system.
            if (!Query("PRAGMA table_info(banks)", r => r.GetString(1)).Contains("date_format"))
                Exec("ALTER TABLE banks ADD COLUMN date_format INTEGER;");
            foreach (var b in Query("SELECT id, country, system FROM banks", r => (Id: r.GetInt64(0), Country: r.GetString(1), System: (BankingSystem)r.GetInt32(2))))
                Exec("UPDATE banks SET date_format = $f WHERE id = $id", ("$f", (int)Parsing.BankDates.Default(b.Country, b.System)), ("$id", b.Id));
            Exec("PRAGMA user_version = 19;");
        }
    }

    // ---------------------------------------------------------------- helpers

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            var cmd = Command(sql, args);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A single write to the settings or the exchange rates only.</summary>
    private static bool IsBookkeeping(string sql)
    {
        var s = sql.AsSpan().Trim();
        if (s.IndexOf(';') is var end and >= 0 && end < s.Length - 1) return false;
        foreach (var prefix in (ReadOnlySpan<string>)["INSERT INTO settings", "UPDATE settings", "DELETE FROM settings", "INSERT INTO rate_sets", "UPDATE rate_sets",
            "DELETE FROM rate_sets", "INSERT INTO rate_sources", "UPDATE rate_sources", "DELETE FROM rate_sources",
            "INSERT INTO holiday_sets", "UPDATE holiday_sets", "DELETE FROM holiday_sets", "INSERT INTO holiday_calendars",
            "UPDATE holiday_calendars", "DELETE FROM holiday_calendars"])
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            var cmd = Command(sql, args);
            return cmd.ExecuteScalar();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        // A write that can reach operations (directly, or by deleting what they belong to) drops the ones kept in
        // memory: they are read again on next use. Settings, rules, categories, banks and templates leave them alone.
        // Settings and exchange rates are bookkeeping: writing them (the window's place, a row order, a download of
        // rates) changes no operation, so what is kept in memory about the operations stays valid.
        var write = !sql.AsSpan().TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
        if (write && IsBookkeeping(sql)) BookkeepingVersion++;
        else if (write)
        {
            Version++;
            if (!_patching && (sql.Contains("transactions", StringComparison.OrdinalIgnoreCase) || sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("PRAGMA", StringComparison.OrdinalIgnoreCase) || sql.Contains("ALTER", StringComparison.OrdinalIgnoreCase)))
            { _all = null; _index = null; }
        }
        // One command per text, kept: SQLite compiles its statements once instead of at every operation written or
        // read (an import writes thousands with the same text). Only used under the lock, one at a time.
        if (!_commands.TryGetValue(sql, out var cmd))
        {
            if (_commands.Count >= MaxCommands)
            {
                foreach (var old in _commands.Values) old.Dispose();
                _commands.Clear();
            }
            cmd = _cn.CreateCommand();
            cmd.CommandText = sql;
            _commands[sql] = cmd;
        }
        cmd.Transaction = _tx;
        cmd.Parameters.Clear();
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);
    private const int MaxCommands = 256;

    private SqliteTransaction? _tx;

    /// <summary>Every operation, by date then id, kept between writes (most screens read them all).</summary>
    private List<Transaction>? _all;
    private Dictionary<long, Transaction>? _index;

    /// <summary>True while a write also updates the operations kept in memory, which then stay valid.</summary>
    private bool _patching;

    /// <summary>Runs a write on operations and applies the same change to the ones kept in memory.</summary>
    private void Patch(Action write, Action<Dictionary<long, Transaction>> apply)
    {
        lock (_gate)
        {
            var (all, index) = (_all, _index);
            _patching = true;
            try { write(); }
            finally { _patching = false; }
            if (all is null || index is null) return;
            apply(index);
            (_all, _index) = (all, index);
        }
    }

    /// <summary>Goes up with every write: lets the services keep results that only depend on the stored data.</summary>
    public long Version { get; private set; }

    /// <summary>Goes up with every write of settings, exchange rates or holidays (see <see cref="IsBookkeeping"/>).</summary>
    public long BookkeepingVersion { get; private set; }

    /// <summary>The same texts (currencies, categories, brands, wordings) repeat across operations: kept once in memory.</summary>
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    private string Shared(string s)
    {
        if (_strings.TryGetValue(s, out var known)) return known;
        if (_strings.Count > 200_000) _strings.Clear();
        _strings[s] = s;
        return s;
    }

    /// <summary>Runs several writes as one all-or-nothing unit.</summary>
    public void InTransaction(Action action)
    {
        lock (_gate)
        {
            if (_tx is not null) { action(); return; }
            _tx = _cn.BeginTransaction();
            try
            {
                action();
                _tx.Commit();
            }
            finally
            {
                _tx.Dispose();
                _tx = null;
            }
        }
    }

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? M(decimal? v) => v is null ? null : AmountParser.ToStorage(v.Value);
    private static decimal? M(object v) => v is DBNull or null ? null : AmountParser.FromStorage((string)v);
    private static string T(DateTime t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime T(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ---------------------------------------------------------------- settings

    /// <summary>Every setting, read once and kept in memory (they are read for every operation converted).</summary>
    private Dictionary<string, string>? _settings;

    private Dictionary<string, string> SettingsCache()
    {
        lock (_gate)
            return _settings ??= Query("SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
    }

    public string? GetSetting(string key)
    {
        var all = SettingsCache();
        lock (_gate) return all.GetValueOrDefault(key);
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            Exec("INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
            if (_settings is not null) _settings[key] = value;
        }
    }

    public List<RateSource> RateSources() =>
        Query("SELECT id, name, url, base, format FROM rate_sources ORDER BY position, rowid",
            // A line kept from before the sources were central banks has no currency: its bank's is the one.
            r => new RateSource(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? CentralBank.Find(r.GetString(4))?.Currency ?? throw new InvalidDataException($"Unknown central bank '{r.GetString(4)}'.") : r.GetString(3),
                r.GetString(4)));

    /// <summary>Adds the source (at the end) or saves its name, address and currency.</summary>
    public void SaveRateSource(RateSource s) => Exec("""
        INSERT INTO rate_sources(id, name, url, base, format, position) VALUES($i, $n, $u, $b, $f, (SELECT COALESCE(MAX(position), -1) + 1 FROM rate_sources))
        ON CONFLICT(id) DO UPDATE SET name = excluded.name, url = excluded.url, base = excluded.base, format = excluded.format
        """, ("$i", s.Id), ("$n", s.Name), ("$u", s.Url), ("$b", s.Base), ("$f", s.Format));

    /// <summary>Forgets the day and the error of a source's last download (its files are the download store's).</summary>
    public void ClearRates(string source) => InTransaction(() =>
    {
        Exec("DELETE FROM settings WHERE key IN ($u, $e)", ("$u", $"rates.{source}.updatedAt"), ("$e", $"rates.{source}.error"));
        _settings = null;
    });

    public void RemoveRateSource(string source) => InTransaction(() =>
    {
        ClearRates(source);
        Exec("DELETE FROM rate_sources WHERE id = $s", ("$s", source));
    });

    // ---------------------------------------------------------------- holidays

    public List<HolidayCalendar> HolidayCalendars() =>
        Query("SELECT id, location, url, active, parent FROM holiday_calendars ORDER BY position, rowid",
            r => new HolidayCalendar(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.IsDBNull(4) ? null : r.GetString(4)));

    /// <summary>Adds the calendar (at the end) or saves its place, address, whether it is checked and the country it is under.</summary>
    public void SaveHolidayCalendar(HolidayCalendar c) => Exec("""
        INSERT INTO holiday_calendars(id, location, url, active, parent, position) VALUES($i, $l, $u, $a, $p, (SELECT COALESCE(MAX(position), -1) + 1 FROM holiday_calendars))
        ON CONFLICT(id) DO UPDATE SET location = excluded.location, url = excluded.url, active = excluded.active, parent = excluded.parent
        """, ("$i", c.Id), ("$l", c.Location), ("$u", c.Url), ("$a", c.Active ? 1 : 0), ("$p", c.Parent));

    /// <summary>Forgets the day and the error of a calendar's last download (its files are the download store's).</summary>
    public void ClearHolidays(string calendar) => InTransaction(() =>
    {
        Exec("DELETE FROM settings WHERE key IN ($u, $e)", ("$u", $"holidays.{calendar}.updatedAt"), ("$e", $"holidays.{calendar}.error"));
        _settings = null;
    });

    public void RemoveHolidayCalendar(string calendar) => InTransaction(() =>
    {
        ClearHolidays(calendar);
        Exec("DELETE FROM holiday_calendars WHERE id = $c", ("$c", calendar));
    });

    public Dictionary<string, string> AllSettings()
    {
        var all = SettingsCache();
        lock (_gate) return new Dictionary<string, string>(all);
    }

    // ---------------------------------------------------------------- accounts

    public List<Account> Accounts() => Query("""
        SELECT a.id, a.name, a.currency, a.kind, a.bank, a.last4, a.bank_id, COALESCE(b.system, 0)
        FROM accounts a LEFT JOIN banks b ON b.id = a.bank_id ORDER BY a.name
        """, r => new Account
    {
        Id = r.GetInt64(0), Name = r.GetString(1), Currency = r.GetString(2), Kind = (AccountKind)r.GetInt32(3),
        Bank = r.IsDBNull(4) ? null : r.GetString(4), Last4 = r.IsDBNull(5) ? null : r.GetString(5),
        BankId = r.IsDBNull(6) ? null : r.GetInt64(6), System = (BankingSystem)r.GetInt32(7),
    });

    public long AddAccount(Account a) => (long)Scalar(
        "INSERT INTO accounts(name, currency, kind, bank, last4, bank_id) VALUES($n, $c, $k, $b, $l, $bi) RETURNING id",
        ("$n", a.Name), ("$c", a.Currency), ("$k", (int)a.Kind), ("$b", a.Bank), ("$l", a.Last4), ("$bi", a.BankId))!;

    /// <summary>Saves the account; its operations take its currency.</summary>
    public void UpdateAccount(Account a) => Exec(
        "UPDATE accounts SET name = $n, currency = $c, kind = $k, bank_id = $bi WHERE id = $id; UPDATE transactions SET currency = $c WHERE account_id = $id",
        ("$n", a.Name), ("$c", a.Currency), ("$k", (int)a.Kind), ("$bi", a.BankId), ("$id", a.Id));

    // ---------------------------------------------------------------- banks

    public List<Bank> Banks() => Query("SELECT id, name, country, system, currency, date_format FROM banks ORDER BY id", r => new Bank
    {
        Id = r.GetInt64(0), Name = r.GetString(1), Country = r.GetString(2), System = (BankingSystem)r.GetInt32(3), Currency = r.GetString(4),
        DateFormat = r.IsDBNull(5) ? Parsing.BankDates.Default(r.GetString(2), (BankingSystem)r.GetInt32(3)) : (DateFormat)r.GetInt32(5),
    });

    public long AddBank(Bank b) => (long)Scalar("INSERT INTO banks(name, country, system, currency, date_format) VALUES($n, $c, $s, $cu, $f) RETURNING id",
        ("$n", b.Name), ("$c", b.Country), ("$s", (int)b.System), ("$cu", b.Currency), ("$f", (int)(b.DateFormat ?? Parsing.BankDates.Default(b.Country, b.System))))!;

    public void UpdateBank(Bank b) => Exec("UPDATE banks SET name = $n, country = $c, system = $s, currency = $cu, date_format = $f WHERE id = $id",
        ("$n", b.Name), ("$c", b.Country), ("$s", (int)b.System), ("$cu", b.Currency), ("$f", (int)(b.DateFormat ?? Parsing.BankDates.Default(b.Country, b.System))), ("$id", b.Id));

    public void DeleteBank(long id) => Exec("DELETE FROM banks WHERE id = $id", ("$id", id));

    // ---------------------------------------------------------------- categories

    /// <summary>Built-in categories missing from the table (a new database, or ones added by a later version) are added.</summary>
    private void SeedCategories()
    {
        var seq = 0;
        foreach (var c in Model.Categories.BuiltIn)
            Exec("INSERT OR IGNORE INTO categories(id, name, kind, parent_id, color, seq) VALUES($i, $n, $k, NULL, NULL, $s)",
                ("$i", c.Id), ("$n", c.Name), ("$k", (int)c.Kind), ("$s", seq++));
        Model.Categories.Use(LoadCategories());
    }

    /// <summary>
    /// Every category, built-in ones first. A category the person made takes its icon from the nearest built-in
    /// category it is nested in.
    /// </summary>
    public List<Category> LoadCategories()
    {
        var rows = Query("SELECT id, name, kind, parent_id, color FROM categories ORDER BY seq, rowid",
            r => (Id: r.GetString(0), Name: r.GetString(1), Kind: (CategoryKind)r.GetInt32(2), Parent: r.IsDBNull(3) ? null : r.GetString(3), Color: r.IsDBNull(4) ? null : r.GetString(4)));
        var byId = rows.ToDictionary(r => r.Id);
        var builtIn = Model.Categories.BuiltIn.ToDictionary(c => c.Id);
        Category? Origin(string id)
        {
            // Bounded walk up the chain: a broken chain cannot loop.
            string? cur = id;
            for (var n = 0; cur is not null && n < 1000; n++)
            {
                if (builtIn.TryGetValue(cur, out var b)) return b;
                cur = byId.TryGetValue(cur, out var row) ? row.Parent : null;
            }
            return null;
        }
        return rows.Select(r =>
        {
            var origin = Origin(r.Id);
            var parent = r.Parent is not null && byId.ContainsKey(r.Parent) ? r.Parent : null;
            return new Category(r.Id, r.Name, r.Kind, origin?.Icon ?? "Category", parent, r.Color, builtIn.ContainsKey(r.Id));
        }).ToList();
    }

    /// <summary>Adds a category the person made; returns its id ("u1", "u2"...).</summary>
    public string AddCategory(string name, CategoryKind kind, string? parentId, string? color)
    {
        var n = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(CAST(substr(id, 2) AS INTEGER)), 0) + 1 FROM categories WHERE id GLOB 'u[0-9]*'"), CultureInfo.InvariantCulture);
        var id = "u" + n.ToString(CultureInfo.InvariantCulture);
        Exec("INSERT INTO categories(id, name, kind, parent_id, color, seq) VALUES($i, $n, $k, $p, $c, (SELECT COALESCE(MAX(seq), 0) + 1 FROM categories))",
            ("$i", id), ("$n", name), ("$k", (int)kind), ("$p", parentId), ("$c", color));
        Model.Categories.Use(LoadCategories());
        return id;
    }

    public void UpdateCategory(string id, string name, string? parentId, string? color)
    {
        Exec("UPDATE categories SET name = $n, parent_id = $p, color = $c WHERE id = $i", ("$i", id), ("$n", name), ("$p", parentId), ("$c", color));
        Model.Categories.Use(LoadCategories());
    }

    /// <summary>Deletes a category: its operations and learned rules take <paramref name="replacement"/>, its nested categories move up to <paramref name="parentId"/>.</summary>
    public void DeleteCategory(string id, string replacement, string? parentId)
    {
        InTransaction(() => Exec("""
            UPDATE transactions SET category_id = $r WHERE category_id = $i;
            UPDATE brand_rules SET category_id = $r WHERE category_id = $i;
            UPDATE categories SET parent_id = $p WHERE parent_id = $i;
            DELETE FROM categories WHERE id = $i;
            """, ("$i", id), ("$r", replacement), ("$p", parentId)));
        Model.Categories.Use(LoadCategories());
    }

    public void DeleteAccount(long id) => Exec("DELETE FROM accounts WHERE id = $id", ("$id", id));

    // ---------------------------------------------------------------- tags

    private (long Version, TagIndex Index)? _tags;

    /// <summary>Every tag and what carries it, read again only after a write.</summary>
    public TagIndex TagIndex()
    {
        lock (_gate)
        {
            if (_tags is { } t && t.Version == Version) return t.Index;
            var tags = Query("SELECT id, name, color FROM tags ORDER BY seq, id", r => new Tag(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            var ops = Query("SELECT operation_id, tag_id FROM operation_tags ORDER BY operation_id, tag_id", r => (Op: r.GetInt64(0), Tag: r.GetInt64(1)))
                .GroupBy(x => x.Op).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToArray());
            var cats = Query("SELECT category_id, tag_id FROM category_tags ORDER BY category_id, tag_id", r => (Cat: r.GetString(0), Tag: r.GetInt64(1)))
                .GroupBy(x => x.Cat).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToArray());
            var index = new TagIndex(tags, ops, cats);
            _tags = (Version, index);
            return index;
        }
    }

    public long AddTag(string name, string? color) => (long)Scalar(
        "INSERT INTO tags(name, color, seq) VALUES($n, $c, (SELECT COALESCE(MAX(seq), 0) + 1 FROM tags)) RETURNING id", ("$n", name), ("$c", color))!;

    public void UpdateTag(long id, string name, string? color) =>
        Exec("UPDATE tags SET name = $n, color = $c WHERE id = $i", ("$i", id), ("$n", name), ("$c", color));

    /// <summary>Deletes a tag: it leaves every operation and category that carried it.</summary>
    public void DeleteTag(long id) => Patch(() => Exec("DELETE FROM tags WHERE id = $i", ("$i", id)), _ => { });

    public bool TagExists(long id) => Scalar("SELECT 1 FROM tags WHERE id = $i", ("$i", id)) is not null;

    public bool TagNameTaken(string name, long exceptId) => Scalar("SELECT 1 FROM tags WHERE name = $n AND id <> $i", ("$n", name), ("$i", exceptId)) is not null;

    /// <summary>The tags put on one operation itself (its category's tags are not stored on it).</summary>
    public void SetOperationTags(long operationId, IReadOnlyCollection<long> tagIds) => Patch(() => InTransaction(() =>
    {
        Exec("DELETE FROM operation_tags WHERE operation_id = $o", ("$o", operationId));
        foreach (var t in tagIds.Distinct()) Exec("INSERT INTO operation_tags(operation_id, tag_id) VALUES($o, $t)", ("$o", operationId), ("$t", t));
    }), _ => { });

    /// <summary>The tags a category gives to every one of its operations.</summary>
    public void SetCategoryTags(string categoryId, IReadOnlyCollection<long> tagIds) => Patch(() => InTransaction(() =>
    {
        Exec("DELETE FROM category_tags WHERE category_id = $c", ("$c", categoryId));
        foreach (var t in tagIds.Distinct()) Exec("INSERT INTO category_tags(category_id, tag_id) VALUES($c, $t)", ("$c", categoryId), ("$t", t));
    }), _ => { });

    // ---------------------------------------------------------------- statements

    public bool HasStatementHash(string hash) => Scalar("SELECT 1 FROM statements WHERE file_hash = $h", ("$h", hash)) is not null;

    public long AddStatement(StatementRecord s) => (long)Scalar("""
        INSERT INTO statements(account_id, file_name, file_hash, period_start, period_end, opening, closing, verified, method, imported_at)
        VALUES($a, $f, $h, $ps, $pe, $o, $c, $v, $m, $t) RETURNING id
        """,
        ("$a", s.AccountId), ("$f", s.FileName), ("$h", s.FileHash), ("$ps", s.PeriodStart is { } ps ? D(ps) : null),
        ("$pe", s.PeriodEnd is { } pe ? D(pe) : null), ("$o", M(s.OpeningBalance)), ("$c", M(s.ClosingBalance)),
        ("$v", s.Verified ? 1 : 0), ("$m", s.Method), ("$t", T(s.ImportedAt)))!;

    public List<StatementRecord> Statements() => Query("""
        SELECT s.id, s.account_id, s.file_name, s.file_hash, s.period_start, s.period_end, s.opening, s.closing, s.verified, s.method, s.imported_at,
               (SELECT COUNT(*) FROM transactions t WHERE t.statement_id = s.id)
        FROM statements s ORDER BY s.period_end DESC, s.id DESC
        """, r => new StatementRecord
    {
        Id = r.GetInt64(0), AccountId = r.GetInt64(1), FileName = r.GetString(2), FileHash = r.GetString(3),
        PeriodStart = r.IsDBNull(4) ? null : D(r.GetString(4)), PeriodEnd = r.IsDBNull(5) ? null : D(r.GetString(5)),
        OpeningBalance = M(r.GetValue(6)), ClosingBalance = M(r.GetValue(7)), Verified = r.GetInt32(8) == 1,
        Method = r.GetString(9), ImportedAt = T(r.GetString(10)), TransactionCount = r.GetInt32(11),
    });

    public void DeleteStatement(long id) => Exec("DELETE FROM statements WHERE id = $id", ("$id", id));

    // ---------------------------------------------------------------- transactions

    private const string TxColumns = "id, account_id, statement_id, date, amount, currency, raw_label, merchant_key, brand, brand_source, category_id, category_source, note, bank_category, linked_id, earned";

    private Transaction ReadTx(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), AccountId = r.GetInt64(1), StatementId = r.IsDBNull(2) ? null : r.GetInt64(2), Date = D(r.GetString(3)),
        Amount = AmountParser.FromStorage(r.GetString(4)), Currency = Shared(r.GetString(5)), RawLabel = Shared(r.GetString(6)), MerchantKey = Shared(r.GetString(7)),
        Brand = Shared(r.GetString(8)), BrandSource = (ValueSource)r.GetInt32(9), CategoryId = Shared(r.GetString(10)), CategorySource = (ValueSource)r.GetInt32(11),
        Note = Shared(r.GetString(12)), BankCategory = Shared(r.GetString(13)),
        LinkedId = r.IsDBNull(14) ? null : r.GetInt64(14), Earned = M(r.GetValue(15)),
    };

    public long AddTransaction(Transaction t) => (long)Scalar($"""
        INSERT INTO transactions(account_id, statement_id, date, amount, currency, raw_label, merchant_key, brand, brand_source, category_id, category_source, note, bank_category, earned)
        VALUES($a, $s, $d, $m, $c, $r, $k, $b, $bs, $cat, $cs, $n, $bc, $e) RETURNING id
        """, ("$bc", t.BankCategory), ("$e", M(t.Earned)),
        ("$a", t.AccountId), ("$s", t.StatementId), ("$d", D(t.Date)), ("$m", AmountParser.ToStorage(t.Amount)), ("$c", t.Currency),
        ("$r", t.RawLabel), ("$k", t.MerchantKey), ("$b", t.Brand), ("$bs", (int)t.BrandSource), ("$cat", t.CategoryId),
        ("$cs", (int)t.CategorySource), ("$n", t.Note))!;

    public void UpdateTransaction(Transaction t)
    {
        var moved = false;
        Patch(() => Exec("""
            UPDATE transactions SET account_id = $a, date = $d, amount = $m, currency = $c, raw_label = $r, merchant_key = $k, brand = $b, brand_source = $bs,
                category_id = $cat, category_source = $cs, note = $n, earned = $e WHERE id = $id
            """,
            ("$a", t.AccountId), ("$d", D(t.Date)), ("$m", AmountParser.ToStorage(t.Amount)), ("$c", t.Currency), ("$r", t.RawLabel),
            ("$k", t.MerchantKey), ("$b", t.Brand), ("$bs", (int)t.BrandSource), ("$cat", t.CategoryId), ("$cs", (int)t.CategorySource),
            ("$n", t.Note), ("$e", M(t.Earned)), ("$id", t.Id)),
            index =>
            {
                if (!index.TryGetValue(t.Id, out var kept) || ReferenceEquals(kept, t)) return;
                moved = kept.Date != t.Date;
                (kept.AccountId, kept.Date, kept.Amount, kept.Currency, kept.RawLabel, kept.MerchantKey) = (t.AccountId, t.Date, t.Amount, t.Currency, t.RawLabel, t.MerchantKey);
                (kept.Brand, kept.BrandSource, kept.CategoryId, kept.CategorySource, kept.Note, kept.Earned) = (t.Brand, t.BrandSource, t.CategoryId, t.CategorySource, t.Note, t.Earned);
            });
        // A new date changes the order: read again. (Operations kept in memory never get a new date in place:
        // a date is only edited on a copy read with GetTransaction.)
        if (moved) lock (_gate) { _all = null; _index = null; }
    }

    /// <summary>Gives many operations one category in a single write.</summary>
    public void SetCategoryOf(IReadOnlyCollection<long> ids, string categoryId, ValueSource source)
    {
        if (ids.Count == 0) return;
        Patch(() => Exec("UPDATE transactions SET category_id = $c, category_source = $s WHERE id IN (SELECT value FROM json_each($ids))",
                ("$c", categoryId), ("$s", (int)source), ("$ids", JsonSerializer.Serialize(ids))),
            index =>
            {
                foreach (var id in ids)
                    if (index.TryGetValue(id, out var t)) (t.CategoryId, t.CategorySource) = (categoryId, source);
            });
    }

    public void DeleteTransaction(long id) => Exec("UPDATE transactions SET linked_id = NULL WHERE linked_id = $id; DELETE FROM transactions WHERE id = $id", ("$id", id));

    /// <summary>Records the other half of a movement between own accounts of an operation (null: none).</summary>
    public void SetLinkOf(long id, long? other) => Patch(
        () => Exec("UPDATE transactions SET linked_id = $o WHERE id = $id", ("$id", id), ("$o", other)),
        index => { if (index.TryGetValue(id, out var t)) t.LinkedId = other; });

    public Transaction? GetTransaction(long id) => Query($"SELECT {TxColumns} FROM transactions WHERE id = $id", ReadTx, ("$id", id)).FirstOrDefault();

    /// <summary>The operations from one day to another (both included), by date then id.</summary>
    public List<Transaction> Transactions(DateOnly from, DateOnly to)
    {
        var all = Cached();
        // First operation on or after "from": the list is in date order.
        int lo = 0, hi = all.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (all[mid].Date < from) lo = mid + 1; else hi = mid; }
        var list = new List<Transaction>();
        for (var i = lo; i < all.Count && all[i].Date <= to; i++) list.Add(all[i]);
        return list;
    }

    /// <summary>Every operation, by date then id. The list is the caller's; the operations are shared until the next write.</summary>
    public List<Transaction> AllTransactions() => [.. Cached()];

    private List<Transaction> Cached()
    {
        lock (_gate)
        {
            if (_all is null)
            {
                _all = Query($"SELECT {TxColumns} FROM transactions ORDER BY date, id", ReadTx);
                _index = _all.ToDictionary(t => t.Id);
            }
            return _all;
        }
    }

    public List<Transaction> TransactionsForAccount(long accountId, DateOnly from, DateOnly to) =>
        Transactions(from, to).Where(t => t.AccountId == accountId).ToList();

    /// <summary>Months (first day) that have at least one operation, with the currencies used.</summary>
    public List<(int Year, int Month, string Currency, int Count)> MonthsWithData() => Cached()
        .GroupBy(t => (t.Date.Year, t.Date.Month, t.Currency))
        .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month).ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
        .Select(g => (g.Key.Year, g.Key.Month, g.Key.Currency, g.Count()))
        .ToList();

    // ---------------------------------------------------------------- brand rules

    public List<BrandRule> BrandRules() => Query("SELECT id, merchant_key, brand, category_id, updated_at FROM brand_rules", r => new BrandRule
    {
        Id = r.GetInt64(0), MerchantKey = r.GetString(1), Brand = r.GetString(2), CategoryId = r.IsDBNull(3) ? null : r.GetString(3), UpdatedAt = T(r.GetString(4)),
    });

    public void UpsertKeyRule(string merchantKey, string brand, string? categoryId) => Exec("""
        INSERT INTO brand_rules(merchant_key, brand, category_id, updated_at) VALUES($k, $b, $c, $t)
        ON CONFLICT(merchant_key) WHERE merchant_key <> '' DO UPDATE SET brand = excluded.brand, category_id = COALESCE(excluded.category_id, brand_rules.category_id), updated_at = excluded.updated_at
        """, ("$k", merchantKey), ("$b", brand), ("$c", categoryId), ("$t", T(DateTime.UtcNow)));

    public void UpsertBrandCategory(string brand, string categoryId) => Exec("""
        INSERT INTO brand_rules(merchant_key, brand, category_id, updated_at) VALUES('', $b, $c, $t)
        ON CONFLICT(merchant_key, brand) WHERE merchant_key = '' DO UPDATE SET category_id = excluded.category_id, updated_at = excluded.updated_at
        """, ("$b", brand), ("$c", categoryId), ("$t", T(DateTime.UtcNow)));

    /// <summary>Forgets everything learned for a brand: its wordings and its category.</summary>
    public void DeleteBrandRules(string brand) => Exec("DELETE FROM brand_rules WHERE brand = $b COLLATE NOCASE", ("$b", brand));

    /// <summary>
    /// The wordings and the category learned for a brand go to another name; when that name has a category of its own
    /// already, it keeps it.
    /// </summary>
    public void RenameBrandRules(string from, string to) => InTransaction(() =>
    {
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            Exec("""
                DELETE FROM brand_rules WHERE merchant_key = '' AND brand = $f COLLATE NOCASE
                    AND EXISTS (SELECT 1 FROM brand_rules WHERE merchant_key = '' AND brand = $t COLLATE NOCASE)
                """, ("$f", from), ("$t", to));
        Exec("UPDATE brand_rules SET brand = $t WHERE brand = $f COLLATE NOCASE", ("$f", from), ("$t", to));
    });

    /// <summary>Every wording learned for the brand takes the category too.</summary>
    public void SetBrandRulesCategory(string brand, string categoryId) =>
        Exec("UPDATE brand_rules SET category_id = $c WHERE brand = $b COLLATE NOCASE AND merchant_key <> ''", ("$b", brand), ("$c", categoryId));

    // ---------------------------------------------------------------- templates

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public List<BankTemplate> Templates() => Query("SELECT id, name, match_text, columns, date_order, kind, updated_at FROM templates ORDER BY name", r => new BankTemplate
    {
        Id = r.GetInt64(0), Name = r.GetString(1), MatchText = r.GetString(2),
        Columns = JsonSerializer.Deserialize<List<TemplateColumn>>(r.GetString(3), Json) ?? [],
        DateOrder = (DateOrder)r.GetInt32(4), Kind = (StatementKindHint)r.GetInt32(5), UpdatedAt = T(r.GetString(6)),
    });

    public long SaveTemplate(BankTemplate t)
    {
        var cols = JsonSerializer.Serialize(t.Columns, Json);
        if (t.Id == 0)
        {
            return (long)Scalar("INSERT INTO templates(name, match_text, columns, date_order, kind, updated_at) VALUES($n, $m, $c, $d, $k, $t) RETURNING id",
                ("$n", t.Name), ("$m", t.MatchText), ("$c", cols), ("$d", (int)t.DateOrder), ("$k", (int)t.Kind), ("$t", T(DateTime.UtcNow)))!;
        }
        Exec("UPDATE templates SET name = $n, match_text = $m, columns = $c, date_order = $d, kind = $k, updated_at = $t WHERE id = $id",
            ("$n", t.Name), ("$m", t.MatchText), ("$c", cols), ("$d", (int)t.DateOrder), ("$k", (int)t.Kind), ("$t", T(DateTime.UtcNow)), ("$id", t.Id));
        return t.Id;
    }

    public void DeleteTemplate(long id) => Exec("DELETE FROM templates WHERE id = $id", ("$id", id));

    /// <summary>Erases every account, statement, operation, learned rule and template. Settings stay.</summary>
    public void EraseAll() => Exec("DELETE FROM transactions; DELETE FROM statements; DELETE FROM accounts; DELETE FROM brand_rules; DELETE FROM templates;");
}
