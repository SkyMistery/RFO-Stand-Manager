namespace RfoGateManager.Domain;

public sealed record AllocationResult
{
    public required IReadOnlyList<Assignment> Assignments { get; init; }
    public required IReadOnlyList<StandRequest> Requests { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public int Unassigned => Assignments.Count(a => a.StandId is null);
    public int Conflicts => Assignments.Count(a => a.Conflict);
}

/// <summary>
/// Assegna gli stand rispettando la timeline di occupazione. Greedy in ordine di ora di
/// arrivo, con punteggio sui candidati: deterministico e soprattutto spiegabile, perché
/// il controllore deve poter capire (e ribaltare) ogni scelta.
/// </summary>
public sealed class StandAllocator(IReadOnlyList<Stand> stands, AllocationOptions options)
{
    private readonly Dictionary<string, Stand> _byId =
        stands.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Relazione MARS resa simmetrica: se A blocca B, allora B blocca A.</summary>
    private readonly Dictionary<string, HashSet<string>> _blocks = BuildBlockMap(stands);

    /// <param name="previous">Assegnazioni del piano precedente, per non rimescolare senza motivo.</param>
    /// <param name="pinned">Stand imposti a mano dal controllore: intoccabili.</param>
    public AllocationResult Allocate(
        IReadOnlyList<StandRequest> requests,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? previous = null,
        IReadOnlyDictionary<string, string>? pinned = null)
    {
        previous ??= new Dictionary<string, string>();
        pinned ??= new Dictionary<string, string>();

        var occupancy = new Dictionary<string, List<Slot>>(StringComparer.OrdinalIgnoreCase);
        var results = new List<Assignment>();

        // Fase 1: quello che è già deciso. Pin manuali, stand reali letti da Aurora, prenotazioni.
        // Vanno posati per primi perché sono vincoli, non preferenze.
        var ordered = requests
            .OrderByDescending(r => pinned.ContainsKey(r.Key) || r.Locked)
            .ThenByDescending(r => r.ActualStand is not null)
            .ThenByDescending(r => r.BookedStand is not null)
            .ThenBy(r => r.From)
            .ToList();

        foreach (var req in ordered)
        {
            var forced = Pick(pinned, req.Key) ?? req.ActualStand ?? req.BookedStand;

            if (forced is not null)
            {
                results.Add(AssignForced(occupancy, req, forced, pinned));
                continue;
            }

            // Fase 2: scelta automatica fra i candidati ammissibili.
            var best = ChooseBest(occupancy, req, previous, out var reason);
            if (best is null)
            {
                results.Add(new Assignment
                {
                    Key = req.Key,
                    Callsign = req.Callsign,
                    StandId = null,
                    From = req.From,
                    To = req.To,
                    Conflict = true,
                    Reason = reason,
                });
                continue;
            }

            Occupy(occupancy, best.Id, req);
            results.Add(new Assignment
            {
                Key = req.Key,
                Callsign = req.Callsign,
                StandId = best.Id,
                From = req.From,
                To = req.To,
                Reason = reason,
            });
        }

        return new AllocationResult
        {
            Assignments = results.OrderBy(a => a.From).ThenBy(a => a.Callsign, StringComparer.Ordinal).ToList(),
            Requests = requests,
            GeneratedAt = now,
        };
    }

    private Assignment AssignForced(
        Dictionary<string, List<Slot>> occupancy,
        StandRequest req,
        string forced,
        IReadOnlyDictionary<string, string> pinned)
    {
        // Stand imposto ma sconosciuto nel .gts: lo segnaliamo invece di ignorarlo.
        if (!_byId.TryGetValue(forced, out var stand))
        {
            return new Assignment
            {
                Key = req.Key,
                Callsign = req.Callsign,
                StandId = forced,
                From = req.From,
                To = req.To,
                Conflict = true,
                Manual = Pick(pinned, req.Key) is not null,
                Reason = $"Stand '{forced}' non presente nel file stand: impossibile verificarlo.",
            };
        }

        var free = IsFree(occupancy, stand, req, out var clashKey);
        var fits = Fits(stand, req, out _);

        Occupy(occupancy, stand.Id, req);

        return new Assignment
        {
            Key = req.Key,
            Callsign = req.Callsign,
            StandId = stand.Id,
            From = req.From,
            To = req.To,
            Conflict = !free || !fits,
            Manual = Pick(pinned, req.Key) is not null,
            Reason = BuildForcedReason(req, pinned, free, fits, clashKey, stand),
        };
    }

    private Stand? ChooseBest(
        Dictionary<string, List<Slot>> occupancy,
        StandRequest req,
        IReadOnlyDictionary<string, string> previous,
        out string reason)
    {
        Stand? best = null;
        var bestScore = double.MaxValue;
        var bestWhy = "";

        var tooSmall = 0;
        var wrongUse = 0;
        var busy = 0;
        var blocked = 0;

        foreach (var stand in _byId.Values)
        {
            if (stand.Disabled) continue;

            if (!Fits(stand, req, out var why))
            {
                if (why == "size") tooSmall++;
                else wrongUse++;
                continue;
            }

            if (!IsFree(occupancy, stand, req, out var clash))
            {
                if (clash == MarsClash) blocked++;
                else busy++;
                continue;
            }

            var (score, explain) = Score(stand, req, previous);
            if (score < bestScore)
            {
                bestScore = score;
                best = stand;
                bestWhy = explain;
            }
        }

        reason = best is not null
            ? bestWhy
            : $"Nessuno stand disponibile per {req.AircraftType} (cat. {req.Size}) dalle {req.From:HH:mm} alle {req.To:HH:mm}Z — " +
              $"{tooSmall} troppo piccoli, {wrongUse} con uso incompatibile, {busy} occupati, {blocked} bloccati da uno stand MARS adiacente.";

        return best;
    }

    private (double Score, string Why) Score(
        Stand stand, StandRequest req, IReadOnlyDictionary<string, string> previous)
    {
        double score = 0;
        var why = new List<string>();

        if (req.AirlineCode is not null &&
            stand.Airlines.Contains(req.AirlineCode, StringComparer.OrdinalIgnoreCase))
        {
            score -= 1000;
            why.Add($"stand assegnato a {req.AirlineCode}");
        }

        if (Pick(previous, req.Key) is { } prev && prev.Equals(stand.Id, StringComparison.OrdinalIgnoreCase))
        {
            score -= 500;
            why.Add("confermato dal piano precedente");
        }

        // Non sprecare uno stand grande su un aereo piccolo.
        var waste = (int)stand.MaxSize - (int)req.Size;
        score += waste * 10;
        if (waste == 0) why.Add("misura esatta");

        if (options.PreferContactStands && req.Use == "pax" && stand.Contact)
        {
            score -= 20;
            why.Add("pontile");
        }

        if (req.Use == "ga" && stand.Contact) score += 30;

        // Bruciare uno stand MARS ne rende inagibili altri: usiamolo per ultimo.
        if (_blocks.TryGetValue(stand.Id, out var b) && b.Count > 0) score += 15;

        score += stand.Priority / 10.0;

        var text = why.Count > 0
            ? $"Stand {stand.Id}: {string.Join(", ", why)}."
            : $"Stand {stand.Id}: primo compatibile e libero nella finestra.";
        return (score, text);
    }

    private static bool Fits(Stand stand, StandRequest req, out string why)
    {
        if (req.Size > stand.MaxSize) { why = "size"; return false; }

        if (stand.Uses.Count > 0 && !stand.Uses.Contains(req.Use, StringComparer.OrdinalIgnoreCase))
        {
            why = "use";
            return false;
        }

        why = "";
        return true;
    }

    private const string MarsClash = " mars";

    private bool IsFree(
        Dictionary<string, List<Slot>> occupancy,
        Stand stand, StandRequest req, out string clashKey)
    {
        var buffer = TimeSpan.FromMinutes(options.BufferMinutes);
        clashKey = "";

        if (occupancy.TryGetValue(stand.Id, out var slots))
        {
            foreach (var s in slots)
            {
                if (s.Key == req.Key) continue;
                if (Overlaps(s.From, s.To, req.From, req.To, buffer)) { clashKey = s.Key; return false; }
            }
        }

        // Uno stand MARS adiacente occupato rende questo inagibile.
        if (_blocks.TryGetValue(stand.Id, out var neighbours))
        {
            foreach (var n in neighbours)
            {
                if (!occupancy.TryGetValue(n, out var ns)) continue;
                foreach (var s in ns)
                {
                    if (s.Key == req.Key) continue;
                    if (Overlaps(s.From, s.To, req.From, req.To, buffer)) { clashKey = MarsClash; return false; }
                }
            }
        }

        return true;
    }

    private static bool Overlaps(
        DateTimeOffset aFrom, DateTimeOffset aTo,
        DateTimeOffset bFrom, DateTimeOffset bTo,
        TimeSpan buffer)
        => aFrom - buffer < bTo && bFrom - buffer < aTo;

    private static void Occupy(
        Dictionary<string, List<Slot>> occupancy, string standId, StandRequest req)
    {
        if (!occupancy.TryGetValue(standId, out var list))
            occupancy[standId] = list = [];
        list.Add(new Slot(req.From, req.To, req.Key));
    }

    private static string BuildForcedReason(
        StandRequest req, IReadOnlyDictionary<string, string> pinned,
        bool free, bool fits, string clashKey, Stand stand)
    {
        var origin = Pick(pinned, req.Key) is not null ? "Imposto a mano"
                   : req.ActualStand is not null ? "Posizione reale letta da Aurora"
                   : "Da prenotazione";

        if (!fits)
            return $"{origin} su {stand.Id}, ma {req.AircraftType} (cat. {req.Size}) supera la categoria massima {stand.MaxSize} dello stand.";

        if (!free)
            return clashKey == MarsClash
                ? $"{origin} su {stand.Id}, ma uno stand MARS adiacente è occupato nella stessa finestra."
                : $"{origin} su {stand.Id}, ma si sovrappone a {clashKey}.";

        return $"{origin} su {stand.Id}.";
    }

    private static string? Pick(IReadOnlyDictionary<string, string> dict, string key)
        => dict.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static Dictionary<string, HashSet<string>> BuildBlockMap(IReadOnlyList<Stand> stands)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> For(string id)
        {
            if (!map.TryGetValue(id, out var set))
                map[id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return set;
        }

        foreach (var s in stands)
        {
            foreach (var other in s.Blocks)
            {
                if (other.Equals(s.Id, StringComparison.OrdinalIgnoreCase)) continue;
                For(s.Id).Add(other);
                For(other).Add(s.Id);
            }
        }

        return map;
    }

    private readonly record struct Slot(DateTimeOffset From, DateTimeOffset To, string Key);
}
