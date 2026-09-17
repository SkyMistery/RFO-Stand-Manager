using RfoGateManager.Aurora;
using RfoGateManager.Data;
using RfoGateManager.Domain;
using RfoGateManager.Sync;

namespace RfoGateManager.Services;

public sealed record PlanSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<Stand> Stands { get; init; } = [];
    public IReadOnlyList<StandRequest> Requests { get; init; } = [];
    public IReadOnlyList<Assignment> Assignments { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
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
    private readonly Dictionary<string, string> _actualStands = new(StringComparer.OrdinalIgnoreCase);
    private PlanSnapshot _snapshot = new();

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

            await EnrichWithAuroraAsync(legs, ct);

            var requests = RotationBuilder.Build(legs, _config.Airport, Options, now)
                .Select(r => _actualStands.TryGetValue(r.Callsign, out var actual)
                    ? r with { ActualStand = actual }
                    : r)
                .ToList();

            var doc = _shared.Shared ? await _shared.PullAsync(ct) : _shared.Current;

            var stands = doc.ClosedStands.Count == 0
                ? _catalog.Stands
                : _catalog.Stands
                    .Select(s => doc.ClosedStands.Contains(s.Id, StringComparer.OrdinalIgnoreCase)
                        ? s with { Disabled = true }
                        : s)
                    .ToList();

            var allocator = new StandAllocator(stands, Options);
            var result = allocator.Allocate(requests, now, previous: doc.Plan, pinned: doc.Pins);

            _snapshot = new PlanSnapshot
            {
                GeneratedAt = now,
                Stands = stands,
                Requests = result.Requests,
                Assignments = result.Assignments,
                Warnings = warnings,
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
    public async Task<string> PushToAuroraAsync(string callsign, string stand, bool alsoPrivateMessage, CancellationToken ct = default)
    {
        await _aurora.AssignGateAsync(callsign, stand, ct);

        if (alsoPrivateMessage)
        {
            try
            {
                await _aurora.SendPrivateMessageAsync(callsign, $"Stand assegnato: {stand}. Buon volo!", ct);
            }
            catch (Exception ex)
            {
                return $"Stand {stand} assegnato a {callsign}, ma il messaggio privato non è partito: {ex.Message}";
            }
        }

        return $"Stand {stand} assegnato a {callsign}.";
    }

    /// <summary>
    /// Chiede ad Aurora dove si trovano davvero gli aerei in raggio radar. Il campo 17 di
    /// #TRPOS è la verità sul piazzale: un aereo già parcheggiato non va spostato.
    /// </summary>
    private async Task EnrichWithAuroraAsync(List<FlightLeg> legs, CancellationToken ct)
    {
        if (!_aurora.IsConnected) return;

        try
        {
            var inRange = await _aurora.GetTrafficInRangeAsync(ct);
            var interesting = legs.Select(l => l.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var cs in inRange.Where(interesting.Contains))
            {
                try
                {
                    var pos = await _aurora.GetTrafficPositionAsync(cs, ct);
                    var stand = !string.IsNullOrWhiteSpace(pos.CurrentGate) ? pos.CurrentGate
                              : !string.IsNullOrWhiteSpace(pos.AssignedGate) ? pos.AssignedGate
                              : null;

                    if (stand is not null && pos.OnGround) _actualStands[cs] = stand;
                    else _actualStands.Remove(cs);
                }
                catch (AuroraException ex)
                {
                    _log.LogDebug("Posizione di {Callsign} non disponibile: {Error}", cs, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Arricchimento da Aurora saltato: {Error}", ex.Message);
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
