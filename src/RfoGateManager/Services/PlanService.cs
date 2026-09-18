using RfoGateManager.Aurora;
using RfoGateManager.Data;
using RfoGateManager.Domain;
using RfoGateManager.Sync;

namespace RfoGateManager.Services;

/// <summary>Uno stand che cambia fra un ricalcolo e l'altro. Il controllore deve saperlo.</summary>
public sealed record StandChange(
    DateTimeOffset At,
    string Key,
    string Callsign,
    string? FromStand,
    string? ToStand,
    string Reason);

public sealed record PlanSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<Stand> Stands { get; init; } = [];
    public IReadOnlyList<StandRequest> Requests { get; init; } = [];
    public IReadOnlyList<Assignment> Assignments { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<PhysicalOccupant> Occupants { get; init; } = [];
    public int Unassigned { get; init; }
    public int Conflicts { get; init; }
}

/// <summary>
/// Tiene insieme le sorgenti (prenotazioni, Whazzup, Aurora), ricalcola il piano stand e
/// spinge le assegnazioni verso Aurora. È l'unico punto in cui si scrive lo stato.
/// </summary>
public sealed class PlanService
{
    private readonly AppConfig _config;
    private readonly BookingClient _booking;
    private readonly WhazzupClient _whazzup;
    private readonly AuroraClient _aurora;
    private readonly SharedStateStore _shared;
    private readonly ILogger<PlanService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private StandCatalogResult _catalog = new();
    private List<FlightLeg> _bookingLegs = [];
    private List<FlightLeg> _manualLegs = [];
    private PlanSnapshot _snapshot = new();
    private readonly List<StandChange> _changes = [];

    /// <summary>Quello che Aurora ha detto di ogni traffico in raggio all'ultimo giro.</summary>
    private IReadOnlyDictionary<string, TrafficStatus> _traffic =
        new Dictionary<string, TrafficStatus>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Entro questo raggio dall'ARP un aereo fermo conta come parcheggiato qui.</summary>
    private const double AirportRadiusMeters = 3000;

    /// <summary>Distanza massima fra un aereo e il punto dello stand per dire che è su quello stand.</summary>
    private const double StandMatchMeters = 40;

    public PlanService(
        AppConfig config, BookingClient booking, WhazzupClient whazzup,
        AuroraClient aurora, SharedStateStore shared, ILogger<PlanService> log)
    {
        _config = config;
        _booking = booking;
        _whazzup = whazzup;
        _aurora = aurora;
        _shared = shared;
        _log = log;
    }

    public AllocationOptions Options { get; set; } = new();
    public PlanSnapshot Snapshot => _snapshot;
    public StandCatalogResult Catalog => _catalog;
    public BookingFetchResult? LastBookingResult { get; private set; }

    /// <summary>Gli ultimi cambi di stand, dal più recente.</summary>
    public IReadOnlyList<StandChange> Changes
    {
        get { lock (_changes) return _changes.AsEnumerable().Reverse().ToList(); }
    }

    public string DataDirectory => AppPaths.Data;

    public StandCatalogResult ReloadStands()
    {
        _catalog = StandCatalog.Load(DataDirectory, _config.Airport);
        _log.LogInformation("Stand caricati: {Count} da {Path}", _catalog.Stands.Count, _catalog.GtsPath ?? "(nessun file)");
        return _catalog;
    }

    public async Task<BookingFetchResult> RefreshBookingAsync(CancellationToken ct = default)
    {
        var result = await _booking.FetchAsync(ct);
        LastBookingResult = result;

        if (result.Success)
        {
            _bookingLegs = result.Legs;
            _log.LogInformation("Prenotazioni caricate: {Count} tratte", result.Legs.Count);
        }

        return result;
    }

    /// <summary>Carica le prenotazioni da un JSON già scaricato, per lavorare senza rete.</summary>
    public BookingFetchResult LoadBookingFromJson(string json)
    {
        var result = _booking.Parse(json);
        LastBookingResult = result;
        if (result.Success) _bookingLegs = result.Legs;
        return result;
    }

    public void AddManualLeg(FlightLeg leg)
    {
        _manualLegs.RemoveAll(l => l.Callsign.Equals(leg.Callsign, StringComparison.OrdinalIgnoreCase)
                                   && l.Kind == leg.Kind);
        _manualLegs.Add(leg with { Source = "manual" });
    }

    public void RemoveManualLeg(string callsign) =>
        _manualLegs.RemoveAll(l => l.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ricalcola il piano intero. Idempotente: si può chiamare quanto si vuole.</summary>
    public async Task<PlanSnapshot> RecomputeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var warnings = new List<string>(_catalog.Warnings);

            if (_catalog.Stands.Count == 0) ReloadStands();

            var live = await _whazzup.GetLegsForAirportAsync(_config.Airport, now, ct);
            if (_whazzup.LastError is { } we) warnings.Add($"Whazzup: {we}");

            var legs = MergeLegs(_bookingLegs, live, _manualLegs);

            var occupants = await DetectOccupantsAsync(ct);

            var requests = Occupancy.Apply(
                RotationBuilder.Build(legs, _config.Airport, Options, now), occupants, now, Options);

            var doc = _shared.Shared ? await _shared.PullAsync(ct) : _shared.Current;

            var stands = doc.ClosedStands.Count == 0
                ? _catalog.Stands
                : _catalog.Stands
                    .Select(s => doc.ClosedStands.Contains(s.Id, StringComparer.OrdinalIgnoreCase)
                        ? s with { Disabled = true }
                        : s)
                    .ToList();

            // Il piano appena calcolato è l'ancora del prossimo: senza, gli ETA che cambiano a
            // ogni giro di Whazzup farebbero ballare le scelte automatiche da uno stand all'altro.
            var previous = new Dictionary<string, string>(doc.Plan, StringComparer.OrdinalIgnoreCase);
            foreach (var a in _snapshot.Assignments)
                if (a.StandId is not null) previous[a.Key] = a.StandId;

            var allocator = new StandAllocator(stands, Options);
            var result = allocator.Allocate(requests, now, previous: previous, pinned: doc.Pins);

            RecordChanges(_snapshot, result.Assignments, now);

            _snapshot = new PlanSnapshot
            {
                GeneratedAt = now,
                Stands = stands,
                Requests = result.Requests,
                Assignments = result.Assignments,
                Warnings = warnings,
                Occupants = occupants,
                Unassigned = result.Unassigned,
                Conflicts = result.Conflicts,
            };

            return _snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Pubblica il piano corrente sullo stato condiviso, così tutti vedono gli stessi stand.</summary>
    public async Task PublishPlanAsync(CancellationToken ct = default)
    {
        var map = _snapshot.Assignments
            .Where(a => a.StandId is not null)
            .ToDictionary(a => a.Key, a => a.StandId!, StringComparer.OrdinalIgnoreCase);

        await _shared.MutateAsync(d => d.Plan = map, ct);
    }

    public async Task PinAsync(string key, string? standId, CancellationToken ct = default)
    {
        await _shared.MutateAsync(d =>
        {
            if (string.IsNullOrWhiteSpace(standId)) d.Pins.Remove(key);
            else d.Pins[key] = standId.Trim();
        }, ct);

        await RecomputeAsync(ct);
    }

    public async Task SetStandClosedAsync(string standId, bool closed, CancellationToken ct = default)
    {
        await _shared.MutateAsync(d =>
        {
            d.ClosedStands.RemoveAll(s => s.Equals(standId, StringComparison.OrdinalIgnoreCase));
            if (closed) d.ClosedStands.Add(standId);
        }, ct);

        await RecomputeAsync(ct);
    }

    /// <summary>Manda lo stand ad Aurora con <c>#LBGTE</c> e, se richiesto, lo comunica al pilota.</summary>
    public async Task<string> PushToAuroraAsync(
        string callsign, string stand, string? key, bool alsoPrivateMessage, CancellationToken ct = default)
    {
        await _aurora.AssignGateAsync(callsign, stand, ct);

        if (alsoPrivateMessage)
        {
            try
            {
                await NotifyPilotAsync(callsign, stand, key, ct);
            }
            catch (Exception ex)
            {
                return $"Stand {stand} assegnato a {callsign}, ma il messaggio privato non è partito: {ex.Message}";
            }

            return $"Stand {stand} assegnato a {callsign} e comunicato al pilota.";
        }

        return $"Stand {stand} assegnato a {callsign}.";
    }

    /// <summary>Il testo che riceverà il pilota, dal modello in configurazione.</summary>
    public string PilotMessage(string callsign, string stand) => _config.PilotMessageTemplate
        .Replace("{callsign}", callsign, StringComparison.OrdinalIgnoreCase)
        .Replace("{stand}", stand, StringComparison.OrdinalIgnoreCase)
        .Replace("{airport}", _config.Airport, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Manda al pilota il PM con lo stand da aspettarsi e se lo segna nello stato condiviso:
    /// così tutte le postazioni sanno che è stato avvisato, e se lo stand poi cambia la riga
    /// dice "da ricomunicare".
    /// </summary>
    public async Task<string> NotifyPilotAsync(string callsign, string stand, string? key, CancellationToken ct = default)
    {
        var text = PilotMessage(callsign, stand);
        await _aurora.SendPrivateMessageAsync(callsign, text, ct);

        if (!string.IsNullOrWhiteSpace(key))
            await _shared.MutateAsync(d => d.Notified[key] = stand, ct);

        return text;
    }

    /// <summary>
    /// Gli stand possibili per un volo del piano, dal più comodo. Null se il volo non c'è.
    /// </summary>
    public (StandRequest Request, List<StandOption> Options)? Suggest(string key)
    {
        var snap = _snapshot;
        var req = snap.Requests.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (req is null) return null;

        var allocator = new StandAllocator(snap.Stands, Options);
        return (req, allocator.Suggest(req, snap.Requests, snap.Assignments));
    }

    /// <summary>La strippiera partenze, calcolata sull'ultimo piano.</summary>
    public List<DepartureStrip> Departures() => DepartureBoard.Build(
        _snapshot.Requests, _snapshot.Assignments, _traffic, _shared.Current.Called, DateTimeOffset.UtcNow);

    /// <summary>
    /// Segna a mano se una partenza ha chiamato. <c>null</c> toglie la scelta manuale e torna
    /// alla deduzione da Aurora.
    /// </summary>
    public Task SetCalledAsync(string callsign, bool? called, CancellationToken ct = default) =>
        _shared.MutateAsync(d =>
        {
            if (called is null) d.Called.Remove(callsign);
            else d.Called[callsign] = called.Value;
        }, ct);

    /// <summary>
    /// Chi è fermo su quale stand, adesso. Prima Aurora: il campo 17 di #TRPOS è lo stand
    /// calcolato da Aurora stesso sul suo file dei gate, ed è la fonte migliore. Poi Whazzup,
    /// per gli aerei che Aurora non vede o quando Aurora non c'è: dalla posizione ricaviamo lo
    /// stand più vicino con le coordinate del .gts.
    ///
    /// Conta solo chi è fermo. Il campo "stand assegnato" non conta: dice dove andrà, non dove
    /// è, e usarlo farebbe occupare lo stand a un aereo che sta ancora rullando.
    /// </summary>
    private async Task<List<PhysicalOccupant>> DetectOccupantsAsync(CancellationToken ct)
    {
        var found = new Dictionary<string, PhysicalOccupant>(StringComparer.OrdinalIgnoreCase);
        var known = _catalog.Stands.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var traffic = new Dictionary<string, TrafficStatus>(StringComparer.OrdinalIgnoreCase);

        if (_aurora.IsConnected)
        {
            try
            {
                foreach (var cs in await _aurora.GetTrafficInRangeAsync(ct))
                {
                    try
                    {
                        var pos = await _aurora.GetTrafficPositionAsync(cs, ct);

                        // Lo stato di ogni traffico serve anche alla strippiera (chi l'ha assunto,
                        // se è ancora a terra), non solo al piazzale.
                        traffic[cs] = new TrafficStatus(cs.ToUpperInvariant(), pos.OnGround,
                            pos.GroundSpeed, pos.Altitude, pos.AssumedStation);

                        if (!pos.OnGround || pos.GroundSpeed > 3) continue;
                        if (string.IsNullOrWhiteSpace(pos.CurrentGate) || !known.Contains(pos.CurrentGate)) continue;

                        // Lo stand "12" esiste anche altrove: conta solo se l'aereo è qui.
                        if (Occupancy.DistanceMeters(_config.AirportLat, _config.AirportLon,
                                pos.Latitude, pos.Longitude) > AirportRadiusMeters) continue;

                        found[cs] = new PhysicalOccupant(cs.ToUpperInvariant(), pos.CurrentGate, "aurora");
                    }
                    catch (AuroraException ex)
                    {
                        _log.LogDebug("Posizione di {Callsign} non disponibile: {Error}", cs, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug("Lettura del piazzale da Aurora saltata: {Error}", ex.Message);
            }
        }

        var withCoords = _catalog.Stands.Where(s => s.Lat != 0 || s.Lon != 0).ToList();
        if (withCoords.Count > 0)
        {
            try
            {
                var parked = await _whazzup.GetParkedNearAsync(
                    _config.AirportLat, _config.AirportLon, AirportRadiusMeters, ct);

                foreach (var g in parked.Where(g => !found.ContainsKey(g.Callsign)))
                {
                    var stand = Occupancy.NearestStand(g.Lat, g.Lon, withCoords, StandMatchMeters);
                    if (stand is not null)
                        found[g.Callsign] = new PhysicalOccupant(g.Callsign, stand, "whazzup", g.AircraftType);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug("Lettura del piazzale da Whazzup saltata: {Error}", ex.Message);
            }
        }

        _traffic = traffic;
        return found.Values.ToList();
    }

    /// <summary>
    /// Confronta il piano nuovo col precedente e registra chi ha cambiato stand. Il primo
    /// calcolo non produce cambi: non c'è niente con cui confrontarlo.
    /// </summary>
    private void RecordChanges(PlanSnapshot before, IReadOnlyList<Assignment> after, DateTimeOffset now)
    {
        if (before.Assignments.Count == 0) return;

        var old = before.Assignments.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);

        lock (_changes)
        {
            foreach (var a in after)
            {
                if (!old.TryGetValue(a.Key, out var prev)) continue;
                if (string.Equals(prev.StandId, a.StandId, StringComparison.OrdinalIgnoreCase)) continue;

                _changes.Add(new StandChange(now, a.Key, a.Callsign, prev.StandId, a.StandId, a.Reason));
                _log.LogInformation("Stand cambiato: {Callsign} {From} -> {To}", a.Callsign,
                    prev.StandId ?? "-", a.StandId ?? "-");
            }

            if (_changes.Count > 200) _changes.RemoveRange(0, _changes.Count - 200);
        }
    }

    /// <summary>
    /// Fonde le sorgenti per callsign. Whazzup vince sugli orari (il volo è online, sappiamo
    /// dov'è davvero); il booking conserva lo stand prenotato e i voli non ancora collegati.
    /// </summary>
    private static List<FlightLeg> MergeLegs(
        List<FlightLeg> booking, List<FlightLeg> live, List<FlightLeg> manual)
    {
        var merged = new Dictionary<(string Callsign, LegKind Kind), FlightLeg>();

        void Put(FlightLeg leg, bool overwriteTimes)
        {
            var key = (leg.Callsign.ToUpperInvariant(), leg.Kind);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = leg;
                return;
            }

            merged[key] = existing with
            {
                Eta = overwriteTimes ? leg.Eta ?? existing.Eta : existing.Eta ?? leg.Eta,
                Etd = overwriteTimes ? leg.Etd ?? existing.Etd : existing.Etd ?? leg.Etd,
                AircraftType = !string.IsNullOrWhiteSpace(leg.AircraftType) ? leg.AircraftType : existing.AircraftType,
                Registration = leg.Registration ?? existing.Registration,
                BookedStand = existing.BookedStand ?? leg.BookedStand,
                IsOnline = existing.IsOnline || leg.IsOnline,
                TrackState = leg.TrackState ?? existing.TrackState,
                DistanceToArrivalNm = leg.DistanceToArrivalNm ?? existing.DistanceToArrivalNm,
                Vid = leg.Vid ?? existing.Vid,
                Source = existing.Source == leg.Source ? existing.Source : $"{existing.Source}+{leg.Source}",
            };
        }

        foreach (var l in booking) Put(l, overwriteTimes: false);
        foreach (var l in live) Put(l, overwriteTimes: true);
        foreach (var l in manual) Put(l, overwriteTimes: true);

        return merged.Values.ToList();
    }
}
