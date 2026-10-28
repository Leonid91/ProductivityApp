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
    private const int SchemaVersion = 4;
    private readonly SqliteConnection _cn;
    private readonly Lock _gate = new();

    public string Path { get; }

    public Database(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _cn.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;");
        Migrate();
    }

    public void Dispose() => _cn.Dispose();

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
    }

    // ---------------------------------------------------------------- helpers

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteScalar();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        var cmd = _cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private SqliteTransaction? _tx;

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

    public string? GetSetting(string key) => Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) as string;

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    public Dictionary<string, string> AllSettings() =>
        Query("SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);

    // ---------------------------------------------------------------- accounts

    public List<Account> Accounts() => Query("SELECT id, name, currency, kind, bank, last4 FROM accounts ORDER BY name", r => new Account
    {
        Id = r.GetInt64(0), Name = r.GetString(1), Currency = r.GetString(2), Kind = (AccountKind)r.GetInt32(3),
        Bank = r.IsDBNull(4) ? null : r.GetString(4), Last4 = r.IsDBNull(5) ? null : r.GetString(5),
    });

    public long AddAccount(Account a) => (long)Scalar(
        "INSERT INTO accounts(name, currency, kind, bank, last4) VALUES($n, $c, $k, $b, $l) RETURNING id",
        ("$n", a.Name), ("$c", a.Currency), ("$k", (int)a.Kind), ("$b", a.Bank), ("$l", a.Last4))!;

    public void UpdateAccount(Account a) => Exec(
        "UPDATE accounts SET name = $n, currency = $c, kind = $k WHERE id = $id",
        ("$n", a.Name), ("$c", a.Currency), ("$k", (int)a.Kind), ("$id", a.Id));

    public void DeleteAccount(long id) => Exec("DELETE FROM accounts WHERE id = $id", ("$id", id));

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

    private const string TxColumns = "id, account_id, statement_id, date, amount, currency, raw_label, merchant_key, brand, brand_source, category_id, category_source, note, bank_category, linked_id";

    private static Transaction ReadTx(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), AccountId = r.GetInt64(1), StatementId = r.IsDBNull(2) ? null : r.GetInt64(2), Date = D(r.GetString(3)),
        Amount = AmountParser.FromStorage(r.GetString(4)), Currency = r.GetString(5), RawLabel = r.GetString(6), MerchantKey = r.GetString(7),
        Brand = r.GetString(8), BrandSource = (ValueSource)r.GetInt32(9), CategoryId = r.GetString(10), CategorySource = (ValueSource)r.GetInt32(11),
        Note = r.GetString(12), BankCategory = r.GetString(13),
        LinkedId = r.IsDBNull(14) ? null : r.GetInt64(14),
    };

    public long AddTransaction(Transaction t) => (long)Scalar($"""
        INSERT INTO transactions(account_id, statement_id, date, amount, currency, raw_label, merchant_key, brand, brand_source, category_id, category_source, note, bank_category)
        VALUES($a, $s, $d, $m, $c, $r, $k, $b, $bs, $cat, $cs, $n, $bc) RETURNING id
        """, ("$bc", t.BankCategory),
        ("$a", t.AccountId), ("$s", t.StatementId), ("$d", D(t.Date)), ("$m", AmountParser.ToStorage(t.Amount)), ("$c", t.Currency),
        ("$r", t.RawLabel), ("$k", t.MerchantKey), ("$b", t.Brand), ("$bs", (int)t.BrandSource), ("$cat", t.CategoryId),
        ("$cs", (int)t.CategorySource), ("$n", t.Note))!;

    public void UpdateTransaction(Transaction t) => Exec("""
        UPDATE transactions SET account_id = $a, date = $d, amount = $m, currency = $c, raw_label = $r, merchant_key = $k, brand = $b, brand_source = $bs,
            category_id = $cat, category_source = $cs, note = $n WHERE id = $id
        """,
        ("$a", t.AccountId), ("$d", D(t.Date)), ("$m", AmountParser.ToStorage(t.Amount)), ("$c", t.Currency), ("$r", t.RawLabel),
        ("$k", t.MerchantKey), ("$b", t.Brand), ("$bs", (int)t.BrandSource), ("$cat", t.CategoryId), ("$cs", (int)t.CategorySource),
        ("$n", t.Note), ("$id", t.Id));

    public void DeleteTransaction(long id) => Exec("UPDATE transactions SET linked_id = NULL WHERE linked_id = $id; DELETE FROM transactions WHERE id = $id", ("$id", id));

    /// <summary>Forgets every pairing of movements between own accounts (they are found again right after).</summary>
    public void ClearLinks() => Exec("UPDATE transactions SET linked_id = NULL WHERE linked_id IS NOT NULL");

    /// <summary>Records that two operations are the two halves of one movement between own accounts.</summary>
    public void SetLink(long a, long b) => Exec("UPDATE transactions SET linked_id = $b WHERE id = $a; UPDATE transactions SET linked_id = $a WHERE id = $b", ("$a", a), ("$b", b));

    public Transaction? GetTransaction(long id) => Query($"SELECT {TxColumns} FROM transactions WHERE id = $id", ReadTx, ("$id", id)).FirstOrDefault();

    public List<Transaction> Transactions(DateOnly from, DateOnly to) =>
        Query($"SELECT {TxColumns} FROM transactions WHERE date >= $f AND date <= $t ORDER BY date, id", ReadTx, ("$f", D(from)), ("$t", D(to)));

    public List<Transaction> AllTransactions() => Query($"SELECT {TxColumns} FROM transactions ORDER BY date, id", ReadTx);

    public List<Transaction> TransactionsForAccount(long accountId, DateOnly from, DateOnly to) =>
        Query($"SELECT {TxColumns} FROM transactions WHERE account_id = $a AND date >= $f AND date <= $t", ReadTx, ("$a", accountId), ("$f", D(from)), ("$t", D(to)));

    /// <summary>Months (first day) that have at least one operation, with the currencies used.</summary>
    public List<(int Year, int Month, string Currency, int Count)> MonthsWithData() => Query(
        "SELECT CAST(substr(date, 1, 4) AS INTEGER), CAST(substr(date, 6, 2) AS INTEGER), currency, COUNT(*) FROM transactions GROUP BY 1, 2, 3 ORDER BY 1 DESC, 2 DESC",
        r => (r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3)));

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

    public void DeleteBrandRule(long id) => Exec("DELETE FROM brand_rules WHERE id = $id", ("$id", id));

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
