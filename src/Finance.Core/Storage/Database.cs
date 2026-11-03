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
    private const int SchemaVersion = 6;
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
        SeedCategories();
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
            AddBank(new Bank { Name = KnownBanks.National, Country = "Canada", System = BankingSystem.NorthAmerica, Currency = "CAD" });
            foreach (var (name, currency) in Query("SELECT bank, MIN(currency) FROM accounts WHERE bank IS NOT NULL AND bank <> $n GROUP BY bank ORDER BY MIN(id)",
                r => (r.GetString(0), r.GetString(1)), ("$n", KnownBanks.National)))
                AddBank(new Bank { Name = name, System = system, Currency = currency });
            Exec("UPDATE accounts SET bank_id = (SELECT b.id FROM banks b WHERE b.name = accounts.bank); PRAGMA user_version = 6;");
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

    public List<Bank> Banks() => Query("SELECT id, name, country, system, currency FROM banks ORDER BY id", r => new Bank
    {
        Id = r.GetInt64(0), Name = r.GetString(1), Country = r.GetString(2), System = (BankingSystem)r.GetInt32(3), Currency = r.GetString(4),
    });

    public long AddBank(Bank b) => (long)Scalar("INSERT INTO banks(name, country, system, currency) VALUES($n, $c, $s, $cu) RETURNING id",
        ("$n", b.Name), ("$c", b.Country), ("$s", (int)b.System), ("$cu", b.Currency))!;

    public void UpdateBank(Bank b) => Exec("UPDATE banks SET name = $n, country = $c, system = $s, currency = $cu WHERE id = $id",
        ("$n", b.Name), ("$c", b.Country), ("$s", (int)b.System), ("$cu", b.Currency), ("$id", b.Id));

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
    /// Every category, built-in ones first. A category the person made takes its icon and group (exceptional) from the
    /// nearest built-in category it is nested in.
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
            return new Category(r.Id, r.Name, r.Kind, origin?.Icon ?? "Category", origin?.Group, parent, r.Color, builtIn.ContainsKey(r.Id));
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

    private static Transaction ReadTx(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), AccountId = r.GetInt64(1), StatementId = r.IsDBNull(2) ? null : r.GetInt64(2), Date = D(r.GetString(3)),
        Amount = AmountParser.FromStorage(r.GetString(4)), Currency = r.GetString(5), RawLabel = r.GetString(6), MerchantKey = r.GetString(7),
        Brand = r.GetString(8), BrandSource = (ValueSource)r.GetInt32(9), CategoryId = r.GetString(10), CategorySource = (ValueSource)r.GetInt32(11),
        Note = r.GetString(12), BankCategory = r.GetString(13),
        LinkedId = r.IsDBNull(14) ? null : r.GetInt64(14), Earned = M(r.GetValue(15)),
    };

    public long AddTransaction(Transaction t) => (long)Scalar($"""
        INSERT INTO transactions(account_id, statement_id, date, amount, currency, raw_label, merchant_key, brand, brand_source, category_id, category_source, note, bank_category, earned)
        VALUES($a, $s, $d, $m, $c, $r, $k, $b, $bs, $cat, $cs, $n, $bc, $e) RETURNING id
        """, ("$bc", t.BankCategory), ("$e", M(t.Earned)),
        ("$a", t.AccountId), ("$s", t.StatementId), ("$d", D(t.Date)), ("$m", AmountParser.ToStorage(t.Amount)), ("$c", t.Currency),
        ("$r", t.RawLabel), ("$k", t.MerchantKey), ("$b", t.Brand), ("$bs", (int)t.BrandSource), ("$cat", t.CategoryId),
        ("$cs", (int)t.CategorySource), ("$n", t.Note))!;

    public void UpdateTransaction(Transaction t) => Exec("""
        UPDATE transactions SET account_id = $a, date = $d, amount = $m, currency = $c, raw_label = $r, merchant_key = $k, brand = $b, brand_source = $bs,
            category_id = $cat, category_source = $cs, note = $n, earned = $e WHERE id = $id
        """,
        ("$a", t.AccountId), ("$d", D(t.Date)), ("$m", AmountParser.ToStorage(t.Amount)), ("$c", t.Currency), ("$r", t.RawLabel),
        ("$k", t.MerchantKey), ("$b", t.Brand), ("$bs", (int)t.BrandSource), ("$cat", t.CategoryId), ("$cs", (int)t.CategorySource),
        ("$n", t.Note), ("$e", M(t.Earned)), ("$id", t.Id));

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

    /// <summary>Forgets everything learned for a brand: its wordings and its category.</summary>
    public void DeleteBrandRules(string brand) => Exec("DELETE FROM brand_rules WHERE brand = $b COLLATE NOCASE", ("$b", brand));

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
