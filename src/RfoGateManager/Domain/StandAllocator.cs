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

        // Fase 1: quello che è già deciso, perché sono vincoli e non preferenze. Prima di tutto
        // chi è fisicamente su uno stand: è un fatto, e nessun piano lo sposta. Poi le scelte
        // a mano, poi le prenotazioni.
        var ordered = requests
            .OrderByDescending(r => r.ActualStand is not null)
            .ThenByDescending(r => pinned.ContainsKey(r.Key) || r.Locked)
            .ThenByDescending(r => r.BookedStand is not null)
            .ThenBy(r => r.From)
            .ToList();

        foreach (var req in ordered)
        {
            if (req.ActualStand is not null)
            {
                results.Add(AssignForced(occupancy, req, req.ActualStand, pinned, physical: true));
                continue;
            }

            var pin = Pick(pinned, req.Key);
            var decided = pin ?? req.BookedStand;

            if (decided is not null)
            {
                // Lo stand deciso è occupato da un aereo che è lì adesso: invece di far spostare
                // chi è già a terra, a questo ne diamo un altro.
                if (_byId.TryGetValue(decided, out var decidedStand) &&
                    PhysicalClash(occupancy, decidedStand, req) is { } occupant)
                {
                    results.Add(Reassign(occupancy, req, decided, occupant, wasPinned: pin is not null, previous));
                    continue;
                }

                results.Add(AssignForced(occupancy, req, decided, pinned, physical: false));
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
        IReadOnlyDictionary<string, string> pinned,
        bool physical)
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
                Unscheduled = req.Unscheduled,
                Reason = $"Stand '{forced}' non presente nel file stand: impossibile verificarlo.",
            };
        }

        var free = IsFree(occupancy, stand, req, out var clashKey);
        var fits = Fits(stand, req, out _);

        Occupy(occupancy, stand.Id, req, physical);

        return new Assignment
        {
            Key = req.Key,
            Callsign = req.Callsign,
            StandId = stand.Id,
            From = req.From,
            To = req.To,
            // Lo stand gia' deciso vince sulle misure: si assegna comunque e si segnala.
            Conflict = !free,
            Oversize = !fits,
            Manual = Pick(pinned, req.Key) is not null,
            Unscheduled = req.Unscheduled,
            Reason = BuildForcedReason(req, pinned, free, fits, clashKey, stand),
        };
    }

    /// <summary>
    /// Trova un altro stand per chi ha trovato il suo occupato. La scelta segue le regole
    /// normali, misure comprese: "lo stand deciso vince sulle misure" valeva per quello
    /// stand, non per il sostituto che scegliamo noi.
    /// </summary>
    private Assignment Reassign(
        Dictionary<string, List<Slot>> occupancy,
        StandRequest req,
        string lost,
        string occupant,
        bool wasPinned,
        IReadOnlyDictionary<string, string> previous)
    {
        var what = wasPinned ? "fissato" : "prenotato";
        var best = ChooseBest(occupancy, req, previous, out var why);

        if (best is null)
        {
            return new Assignment
            {
                Key = req.Key,
                Callsign = req.Callsign,
                StandId = null,
                From = req.From,
                To = req.To,
                Conflict = true,
                Reassigned = true,
                DisplacedFrom = lost,
                DisplacedBy = occupant,
                WasPinned = wasPinned,
                Reason = $"Stand {what} {lost} occupato da {occupant}, e nessun altro è libero. {why}",
            };
        }

        Occupy(occupancy, best.Id, req);

        var tail = wasPinned ? ", da ricomunicare al pilota. " : ". ";
        return new Assignment
        {
            Key = req.Key,
            Callsign = req.Callsign,
            StandId = best.Id,
            From = req.From,
            To = req.To,
            Reassigned = true,
            DisplacedFrom = lost,
            DisplacedBy = occupant,
            WasPinned = wasPinned,
            Reason = $"Stand {what} {lost} occupato da {occupant}: riassegnato a {best.Id}{tail}{why}",
        };
    }

    /// <summary>
    /// Chi occupa fisicamente lo stand (o uno stand MARS che lo blocca) nella finestra della
    /// richiesta. Le sole prenotazioni non contano: lì non c'è ancora nessuno da spostare.
    /// </summary>
    private string? PhysicalClash(Dictionary<string, List<Slot>> occupancy, Stand stand, StandRequest req)
    {
        var buffer = TimeSpan.FromMinutes(options.BufferMinutes);

        var ids = new List<string> { stand.Id };
        if (_blocks.TryGetValue(stand.Id, out var neighbours)) ids.AddRange(neighbours);

        foreach (var id in ids)
        {
            if (!occupancy.TryGetValue(id, out var slots)) continue;

            foreach (var s in slots)
            {
                if (!s.Physical || s.Key == req.Key) continue;
                if (!Overlaps(s.From, s.To, req.From, req.To, buffer)) continue;

                return id.Equals(stand.Id, StringComparison.OrdinalIgnoreCase)
                    ? s.Callsign
                    : $"{s.Callsign} (sullo stand MARS {id})";
            }
        }

        return null;
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

        // Non sprecare uno stand grande su un aereo piccolo. In metri se li conosciamo,
        // altrimenti a salti di categoria.
        if (stand.MaxWingspanM is { } maxSpan && req.WingspanM is { } span)
        {
            var slack = maxSpan - span;
            score += slack;
            if (slack <= 3) why.Add($"misura giusta ({span:0.#} m su {maxSpan:0.#} m)");
        }
        else
        {
            var waste = (int)stand.MaxSize - (int)req.Size;
            score += waste * 10;
            if (waste == 0) why.Add("categoria esatta");
        }

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

    /// <summary>
    /// Verifica l'ammissibilita'. Quando conosciamo sia i limiti dello stand (AIP) sia le
    /// misure del tipo, decidono quelle: la lettera di codice e' piu' grossolana e a Napoli
    /// diversi stand codice C hanno limiti sotto i 36 m, dove un A320 non entra.
    /// </summary>
    private static bool Fits(Stand stand, StandRequest req, out string why)
    {
        var measured = false;

        if (stand.MaxWingspanM is { } maxSpan && req.WingspanM is { } span)
        {
            measured = true;
            if (span > maxSpan) { why = "size"; return false; }
        }

        if (stand.MaxLengthM is { } maxLen && req.LengthM is { } len)
        {
            measured = true;
            if (len > maxLen) { why = "size"; return false; }
        }

        // Senza misure da confrontare resta la categoria.
        if (!measured && req.Size > stand.MaxSize) { why = "size"; return false; }

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
        Dictionary<string, List<Slot>> occupancy, string standId, StandRequest req, bool physical = false)
    {
        if (!occupancy.TryGetValue(standId, out var list))
            occupancy[standId] = list = [];
        list.Add(new Slot(req.From, req.To, req.Key, req.Callsign, physical));
    }

    private static string BuildForcedReason(
        StandRequest req, IReadOnlyDictionary<string, string> pinned,
        bool free, bool fits, string clashKey, Stand stand)
    {
        var origin = req.Unscheduled ? "Non programmato, fermo"
                   : req.ActualStand is not null ? "A terra, fermo"
                   : Pick(pinned, req.Key) is not null ? "Imposto a mano"
                   : "Da prenotazione";

        var parts = new List<string> { $"{origin} su {stand.Id}" };

        if (!fits)
            parts.Add($"ATTENZIONE: {req.AircraftType} non ci sta ({Oversize(stand, req)}). " +
                      "Assegnato lo stesso perché lo stand era già deciso: verificare gli stand adiacenti");

        if (!free)
            parts.Add(clashKey == MarsClash
                ? "uno stand MARS adiacente è occupato nella stessa finestra"
                : $"si sovrappone a {clashKey}");

        return string.Join(" — ", parts) + ".";
    }

    /// <summary>Dice quale limite e' stato sforato, per scriverlo nel motivo.</summary>
    private static string Oversize(Stand stand, StandRequest req)
    {
        if (stand.MaxWingspanM is { } maxSpan && req.WingspanM is { } span && span > maxSpan)
            return $"apertura alare {span:0.#} m contro un massimo di {maxSpan:0.#} m";

        if (stand.MaxLengthM is { } maxLen && req.LengthM is { } len && len > maxLen)
            return $"lunghezza {len:0.#} m contro un massimo di {maxLen:0.#} m";

        return $"categoria {req.Size} contro un massimo di {stand.MaxSize}";
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

    /// <param name="Physical">L'aereo è su questo stand adesso, non solo in programma.</param>
    private readonly record struct Slot(DateTimeOffset From, DateTimeOffset To, string Key, string Callsign, bool Physical);
}
