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
        ["C68A"] = SizeCategory.B, ["C700"] = SizeCategory.B, ["CL35"] = SizeCategory.B,
        ["CL30"] = SizeCategory.B, ["G280"] = SizeCategory.B, ["F2TH"] = SizeCategory.B,
        ["FA8X"] = SizeCategory.C, ["GLEX"] = SizeCategory.C, ["GL7T"] = SizeCategory.C,
        ["PC24"] = SizeCategory.B, ["LJ45"] = SizeCategory.A, ["H25B"] = SizeCategory.B,
        ["TBM8"] = SizeCategory.A, ["TBM9"] = SizeCategory.A, ["TBM7"] = SizeCategory.A,

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

    /// <summary>Apertura alare e lunghezza fuori tutto, in metri.</summary>
    public readonly record struct AircraftDimensions(double WingspanM, double LengthM);

    /// <summary>
    /// Misure dei tipi piu' comuni, per confrontarle con i limiti di stand dell'AIP.
    /// Un tipo assente qui ricade sulla lettera di codice, che e' piu' grossolana.
    /// </summary>
    private static readonly Dictionary<string, AircraftDimensions> Dimensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Aviazione generale e business jet
        ["C152"] = new(10.16, 7.29),  ["C172"] = new(11.00, 8.28),  ["C182"] = new(10.97, 8.84),
        ["P28A"] = new(10.67, 7.25),  ["PA28"] = new(10.67, 7.25),  ["SR22"] = new(11.68, 7.92),
        ["DA40"] = new(11.94, 8.06),  ["DA42"] = new(13.42, 8.56),  ["BE58"] = new(11.53, 9.09),
        ["BE20"] = new(16.61, 13.34), ["PC12"] = new(16.28, 14.40), ["C510"] = new(12.37, 12.98),
        ["C25A"] = new(15.16, 14.38), ["C25B"] = new(16.26, 15.45), ["C56X"] = new(16.97, 14.91),
        ["CL60"] = new(19.61, 20.85), ["GLF4"] = new(23.72, 26.92), ["GLF5"] = new(28.50, 29.40),
        ["GLF6"] = new(28.50, 30.40), ["FA7X"] = new(26.21, 23.38), ["E50P"] = new(12.47, 12.82),
        ["E55P"] = new(14.35, 14.21), ["C68A"] = new(22.05, 18.97), ["C700"] = new(20.93, 22.30),
        ["CL35"] = new(21.00, 20.90), ["CL30"] = new(19.46, 20.92), ["G280"] = new(19.20, 20.30),
        ["F2TH"] = new(21.38, 20.23), ["FA8X"] = new(26.29, 24.46), ["GLEX"] = new(28.65, 30.30),
        ["GL7T"] = new(31.70, 33.80), ["PC24"] = new(17.00, 16.85), ["LJ45"] = new(14.58, 17.68),
        ["H25B"] = new(15.66, 15.60), ["TBM8"] = new(12.83, 10.64), ["TBM9"] = new(12.83, 10.74),
        ["TBM7"] = new(12.68, 10.64),

        // Regionali
        ["AT43"] = new(24.57, 22.67), ["AT45"] = new(24.57, 22.67), ["AT72"] = new(27.05, 27.17),
        ["AT75"] = new(27.05, 27.17), ["AT76"] = new(27.05, 27.17),
        ["DH8C"] = new(25.91, 25.68), ["DH8D"] = new(28.42, 32.84),
        ["SF34"] = new(21.44, 19.72), ["J328"] = new(20.98, 21.28),
        ["E135"] = new(20.04, 26.33), ["E145"] = new(20.04, 29.87), ["E45X"] = new(20.04, 29.87),
        ["CRJ1"] = new(21.21, 26.77), ["CRJ2"] = new(21.21, 26.77), ["CRJ7"] = new(23.24, 32.30),
        ["CRJ9"] = new(24.85, 36.40), ["CRJX"] = new(26.18, 39.10),

        // Narrowbody
        ["A318"] = new(34.10, 31.44), ["A319"] = new(35.80, 33.84), ["A320"] = new(35.80, 37.57),
        ["A321"] = new(35.80, 44.51), ["A19N"] = new(35.80, 33.84), ["A20N"] = new(35.80, 37.57),
        ["A21N"] = new(35.80, 44.51),
        ["B733"] = new(28.88, 33.40), ["B734"] = new(28.88, 36.45), ["B735"] = new(28.88, 31.00),
        ["B736"] = new(34.30, 31.20), ["B737"] = new(34.30, 33.63), ["B738"] = new(35.79, 39.47),
        ["B739"] = new(35.79, 42.11), ["B37M"] = new(35.90, 35.56), ["B38M"] = new(35.90, 39.52),
        ["B39M"] = new(35.90, 42.16), ["B712"] = new(28.45, 37.80),
        ["BCS1"] = new(35.10, 35.00), ["BCS3"] = new(35.10, 38.70),
        ["E170"] = new(26.00, 29.90), ["E175"] = new(26.00, 31.68), ["E190"] = new(28.72, 36.24),
        ["E195"] = new(28.72, 38.65), ["E290"] = new(33.72, 36.24), ["E295"] = new(35.10, 41.50),
        ["MD82"] = new(32.85, 45.06), ["MD83"] = new(32.85, 45.06), ["MD88"] = new(32.85, 45.06),
        ["MD87"] = new(32.85, 39.75), ["F100"] = new(28.08, 35.53), ["SU95"] = new(27.80, 29.94),

        // Widebody e quadrigetti
        ["B752"] = new(38.05, 47.32), ["B753"] = new(38.05, 54.47),
        ["B762"] = new(47.57, 48.51), ["B763"] = new(47.57, 54.94), ["B764"] = new(51.90, 61.37),
        ["A306"] = new(44.84, 54.08), ["A30B"] = new(44.84, 53.62), ["A310"] = new(43.90, 46.66),
        ["A332"] = new(60.30, 58.82), ["A333"] = new(60.30, 63.69),
        ["A338"] = new(64.00, 58.80), ["A339"] = new(64.00, 63.70),
        ["A342"] = new(60.30, 59.40), ["A343"] = new(60.30, 63.69),
        ["A345"] = new(63.45, 67.90), ["A346"] = new(63.45, 75.30),
        ["A359"] = new(64.75, 66.80), ["A35K"] = new(64.75, 73.79),
        ["B772"] = new(60.93, 63.73), ["B773"] = new(60.93, 73.86),
        ["B77L"] = new(64.80, 63.70), ["B77W"] = new(64.80, 73.86),
        ["B788"] = new(60.12, 56.72), ["B789"] = new(60.12, 62.81), ["B78X"] = new(60.12, 68.28),
        ["B741"] = new(59.64, 70.66), ["B742"] = new(59.64, 70.66), ["B743"] = new(59.64, 70.66),
        ["B744"] = new(64.44, 70.66), ["B74S"] = new(59.64, 56.31), ["B748"] = new(68.40, 76.25),
        ["MD11"] = new(51.66, 61.21), ["IL76"] = new(50.50, 46.59), ["C130"] = new(40.41, 29.79),
        ["DC10"] = new(50.40, 55.50),
        ["A388"] = new(79.75, 72.72), ["A124"] = new(73.30, 69.10), ["A225"] = new(88.40, 84.00),
    };

    /// <summary>Misure del tipo, oppure null se non lo conosciamo.</summary>
    public static AircraftDimensions? DimensionsOf(string? icaoType)
    {
        if (string.IsNullOrWhiteSpace(icaoType)) return null;
        return Dimensions.TryGetValue(icaoType.Trim(), out var d) ? d : null;
    }

    public static SizeCategory SizeOf(string? icaoType)
    {
        if (string.IsNullOrWhiteSpace(icaoType)) return SizeCategory.C;
        return Map.TryGetValue(icaoType.Trim(), out var s) ? s : SizeCategory.C;
    }

    public static bool IsLikelyCargoType(string? icaoType) =>
        !string.IsNullOrWhiteSpace(icaoType) && CargoTypes.Contains(icaoType.Trim());

    /// <summary>True se il tipo è aviazione generale (categoria A e non di linea).</summary>
    public static bool IsGeneralAviation(string? icaoType) => SizeOf(icaoType) == SizeCategory.A;

    /// <summary>Business jet e turboelica executive: vanno sugli stand dei jet privati.</summary>
    private static readonly HashSet<string> BusinessTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "C25A", "C25B", "C25C", "C510", "C525", "C550", "C560", "C56X", "C650", "C680", "C68A",
        "C700", "C750", "CL30", "CL35", "CL60", "GL5T", "GL7T", "GLEX", "GLF4", "GLF5", "GLF6",
        "G280", "FA50", "FA7X", "FA8X", "F2TH", "F900", "E50P", "E55P", "E545", "E550",
        "LJ35", "LJ45", "LJ60", "LJ75", "H25B", "BE40", "PRM1", "HDJT", "PC12", "PC24",
        "TBM7", "TBM8", "TBM9", "P180", "BE20", "BE9L",
    };

    /// <summary>
    /// Jet privato o aviazione generale. Lo riconosciamo dal tipo, oppure dal callsign quando
    /// è una marca e non un volo di compagnia: "N900FZ", "IABCD", "D-IABC".
    /// </summary>
    public static bool IsBusinessOrGa(string? icaoType, string? callsign) =>
        IsGeneralAviation(icaoType)
        || (!string.IsNullOrWhiteSpace(icaoType) && BusinessTypes.Contains(icaoType.Trim()))
        || LooksLikeRegistration(callsign);

    /// <summary>
    /// Un callsign che è una marca. I voli di linea hanno un prefisso di compagnia seguito da
    /// cifre (AZA1234, RYR54TG); le marche no: tutte lettere (IABCD), col trattino (D-IABC)
    /// o nel formato americano N seguito da cifre (N900FZ).
    /// </summary>
    public static bool LooksLikeRegistration(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return false;
        var cs = callsign.Trim().ToUpperInvariant();

        if (cs.Contains('-')) return true;
        if (cs.Length is >= 4 and <= 6 && cs.All(char.IsLetter)) return true;
        return cs.Length is >= 3 and <= 6 && cs[0] == 'N' && char.IsDigit(cs[1]);
    }

    /// <summary>Widebody o comunque più grande di un narrowbody: oltre i 36 m di apertura.</summary>
    public static bool IsLarge(SizeCategory size, double? wingspanM) =>
        wingspanM is { } w ? w > 36.5 : size >= SizeCategory.D;

    /// <summary>Il prefisso compagnia ICAO di 3 lettere, se il callsign ce l'ha.</summary>
    public static string? AirlineOf(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign) || callsign.Length < 4) return null;
        var prefix = callsign[..3];
        return prefix.All(char.IsLetter) ? prefix.ToUpperInvariant() : null;
    }
}
