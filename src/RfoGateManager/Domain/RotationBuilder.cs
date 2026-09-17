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

            // La ripartenza dello stesso aereo: prima per callsign, poi per marche.
            var outbound = departures.FirstOrDefault(d =>
                !used.Contains(d) &&
                SameAircraft(arr, d) &&
                (d.Etd ?? now) >= onBlocks.AddMinutes(-5) &&
                (d.Etd ?? now) - onBlocks <= window);

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

    private static bool SameAircraft(FlightLeg arr, FlightLeg dep)
    {
        if (!string.IsNullOrWhiteSpace(arr.Registration) &&
            string.Equals(arr.Registration, dep.Registration, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(arr.Callsign, dep.Callsign, StringComparison.OrdinalIgnoreCase);
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

        return new StandRequest
        {
            Key = $"{primary.Callsign}:{from:yyyyMMddHHmm}",
            Callsign = primary.Callsign,
            AircraftType = type,
            Size = AircraftCatalog.SizeOf(type),
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
