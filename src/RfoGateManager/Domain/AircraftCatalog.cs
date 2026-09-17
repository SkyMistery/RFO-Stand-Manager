namespace RfoGateManager.Domain;

/// <summary>
/// Mappa tipo ICAO -> categoria di riferimento ICAO (lettera di codice, basata sull'apertura alare).
/// A &lt;15m, B 15-24m, C 24-36m, D 36-52m, E 52-65m, F 65-80m.
/// Coperti i tipi che realisticamente si vedono a un RFO italiano; il resto ricade su C.
/// </summary>
public static class AircraftCatalog
{
    private static readonly Dictionary<string, SizeCategory> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // A - aviazione generale
        ["C152"] = SizeCategory.A, ["C172"] = SizeCategory.A, ["C182"] = SizeCategory.A,
        ["P28A"] = SizeCategory.A, ["PA28"] = SizeCategory.A, ["DA40"] = SizeCategory.A,
        ["DA42"] = SizeCategory.A, ["SR22"] = SizeCategory.A, ["BE58"] = SizeCategory.A,
        ["C25A"] = SizeCategory.A, ["C25B"] = SizeCategory.A, ["C510"] = SizeCategory.A,

        // B - regionali e business jet
        ["AT43"] = SizeCategory.B, ["AT45"] = SizeCategory.B, ["AT72"] = SizeCategory.B,
        ["AT75"] = SizeCategory.B, ["AT76"] = SizeCategory.B,
        ["DH8A"] = SizeCategory.B, ["DH8C"] = SizeCategory.B, ["DH8D"] = SizeCategory.B,
        ["E135"] = SizeCategory.B, ["E145"] = SizeCategory.B, ["E45X"] = SizeCategory.B,
        ["CRJ1"] = SizeCategory.B, ["CRJ2"] = SizeCategory.B, ["CRJ7"] = SizeCategory.B,
        ["SF34"] = SizeCategory.B, ["J328"] = SizeCategory.B, ["BE20"] = SizeCategory.B,
        ["C56X"] = SizeCategory.B, ["CL60"] = SizeCategory.B, ["GLF4"] = SizeCategory.B,
        ["GLF5"] = SizeCategory.B, ["GLF6"] = SizeCategory.B, ["FA7X"] = SizeCategory.B,
        ["E50P"] = SizeCategory.B, ["E55P"] = SizeCategory.B, ["PC12"] = SizeCategory.B,

        // C - narrowbody, il grosso del traffico
        ["A19N"] = SizeCategory.C, ["A20N"] = SizeCategory.C, ["A21N"] = SizeCategory.C,
        ["A318"] = SizeCategory.C, ["A319"] = SizeCategory.C, ["A320"] = SizeCategory.C,
        ["A321"] = SizeCategory.C,
        ["B733"] = SizeCategory.C, ["B734"] = SizeCategory.C, ["B735"] = SizeCategory.C,
        ["B736"] = SizeCategory.C, ["B737"] = SizeCategory.C, ["B738"] = SizeCategory.C,
        ["B739"] = SizeCategory.C, ["B37M"] = SizeCategory.C, ["B38M"] = SizeCategory.C,
        ["B39M"] = SizeCategory.C, ["B3XM"] = SizeCategory.C,
        ["E170"] = SizeCategory.C, ["E175"] = SizeCategory.C, ["E190"] = SizeCategory.C,
        ["E195"] = SizeCategory.C, ["E290"] = SizeCategory.C, ["E295"] = SizeCategory.C,
        ["CRJ9"] = SizeCategory.C, ["CRJX"] = SizeCategory.C,
        ["BCS1"] = SizeCategory.C, ["BCS3"] = SizeCategory.C,
        ["MD82"] = SizeCategory.C, ["MD83"] = SizeCategory.C, ["MD87"] = SizeCategory.C,
        ["MD88"] = SizeCategory.C, ["B712"] = SizeCategory.C, ["F100"] = SizeCategory.C,
        ["SU95"] = SizeCategory.C,

        // D - narrowbody lunghi e widebody stretti
        ["B752"] = SizeCategory.D, ["B753"] = SizeCategory.D,
        ["B762"] = SizeCategory.D, ["B763"] = SizeCategory.D, ["B764"] = SizeCategory.D,
        ["A310"] = SizeCategory.D, ["A306"] = SizeCategory.D, ["A30B"] = SizeCategory.D,
        ["C130"] = SizeCategory.D, ["DC10"] = SizeCategory.D,

        // E - widebody
        ["A332"] = SizeCategory.E, ["A333"] = SizeCategory.E, ["A338"] = SizeCategory.E,
        ["A339"] = SizeCategory.E, ["A342"] = SizeCategory.E, ["A343"] = SizeCategory.E,
        ["A345"] = SizeCategory.E, ["A346"] = SizeCategory.E,
        ["A359"] = SizeCategory.E, ["A35K"] = SizeCategory.E,
        ["B772"] = SizeCategory.E, ["B773"] = SizeCategory.E, ["B77L"] = SizeCategory.E,
        ["B77W"] = SizeCategory.E, ["B778"] = SizeCategory.E, ["B779"] = SizeCategory.E,
        ["B788"] = SizeCategory.E, ["B789"] = SizeCategory.E, ["B78X"] = SizeCategory.E,
        ["B741"] = SizeCategory.E, ["B742"] = SizeCategory.E, ["B743"] = SizeCategory.E,
        ["B744"] = SizeCategory.E, ["B74S"] = SizeCategory.E, ["MD11"] = SizeCategory.E,
        ["IL76"] = SizeCategory.E,

        // F - superjumbo
        ["A388"] = SizeCategory.F, ["B748"] = SizeCategory.F,
        ["A124"] = SizeCategory.F, ["A225"] = SizeCategory.F,
    };

    /// <summary>Tipi merci: se il piano di volo li usa, preferiamo uno stand cargo.</summary>
    private static readonly HashSet<string> CargoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "B77L", "B74S", "MD11", "IL76", "A124", "A225", "C130", "B763", "AT72", "AT76",
    };

    public static SizeCategory SizeOf(string? icaoType)
    {
        if (string.IsNullOrWhiteSpace(icaoType)) return SizeCategory.C;
        return Map.TryGetValue(icaoType.Trim(), out var s) ? s : SizeCategory.C;
    }

    public static bool IsLikelyCargoType(string? icaoType) =>
        !string.IsNullOrWhiteSpace(icaoType) && CargoTypes.Contains(icaoType.Trim());

    /// <summary>True se il tipo è aviazione generale (categoria A e non di linea).</summary>
    public static bool IsGeneralAviation(string? icaoType) => SizeOf(icaoType) == SizeCategory.A;

    /// <summary>Il prefisso compagnia ICAO di 3 lettere, se il callsign ce l'ha.</summary>
    public static string? AirlineOf(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign) || callsign.Length < 4) return null;
        var prefix = callsign[..3];
        return prefix.All(char.IsLetter) ? prefix.ToUpperInvariant() : null;
    }
}
