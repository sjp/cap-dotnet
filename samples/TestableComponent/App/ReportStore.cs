using System.Globalization;
using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;

namespace TestableComponent;

/// <summary>
/// Keeps one report per day, as a file named for the day, in the directory it is given.
/// </summary>
/// <remarks>
/// <para>
/// Takes <see cref="IDir"/> rather than <see cref="Dir"/> so that a test can hand it a stub.
/// That is safe here because every name the store touches is one it made itself from a
/// <see cref="DateOnly"/>: nothing a caller passes becomes part of a path. A component that
/// resolves names it was given by someone else, such as an uploaded file name, takes
/// <see cref="Dir"/>, because containment belongs to that type and not to the interface.
/// </para>
/// <para>
/// The composition root still hands it a <see cref="Dir"/>, so in production it is confined
/// all the same.
/// </para>
/// </remarks>
public sealed class ReportStore(IDir reports)
{
    private const string Format = "yyyy-MM-dd";
    private const string Extension = ".json";

    /// <summary>Replaces the day's report, so that a reader sees the old one or the new one, never a mixture.</summary>
    public void Save(DateOnly day, string json) => reports.WriteAllTextAtomic(NameOf(day), json);

    /// <summary>The day's report, or null if there is none.</summary>
    public string? Load(DateOnly day)
    {
        string name = NameOf(day);
        return reports.Exists(name) ? reports.ReadAllText(name) : null;
    }

    /// <summary>The days that have a report, oldest first. Anything else in the directory is ignored.</summary>
    public IReadOnlyList<DateOnly> ListDays()
    {
        List<DateOnly> days = [];
        foreach (IDirEntry entry in reports.EnumerateEntries())
        {
            if (entry.Type == CapFileType.File && TryParse(entry.Name, out DateOnly day))
            {
                days.Add(day);
            }
        }

        days.Sort();
        return days;
    }

    /// <summary>Removes every report older than <paramref name="oldestKept"/>, and returns how many it removed.</summary>
    public int Prune(DateOnly oldestKept)
    {
        int removed = 0;
        foreach (DateOnly day in ListDays())
        {
            if (day < oldestKept && reports.TryDeleteFile(NameOf(day)))
            {
                removed++;
            }
        }

        return removed;
    }

    private static string NameOf(DateOnly day) => day.ToString(Format, CultureInfo.InvariantCulture) + Extension;

    private static bool TryParse(string name, out DateOnly day)
    {
        day = default;
        return name.EndsWith(Extension, StringComparison.Ordinal)
            && DateOnly.TryParseExact(name[..^Extension.Length], Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }
}
