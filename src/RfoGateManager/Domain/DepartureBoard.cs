namespace RfoGateManager.Domain;

/// <summary>Quello che Aurora dice di un traffico in questo momento.</summary>
public sealed record TrafficStatus(
    string Callsign,
    bool OnGround,
    int GroundSpeed,
    int Altitude,
    string? AssumedBy);

/// <summary>Una strip della strippiera partenze.</summary>
public sealed record DepartureStrip
{
    public required string Key { get; init; }
    public required string Callsign { get; init; }
    public string AircraftType { get; init; } = "";
    public string Destination { get; init; } = "";
    public DateTimeOffset? Eobt { get; init; }
    public string? Stand { get; init; }

    /// <summary>Il pilota è connesso alla rete (lo vede Whazzup o Aurora).</summary>
    public bool Online { get; init; }

    public string? TrackState { get; init; }
    public string? AssumedBy { get; init; }

    public bool Called { get; init; }

    /// <summary>"manuale" se l'ha deciso un controllore, "aurora" se dedotto dall'assunzione.</summary>
    public string? CalledSource { get; init; }

    /// <summary>Minuti all'EOBT; negativi se è già passato.</summary>
    public int? MinutesToEobt { get; init; }
}

/// <summary>
/// La strippiera delle partenze: in ordine di EOBT, divise fra chi ha già chiamato e chi no.
///
/// "Ha chiamato" l'app non lo può sentire, quindi lo deduce: un traffico assunto da una
/// posizione in Aurora ha chiamato, perché DEL o GND lo assumono quando il pilota si fa vivo.
/// Il controllore può sempre correggere a mano, e la sua scelta vince sulla deduzione.
/// </summary>
public static class DepartureBoard
{
    /// <summary>Stati Whazzup di un volo già partito: la strip esce dalla strippiera.</summary>
    private static readonly HashSet<string> GoneStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Initial Climb", "En Route", "Approach", "Approaching", "Landed",
    };

    public static List<DepartureStrip> Build(
        IReadOnlyList<StandRequest> requests,
        IReadOnlyList<Assignment> assignments,
        IReadOnlyDictionary<string, TrafficStatus> traffic,
        IReadOnlyDictionary<string, bool> calledOverrides,
        DateTimeOffset now)
    {
        var standByKey = assignments.ToDictionary(a => a.Key, a => a.StandId, StringComparer.OrdinalIgnoreCase);
        var strips = new List<DepartureStrip>();

        foreach (var r in requests)
        {
            var dep = r.Outbound;
            if (dep is null) continue;

            traffic.TryGetValue(dep.Callsign, out var t);

            // Già in volo: la strip ha finito il suo lavoro.
            if (t is { OnGround: false, Altitude: > 500 }) continue;
            if (dep.TrackState is not null && GoneStates.Contains(dep.TrackState)) continue;

            var assumedBy = string.IsNullOrWhiteSpace(t?.AssumedBy) ? null : t!.AssumedBy;

            bool called;
            string? source;
            if (calledOverrides.TryGetValue(dep.Callsign, out var manual))
            {
                called = manual;
                source = "manuale";
            }
            else
            {
                called = assumedBy is not null;
                source = called ? "aurora" : null;
            }

            strips.Add(new DepartureStrip
            {
                Key = r.Key,
                Callsign = dep.Callsign,
                AircraftType = dep.AircraftType,
                Destination = dep.Arrival,
                Eobt = dep.Etd,
                Stand = r.ActualStand ?? (standByKey.TryGetValue(r.Key, out var s) ? s : null),
                Online = dep.IsOnline || t is not null,
                TrackState = dep.TrackState,
                AssumedBy = assumedBy,
                Called = called,
                CalledSource = source,
                MinutesToEobt = dep.Etd is { } e ? (int)Math.Round((e - now).TotalMinutes) : null,
            });
        }

        return strips
            .OrderBy(s => s.Eobt ?? DateTimeOffset.MaxValue)
            .ThenBy(s => s.Callsign, StringComparer.Ordinal)
            .ToList();
    }
}
