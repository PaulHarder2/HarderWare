using MetarParser.Data.Entities;

namespace MetarParser.Data;

/// <summary>
/// Propagates locality membership and the shared locality fields onto recipients.
/// The locality is authoritative: a member's display name and METAR/TAF ICAO mirror
/// its locality's, so every recipient in a locality shares one identity — the basis
/// for batching the expensive Claude reconciliation once per locality (WX-123).
/// </summary>
public static class LocalityAssignment
{
    /// <summary>
    /// Assigns <paramref name="recipient"/> to <paramref name="locality"/>: sets the
    /// foreign key and mirrors the locality's display name and station hierarchy
    /// verbatim onto the recipient (<see cref="Recipient.LocalityName"/>,
    /// <see cref="Recipient.MetarIcao"/>, <see cref="Recipient.TafIcao"/>).
    /// </summary>
    public static void Assign(Recipient recipient, Locality locality)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(locality);

        recipient.LocalityId = locality.Id;
        MirrorFromLocality(recipient, locality);
    }

    /// <summary>
    /// Re-applies <paramref name="locality"/>'s display name and station hierarchy to
    /// every recipient in <paramref name="members"/> — used when a locality's name or
    /// stations change so its members stay in sync. Members are assumed to already
    /// belong to the locality.
    /// </summary>
    public static void SyncMembers(Locality locality, IEnumerable<Recipient> members)
    {
        ArgumentNullException.ThrowIfNull(locality);
        ArgumentNullException.ThrowIfNull(members);

        foreach (var member in members)
            MirrorFromLocality(member, locality);
    }

    /// <summary>
    /// Mirrors the locality's display name and station hierarchy verbatim (stations may
    /// be <see langword="null"/>) onto the recipient, so the recipient always reflects
    /// its locality.
    /// </summary>
    private static void MirrorFromLocality(Recipient recipient, Locality locality)
    {
        recipient.LocalityName = locality.Name;
        recipient.MetarIcao = locality.MetarIcao;
        recipient.TafIcao = locality.TafIcao;
    }
}