namespace OnaPlotter.Utilities;

/// <summary>
/// Classifies AIS MMSI numbers in the ITU-reserved ranges for distress
/// transmitters. An MMSI starting with 970 / 972 / 974 is not a vessel;
/// it's a beacon whose appearance on the net is itself the alarm.
///
/// <list type="bullet">
///   <item><c>970xxxxxx</c> -- AIS SART (Search And Rescue Transmitter,
///     typically in a life raft)</item>
///   <item><c>972xxxxxx</c> -- AIS MOB (Man Overboard beacon)</item>
///   <item><c>974xxxxxx</c> -- AIS EPIRB (Emergency Position Indicating
///     Radio Beacon)</item>
/// </list>
///
/// Reference: ITU-R M.585-9 Annex 1.
/// </summary>
public static class AisSart
{
    /// <summary>SART category name (SART / MOB / EPIRB) or null for a
    /// regular AIS MMSI.</summary>
    public static string? Category(string? mmsi)
    {
        if (mmsi is null || mmsi.Length != 9) return null;
        if (mmsi[0] != '9' || mmsi[1] != '7') return null;
        return mmsi[2] switch
        {
            '0' => "SART",
            '2' => "MOB",
            '4' => "EPIRB",
            _ => null,
        };
    }

    /// <summary>True when <paramref name="mmsi"/> falls in any of the
    /// distress-transmitter ranges.</summary>
    public static bool IsSart(string? mmsi) => Category(mmsi) is not null;

    /// <summary>Looks up the category for an MMSI, falling back to
    /// extracting the MMSI from a SignalK <c>vessels.urn:mrn:imo:mmsi:NNN</c>
    /// context when the bare MMSI field isn't populated yet.</summary>
    public static string? CategoryFromAny(string? mmsi, string context)
    {
        var fromMmsi = Category(mmsi);
        if (fromMmsi is not null) return fromMmsi;
        return Category(OnaPlotter.Models.AisVessel.ExtractMmsi(context));
    }
}
