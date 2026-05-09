namespace OnaPlotter.Utilities;

/// <summary>
/// Classifies AIS MMSI numbers in the ITU-reserved ranges for distress
/// transmitters. An MMSI starting with 970 / 972 / 974 is not a vessel;
/// it's a beacon whose appearance on the net is itself the alarm.
///
/// <list type="bullet">
///   <item><c>970xxxxxx</c> - AIS SART (Search And Rescue Transmitter,
///     typically in a life raft)</item>
///   <item><c>972xxxxxx</c> - AIS MOB (Man Overboard beacon)</item>
///   <item><c>974xxxxxx</c> - AIS EPIRB (Emergency Position Indicating
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
    /// context when the bare MMSI field isn't populated yet.
    /// <para>Per-vessel hot path on the AIS push (200+ vessels at
    /// ~3 Hz). The fallback's <see cref="OnaPlotter.Models.AisVessel.ExtractMmsi"/>
    /// allocates a substring on every call - even when the context
    /// belongs to a regular MMSI (~99% of vessels). The "mmsi:9"
    /// fast-path skips the allocation when no 97x SART/MOB/EPIRB
    /// MMSI can be hiding in the context.</para></summary>
    public static string? CategoryFromAny(string? mmsi, string context)
    {
        var fromMmsi = Category(mmsi);
        if (fromMmsi is not null) return fromMmsi;
        // Cheap span-level prefilter: SART/MOB/EPIRB MMSIs all start
        // with "97" (970, 972, 974), so the context substring after
        // the "mmsi:" anchor must start with '9'. The full prefix
        // "mmsi:9" is what we look for; if absent, no SART can be
        // hiding here and we skip the allocation that ExtractMmsi
        // would do.
        if (context.IndexOf("mmsi:9", StringComparison.Ordinal) < 0) return null;
        return Category(OnaPlotter.Models.AisVessel.ExtractMmsi(context));
    }
}
