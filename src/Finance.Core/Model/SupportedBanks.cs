using System.Text.Json;

namespace ProductivityApp.Finance.Model;

/// <summary>A bank whose statements are read by themselves.</summary>
/// <param name="Considered">The reader was made and tested for its statements and exports, in their layouts.</param>
/// <param name="Confirmed">The person's own statements of the bank were read.</param>
public sealed record SupportedBank(string Name, bool Considered, bool Confirmed);

/// <summary>The banks of a country (its ISO 3166 code: CA, FR, GB).</summary>
public sealed record SupportedCountry(string Code, string Name, IReadOnlyList<SupportedBank> Banks);

/// <summary>
/// The banks whose statements and exports are read by themselves, by country, as shown in Settings > Information. Any
/// other bank is read too (columns found from the statement itself, or set once by hand): these are the ones made and
/// tested for (Considered) and the ones read from the person's own statements (Confirmed).
/// The list is a JSON file the person can change (with the pencil of each bank, or by hand); the first time, it is the
/// list that comes with the app.
/// </summary>
public sealed class SupportedBanks(string file)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string File { get; } = file;

    /// <summary>The list that comes with the app.</summary>
    public static IReadOnlyList<SupportedCountry> Defaults()
    {
        using var stream = typeof(SupportedBanks).Assembly.GetManifestResourceStream("supported-banks.json")
            ?? throw new InvalidOperationException("supported-banks.json is missing from the app.");
        return JsonSerializer.Deserialize<List<SupportedCountry>>(stream, Json) ?? [];
    }

    /// <summary>Writes the list that comes with the app to the file when there is none (at start: there to be changed by hand).</summary>
    public void Create()
    {
        if (!System.IO.File.Exists(File)) Write(Defaults());
    }

    /// <summary>The list of the file; the first time, the list that comes with the app, written to the file.</summary>
    public IReadOnlyList<SupportedCountry> All()
    {
        if (!System.IO.File.Exists(File))
        {
            var defaults = Defaults();
            Write(defaults);
            return defaults;
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<SupportedCountry>>(System.IO.File.ReadAllText(File), Json)
                ?? throw new JsonException("The file holds no list.");
            Check(list);
            return list;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{File} could not be read: {e.Message}", e);
        }
    }

    /// <summary>Changes the bank of a country (found by its name) and writes the file. Gives the new list back.</summary>
    public IReadOnlyList<SupportedCountry> Update(string country, string name, SupportedBank bank)
    {
        bank = bank with { Name = bank.Name.Trim() };
        if (bank.Name.Length is 0 or > 80) throw new ArgumentException("The bank name must be 1 to 80 characters.");
        var list = All().ToList();
        var c = list.FindIndex(x => x.Code == country);
        if (c < 0) throw new ArgumentException($"No country {country} in the list.");
        var banks = list[c].Banks.ToList();
        var b = banks.FindIndex(x => x.Name == name);
        if (b < 0) throw new ArgumentException($"No bank {name} in {list[c].Name}.");
        if (banks.Where((x, i) => i != b).Any(x => string.Equals(x.Name, bank.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{bank.Name} is already in {list[c].Name}.");
        banks[b] = bank;
        list[c] = list[c] with { Banks = banks };
        Write(list);
        return list;
    }

    private static void Check(List<SupportedCountry> list)
    {
        foreach (var c in list)
        {
            if (string.IsNullOrWhiteSpace(c.Code) || string.IsNullOrWhiteSpace(c.Name) || c.Banks is null)
                throw new JsonException("Every country needs a code, a name and its banks.");
            if (c.Banks.Any(b => b is null || string.IsNullOrWhiteSpace(b.Name)))
                throw new JsonException($"Every bank of {c.Name} needs a name.");
        }
    }

    private void Write(IReadOnlyList<SupportedCountry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
        // Written whole beside it first: a file cut short by a crash would lose the list.
        var part = File + ".part";
        System.IO.File.WriteAllText(part, JsonSerializer.Serialize(list, Json));
        System.IO.File.Move(part, File, overwrite: true);
    }
}
