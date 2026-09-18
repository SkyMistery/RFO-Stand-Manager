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

    /// <summary>
    /// Apertura alare massima in metri, dall'AIP. Quando c'e' batte la categoria: a Napoli
    /// lo stand 23 e' codice C ma accetta solo 32 m, e un A320 (35,8 m) non ci entra.
    /// </summary>
    public double? MaxWingspanM { get; init; }

    /// <summary>Lunghezza fuori tutto massima in metri, dall'AIP.</summary>
    public double? MaxLengthM { get; init; }

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

    /// <summary>
    /// Ordine di comodità: più basso = più comodo. Separa i gruppi di stand (a Napoli
    /// 10-23, poi 51-57, poi 41-46, poi apron 2, poi apron 3) e pesa più dello spazio
    /// sprecato, quindi un gruppo viene riempito prima di passare al successivo.
    /// </summary>
    public int Priority { get; init; } = 100;

    public int? Apron { get; init; }

    /// <summary>
    /// A chi va di solito questo stand: "large" (widebody), "ga" (jet privati). Non è un
    /// divieto: chi corrisponde lo preferisce a qualsiasi altro stand, gli altri lo usano solo
    /// quando è pieno tutto il resto, apron 3 compreso.
    /// </summary>
    public IReadOnlyList<string> ReservedFor { get; init; } = [];

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

    /// <summary>Apertura alare del tipo, se la conosciamo.</summary>
    public double? WingspanM { get; init; }

    /// <summary>Lunghezza del tipo, se la conosciamo.</summary>
    public double? LengthM { get; init; }

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

    /// <summary>Aereo fermo su uno stand senza essere nel piano: arrivato senza prenotazione.</summary>
    public bool Unscheduled { get; init; }
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

    /// <summary>Due aerei sullo stesso stand nella stessa finestra: va risolto.</summary>
    public bool Conflict { get; init; }

    /// <summary>
    /// L'aereo non ci sta per misure, ma lo stand era gia' stato deciso (prenotazione o
    /// scelta del controllore) e quella decisione vince. Da segnalare, non da bloccare.
    /// </summary>
    public bool Oversize { get; init; }

    public bool PushedToAurora { get; init; }
    public bool Manual { get; init; }

    /// <summary>
    /// Lo stand deciso (prenotato o fissato a mano) è occupato fisicamente da un altro aereo,
    /// quindi a questo ne è stato dato un altro invece di far spostare chi è già a terra.
    /// </summary>
    public bool Reassigned { get; init; }

    /// <summary>Lo stand che avrebbe dovuto avere.</summary>
    public string? DisplacedFrom { get; init; }

    /// <summary>Chi lo occupa.</summary>
    public string? DisplacedBy { get; init; }

    /// <summary>
    /// Lo stand perso era stato fissato a mano, quindi probabilmente già comunicato al pilota:
    /// il nuovo va ricomunicato.
    /// </summary>
    public bool WasPinned { get; init; }

    public bool Unscheduled { get; init; }
}

public sealed record AllocationOptions
{
    /// <summary>Minuti di rullaggio dall'atterraggio all'on-blocks.</summary>
    public int TaxiInMinutes { get; init; } = 8;

    /// <summary>Minuti fra off-blocks e decollo.</summary>
    public int TaxiOutMinutes { get; init; } = 12;

    /// <summary>
    /// Margine fra un occupante e il successivo sullo stesso stand. Cinque minuti perche'
    /// e' cosi' che il booking dell'evento impacchetta gli slot: con dieci, una partenza
    /// alle 13:00 e un arrivo alle 13:05 risultavano in conflitto senza esserlo davvero.
    /// </summary>
    public int BufferMinutes { get; init; } = 5;

    /// <summary>Se un volo è solo in partenza, da quanto prima dell'EOBT occupa lo stand.</summary>
    public int DepartureOccupancyMinutes { get; init; } = 60;

    /// <summary>Durata assunta per una sosta di cui non si conosce la ripartenza.</summary>
    public int DefaultTurnaroundMinutes { get; init; } = 90;

    /// <summary>Finestra massima entro cui un arrivo e una partenza sono la stessa rotazione.</summary>
    public int RotationWindowHours { get; init; } = 8;

    public bool PreferContactStands { get; init; } = true;

    /// <summary>
    /// Per quanto si considera occupato uno stand da un aereo di cui non si sa quando ripartirà.
    /// La finestra scorre: finché l'aereo resta lì, ogni ricalcolo la sposta in avanti. Gli
    /// arrivi che cadono dentro questa finestra vengono dirottati su un altro stand.
    /// </summary>
    public int UnscheduledOccupancyMinutes { get; init; } = 60;
}
