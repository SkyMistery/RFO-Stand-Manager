namespace RfoGateManager.Domain;

/// <summary>
/// Trasforma le tratte in richieste di stand. Se un arrivo e una partenza sono lo stesso
/// aereo, diventano una sola occupazione continua: lo stand resta impegnato da quando
/// atterra a quando riparte.
/// </summary>
public static class RotationBuilder
{
    public static List<StandRequest> Build(
        IEnumerable<FlightLeg> legs,
        string airport,
        AllocationOptions opt,
        DateTimeOffset now)
    {
        var all = legs.ToList();
        var arrivals = all
            .Where(l => l.Arrival.Equals(airport, StringComparison.OrdinalIgnoreCase) && l.Kind != LegKind.Departure)
            .OrderBy(l => l.Eta ?? now)
            .ToList();
        var departures = all
            .Where(l => l.Departure.Equals(airport, StringComparison.OrdinalIgnoreCase) && l.Kind != LegKind.Arrival)
            .OrderBy(l => l.Etd ?? now)
            .ToList();

        var used = new HashSet<FlightLeg>();
        var requests = new List<StandRequest>();
        var window = TimeSpan.FromHours(opt.RotationWindowHours);

        foreach (var arr in arrivals)
        {
            var onBlocks = arr.Eta ?? now;

            // La ripartenza dello stesso aereo, scegliendo il segnale piu' affidabile
            // fra quelli disponibili e, a parita' di segnale, la partenza piu' vicina.
            var outbound = departures
                .Where(d => !used.Contains(d)
                            && (d.Etd ?? now) >= onBlocks.AddMinutes(-5)
                            && (d.Etd ?? now) - onBlocks <= window)
                .Select(d => (Leg: d, Strength: MatchStrength(arr, d)))
                .Where(x => x.Strength > 0)
                .OrderByDescending(x => x.Strength)
                .ThenBy(x => x.Leg.Etd ?? now)
                .Select(x => x.Leg)
                .FirstOrDefault();

            DateTimeOffset offBlocks;
            if (outbound is not null)
            {
                used.Add(outbound);
                offBlocks = outbound.Etd ?? onBlocks.AddMinutes(opt.DefaultTurnaroundMinutes);
            }
            else
            {
                offBlocks = onBlocks.AddMinutes(opt.DefaultTurnaroundMinutes);
            }

            if (offBlocks <= onBlocks) offBlocks = onBlocks.AddMinutes(opt.DefaultTurnaroundMinutes);

            requests.Add(MakeRequest(arr, outbound, onBlocks, offBlocks));
        }

        // Partenze senza arrivo abbinato: l'aereo è già a terra (based, o arrivato prima dell'evento).
        foreach (var dep in departures.Where(d => !used.Contains(d)))
        {
            var offBlocks = dep.Etd ?? now;
            var onBlocks = offBlocks.AddMinutes(-opt.DepartureOccupancyMinutes);
            if (onBlocks < now.AddHours(-12)) onBlocks = now.AddHours(-12);
            requests.Add(MakeRequest(null, dep, onBlocks, offBlocks));
        }

        return requests.OrderBy(r => r.From).ThenBy(r => r.Callsign, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Quanto e' credibile che queste due tratte siano lo stesso aereo. Zero significa
    /// nessun legame; piu' alto e' il valore, piu' forte e' la prova.
    ///
    /// Lo stand prenotato pesa piu' del callsign perche' nel booking di un evento il
    /// callsign cambia quasi sempre fra andata e ritorno (AAL180 arriva, AAL781 riparte),
    /// mentre il sistema assegna a quella coppia lo stesso gate: e' il sistema stesso a
    /// dirci che e' un turnaround. Il VID viene per ultimo perche' spesso e' nullo o zero,
    /// e capita che nella stessa coppia i due voli risultino prenotati da piloti diversi.
    /// </summary>
    private static int MatchStrength(FlightLeg arr, FlightLeg dep)
    {
        // Le marche sono l'unica identificazione certa dell'aeromobile.
        if (!string.IsNullOrWhiteSpace(arr.Registration) &&
            string.Equals(arr.Registration, dep.Registration, StringComparison.OrdinalIgnoreCase))
            return 4;

        if (!string.IsNullOrWhiteSpace(arr.BookedStand) &&
            string.Equals(arr.BookedStand, dep.BookedStand, StringComparison.OrdinalIgnoreCase) &&
            SameType(arr, dep))
            return 3;

        if (string.Equals(arr.Callsign, dep.Callsign, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (arr.Vid is > 0 && arr.Vid == dep.Vid)
            return 1;

        return 0;
    }

    /// <summary>
    /// Stesso tipo di aeromobile. Serve a non scambiare per turnaround due aerei diversi
    /// che si danno il cambio sullo stesso stand.
    ///
    /// Se uno dei due tipi non e' riconosciuto vince lo stand: nelle prenotazioni capita di
    /// trovare refusi (un B738 ripartito come "B378") e rifiutare l'abbinamento per quello
    /// produrrebbe un conflitto inventato al posto di una rotazione reale.
    /// </summary>
    private static bool SameType(FlightLeg a, FlightLeg b)
    {
        if (string.IsNullOrWhiteSpace(a.AircraftType) || string.IsNullOrWhiteSpace(b.AircraftType))
            return true;

        if (string.Equals(a.AircraftType, b.AircraftType, StringComparison.OrdinalIgnoreCase))
            return true;

        return AircraftCatalog.DimensionsOf(a.AircraftType) is null
            || AircraftCatalog.DimensionsOf(b.AircraftType) is null;
    }

    private static StandRequest MakeRequest(
        FlightLeg? inbound, FlightLeg? outbound, DateTimeOffset from, DateTimeOffset to)
    {
        var primary = inbound ?? outbound!;
        var type = !string.IsNullOrWhiteSpace(primary.AircraftType)
            ? primary.AircraftType
            : outbound?.AircraftType ?? "";

        var use = AircraftCatalog.IsLikelyCargoType(type) ? "cargo"
                : AircraftCatalog.IsGeneralAviation(type) ? "ga"
                : "pax";

        var dims = AircraftCatalog.DimensionsOf(type);

        return new StandRequest
        {
            Key = $"{primary.Callsign}:{from:yyyyMMddHHmm}",
            Callsign = primary.Callsign,
            AircraftType = type,
            Size = AircraftCatalog.SizeOf(type),
            WingspanM = dims?.WingspanM,
            LengthM = dims?.LengthM,
            Use = use,
            AirlineCode = AircraftCatalog.AirlineOf(primary.Callsign),
            From = from,
            To = to,
            BookedStand = inbound?.BookedStand ?? outbound?.BookedStand,
            Inbound = inbound,
            Outbound = outbound,
        };
    }
}
