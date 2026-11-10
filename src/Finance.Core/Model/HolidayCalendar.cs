namespace ProductivityApp.Finance.Model;

/// <summary>
/// A calendar of public holidays defined in Definitions > Holidays: the place it is for (free text, "Canada - Quebec"),
/// the address of its file (ICS or JSON; "{year}" in it stands for each year when the file holds one year), and
/// whether it is checked (its holidays are shown on the calendar and are not business days).
/// </summary>
public sealed record HolidayCalendar(string Id, string Location, string Url, bool Active)
{
    /// <summary>Quebec's holidays (the CNESST list), one file per year, from canada-holidays.ca.</summary>
    public static readonly HolidayCalendar Quebec = new("qc", "Canada - Quebec", "https://canada-holidays.ca/ics/QC/{year}", true);

    /// <summary>The US federal holidays of the Office of Personnel Management (their observed days), 2021 to 2030.</summary>
    public static readonly HolidayCalendar UnitedStates = new("us", "United States",
        "https://www.opm.gov/policy-data-oversight/pay-leave/federal-holidays/holidays.ics", false);

    /// <summary>France's public holidays (metropolitan France), from the government's service, twenty years back and five ahead.</summary>
    public static readonly HolidayCalendar France = new("fr", "France", "https://calendrier.api.gouv.fr/jours-feries/metropole.json", false);

    /// <summary>The calendars every database starts with (Quebec's checked), also offered again for a new line.</summary>
    public static readonly IReadOnlyList<HolidayCalendar> Defaults = [Quebec, UnitedStates, France];
}

/// <summary>One public holiday: its day and its name as its calendar writes it.</summary>
public sealed record Holiday(DateOnly Day, string Name);
