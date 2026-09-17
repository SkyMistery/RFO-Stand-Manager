namespace RfoGateManager.Domain;

/// <summary>Categoria ICAO di riferimento dell'aerodromo (apertura alare).</summary>
public enum SizeCategory { A = 1, B = 2, C = 3, D = 4, E = 5, F = 6 }

public enum LegKind { Arrival, Departure, Turnaround }

/// <summary>Uno stand come dichiarato nel file .gts di Aurora, arricchito con gli attributi operativi.</summary>
public sealed record Stand
{
    public required string Id { get; init; }
    public required string Icao { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
    public string? GtsType { get; init; }

    /// <summary>Massima categoria accettata. Un C non entra in uno stand B.</summary>
    public SizeCategory MaxSize { get; init; } = SizeCategory.C;

    /// <summary>Stand con pontile.</summary>
    public bool Contact { get; init; }

    /// <summary>Usi ammessi: pax, cargo, ga, mil. Vuoto = tutti.</summary>
    public IReadOnlyList<string> Uses { get; init; } = [];

    /// <summary>Compagnie preferenziali (prefisso callsign ICAO, es. AZA, RYR).</summary>
    public IReadOnlyList<string> Airlines { get; init; } = [];

    /// <summary>
    /// Stand MARS: occupare questo rende inagibili questi altri (e viceversa, la relazione
    /// viene resa simmetrica al caricamento).
    /// </summary>
    public IReadOnlyList<string> Blocks { get; init; } = [];

    /// <summary>A parità di punteggio vince la priorità più bassa.</summary>
    public int Priority { get; init; } = 100;

    /// <summary>Stand escluso dall'allocazione automatica (lavori, chiuso, riservato).</summary>
    public bool Disabled { get; init; }

    public string? Note { get; init; }
}

/// <summary>Una tratta singola: un arrivo o una partenza a LIRN.</summary>
public sealed record FlightLeg
{
    public required string Callsign { get; init; }
    public string? Registration { get; init; }
    public string AircraftType { get; init; } = "";
    public required string Departure { get; init; }
    public required string Arrival { get; init; }

    /// <summary>Ora stimata on-blocks (per gli arrivi).</summary>
    public DateTimeOffset? Eta { get; init; }

    /// <summary>Ora stimata off-blocks (per le partenze).</summary>
    public DateTimeOffset? Etd { get; init; }

    public required LegKind Kind { get; init; }
    public string? BookedStand { get; init; }

    /// <summary>booking | whazzup | aurora | manual</summary>
    public string Source { get; init; } = "manual";

    public int? Vid { get; init; }
    public bool IsOnline { get; init; }
    public string? TrackState { get; init; }
    public double? DistanceToArrivalNm { get; init; }
}

/// <summary>
/// Una richiesta di stand: una rotazione completa (arrivo + eventuale ripartenza) occupa
/// lo stand in un unico intervallo continuo, da on-blocks a off-blocks.
/// </summary>
public sealed record StandRequest
{
    public required string Key { get; init; }
    public required string Callsign { get; init; }
    public string AircraftType { get; init; } = "";
    public SizeCategory Size { get; init; } = SizeCategory.C;
    public string Use { get; init; } = "pax";
    public string? AirlineCode { get; init; }

    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    public string? BookedStand { get; init; }
    public FlightLeg? Inbound { get; init; }
    public FlightLeg? Outbound { get; init; }

    /// <summary>Lo stand su cui l'aereo si trova fisicamente adesso, letto da Aurora (#TRPOS campo 17).</summary>
    public string? ActualStand { get; init; }

    public bool Locked { get; init; }
}

public sealed record Assignment
{
    public required string Key { get; init; }
    public required string Callsign { get; init; }
    public string? StandId { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>Perché è stato scelto questo stand, o perché non se n'è trovato nessuno.</summary>
    public string Reason { get; init; } = "";

    public bool Conflict { get; init; }
    public bool PushedToAurora { get; init; }
    public bool Manual { get; init; }
}

public sealed record AllocationOptions
{
    /// <summary>Minuti di rullaggio dall'atterraggio all'on-blocks.</summary>
    public int TaxiInMinutes { get; init; } = 8;

    /// <summary>Minuti fra off-blocks e decollo.</summary>
    public int TaxiOutMinutes { get; init; } = 12;

    /// <summary>Margine fra un occupante e il successivo sullo stesso stand.</summary>
    public int BufferMinutes { get; init; } = 10;

    /// <summary>Se un volo è solo in partenza, da quanto prima dell'EOBT occupa lo stand.</summary>
    public int DepartureOccupancyMinutes { get; init; } = 60;

    /// <summary>Durata assunta per una sosta di cui non si conosce la ripartenza.</summary>
    public int DefaultTurnaroundMinutes { get; init; } = 90;

    /// <summary>Finestra massima entro cui un arrivo e una partenza sono la stessa rotazione.</summary>
    public int RotationWindowHours { get; init; } = 8;

    public bool PreferContactStands { get; init; } = true;
}
