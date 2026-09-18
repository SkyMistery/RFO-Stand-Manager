namespace RfoGateManager.Aurora;

/// <summary>Piano di volo restituito da <c>#FP</c> (15 campi).</summary>
public sealed record AuroraFlightPlan
{
    public required string Callsign { get; init; }
    public string Departure { get; init; } = "";
    public string Arrival { get; init; } = "";
    public string Alternate { get; init; } = "";
    public string EstimatedDepartureTime { get; init; } = "";
    public string AircraftType { get; init; } = "";
    public string WakeTurbulence { get; init; } = "";
    public string FlightType { get; init; } = "";
    public string FlightRules { get; init; } = "";
    public string Equipment { get; init; } = "";
    public string CruisingAltitude { get; init; } = "";
    public string CruisingSpeed { get; init; } = "";
    public string Endurance { get; init; } = "";
    public string EstimatedFlightTime { get; init; } = "";
    public string Route { get; init; } = "";
    public string Remarks { get; init; } = "";

    public static AuroraFlightPlan Parse(string callsign, IReadOnlyList<string> f) => new()
    {
        Callsign = callsign,
        Departure = At(f, 0),
        Arrival = At(f, 1),
        Alternate = At(f, 2),
        EstimatedDepartureTime = At(f, 3),
        AircraftType = At(f, 4),
        WakeTurbulence = At(f, 5),
        // Il manuale mette prima il tipo di volo e poi le regole, ma Aurora manda il
        // contrario: verificato su un volo vero, arriva "I;S" (IFR, poi linea).
        FlightRules = At(f, 6),
        FlightType = At(f, 7),
        Equipment = At(f, 8),
        CruisingAltitude = At(f, 9),
        CruisingSpeed = At(f, 10),
        Endurance = At(f, 11),
        EstimatedFlightTime = At(f, 12),
        Route = At(f, 13),
        Remarks = At(f, 14),
    };

    private static string At(IReadOnlyList<string> f, int i) => i < f.Count ? f[i] : "";
}

/// <summary>
/// Posizione restituita da <c>#TRPOS</c> (21 campi). I due che ci interessano davvero sono
/// il 17 (stand su cui l'aereo si trova) e il 21 (stand già assegnato).
/// </summary>
public sealed record AuroraTrafficPosition
{
    public required string Callsign { get; init; }
    public string Heading { get; init; } = "";
    public string Track { get; init; } = "";
    public int Altitude { get; init; }
    public int GroundSpeed { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string SsrSet { get; init; } = "";
    public string SsrLabel { get; init; } = "";
    public string WaypointLabel { get; init; } = "";
    public string AltitudeLabel { get; init; } = "";
    public string SpeedLabel { get; init; } = "";
    public string AssumedStation { get; init; } = "";
    public string NextStation { get; init; } = "";
    public bool OnGround { get; init; }
    public bool IsSelected { get; init; }
    public bool WasSelected { get; init; }
    public string CurrentGate { get; init; } = "";
    public string Voice { get; init; } = "";
    public string TransferAltitude { get; init; } = "";
    public int VerticalSpeed { get; init; }
    public string AssignedGate { get; init; } = "";

    public static AuroraTrafficPosition Parse(string callsign, IReadOnlyList<string> f) => new()
    {
        Callsign = callsign,
        Heading = At(f, 0),
        Track = At(f, 1),
        Altitude = Int(f, 2),
        GroundSpeed = Int(f, 3),
        Latitude = Dbl(f, 4),
        Longitude = Dbl(f, 5),
        SsrSet = At(f, 6),
        SsrLabel = At(f, 7),
        WaypointLabel = At(f, 8),
        AltitudeLabel = At(f, 9),
        SpeedLabel = At(f, 10),
        AssumedStation = At(f, 11),
        NextStation = At(f, 12),
        OnGround = Bool(f, 13),
        IsSelected = Bool(f, 14),
        WasSelected = Bool(f, 15),
        CurrentGate = At(f, 16),
        Voice = At(f, 17),
        TransferAltitude = At(f, 18),
        VerticalSpeed = Int(f, 19),
        AssignedGate = At(f, 20),
    };

    private static string At(IReadOnlyList<string> f, int i) => i < f.Count ? f[i].Trim() : "";

    private static int Int(IReadOnlyList<string> f, int i) =>
        int.TryParse(At(f, i), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Dbl(IReadOnlyList<string> f, int i) =>
        double.TryParse(At(f, i), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static bool Bool(IReadOnlyList<string> f, int i)
    {
        var s = At(f, i);
        return s is "1" or "Y" or "y" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AuroraException(string message) : Exception(message);
