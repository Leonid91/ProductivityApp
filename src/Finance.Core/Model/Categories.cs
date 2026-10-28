namespace ProductivityApp.Finance.Model;

public enum CategoryKind { Expense, Income, Transfer }

/// <summary>A predefined category. Icon is a Material icon name used by the interface.</summary>
public sealed record Category(string Id, string Name, CategoryKind Kind, string Icon);

public static class Categories
{
    public const string OtherExpense = "other";
    public const string OtherIncome = "otherincome";
    public const string Transfers = "transfers";

    public static readonly IReadOnlyList<Category> All =
    [
        new("groceries", "Groceries", CategoryKind.Expense, "ShoppingBasket"),
        new("restaurants", "Restaurants & cafés", CategoryKind.Expense, "Restaurant"),
        new("transport", "Transport", CategoryKind.Expense, "DirectionsBus"),
        new("car", "Car & fuel", CategoryKind.Expense, "LocalGasStation"),
        new("housing", "Housing", CategoryKind.Expense, "Home"),
        new("utilities", "Utilities", CategoryKind.Expense, "Bolt"),
        new("telecom", "Phone & internet", CategoryKind.Expense, "Wifi"),
        new("subscriptions", "Subscriptions", CategoryKind.Expense, "Subscriptions"),
        new("shopping", "Shopping", CategoryKind.Expense, "ShoppingBag"),
        new("health", "Health", CategoryKind.Expense, "LocalPharmacy"),
        new("insurance", "Insurance", CategoryKind.Expense, "Shield"),
        new("entertainment", "Entertainment", CategoryKind.Expense, "Theaters"),
        new("travel", "Travel", CategoryKind.Expense, "Flight"),
        new("education", "Education", CategoryKind.Expense, "School"),
        new("personal", "Personal care", CategoryKind.Expense, "Spa"),
        new("gifts", "Gifts & donations", CategoryKind.Expense, "CardGiftcard"),
        new("fees", "Fees & interest", CategoryKind.Expense, "Percent"),
        new("taxes", "Taxes", CategoryKind.Expense, "AccountBalance"),
        new("cash", "Cash withdrawal", CategoryKind.Expense, "LocalAtm"),
        new(OtherExpense, "Other expenses", CategoryKind.Expense, "MoreHoriz"),
        new("salary", "Salary", CategoryKind.Income, "Work"),
        new("refunds", "Refunds", CategoryKind.Income, "Replay"),
        new(OtherIncome, "Other income", CategoryKind.Income, "Savings"),
        new(Transfers, "Transfers", CategoryKind.Transfer, "SwapHoriz"),
    ];

    private static readonly Dictionary<string, Category> ById = All.ToDictionary(c => c.Id);

    public static bool Exists(string id) => ById.ContainsKey(id);

    public static Category Get(string id) =>
        ById.TryGetValue(id, out var c) ? c : throw new ArgumentException($"Unknown category '{id}'.", nameof(id));

    /// <summary>Default category for an amount nobody recognised.</summary>
    public static string DefaultFor(decimal amount) => amount >= 0 ? OtherIncome : OtherExpense;
}
