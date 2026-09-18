using System.Text.Json;
using System.Text.Json.Serialization;
using RfoGateManager.Domain;

namespace RfoGateManager.Data;

/// <summary>
/// Whazzup v2 di IVAO: pubblico, senza autenticazione, aggiornato ogni ~15 secondi.
/// Rispetto a <c>#TR</c> di Aurora vede tutti i voli del mondo, non solo quelli nel raggio
/// radar: è questo che permette di pianificare gli stand con ore di anticipo.
/// </summary>
public sealed class WhazzupClient(HttpClient http, ILogger<WhazzupClient> log)
{
    public const string Url = "https://api.ivao.aero/v2/tracker/whazzup";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DateTimeOffset? LastFetch { get; private set; }
    public string? LastError { get; private set; }
    public int LastPilotCount { get; private set; }

    private WhazzupSnapshot? _cache;

    /// <summary>Scarica lo snapshot, con una cache corta per non martellare l'API.</summary>
    public async Task<WhazzupSnapshot?> GetAsync(CancellationToken ct = default)
    {
        if (_cache is not null && LastFetch is { } t && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(12))
            return _cache;

        try
        {
            using var res = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var snap = await JsonSerializer.DeserializeAsync<WhazzupSnapshot>(stream, JsonOptions, ct);

            if (snap is not null)
            {
                _cache = snap;
                LastFetch = DateTimeOffset.UtcNow;
                LastPilotCount = snap.Clients?.Pilots?.Count ?? 0;
                LastError = null;
            }

            return _cache;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.LogWarning("Whazzup non raggiungibile: {Error}", ex.Message);
            return _cache;
        }
    }

    /// <summary>Tutti i voli online da o per l'aeroporto, già convertiti in tratte con orari stimati.</summary>
    public async Task<List<FlightLeg>> GetLegsForAirportAsync(
        string icao, DateTimeOffset now, CancellationToken ct = default)
    {
        var snap = await GetAsync(ct);
        var legs = new List<FlightLeg>();
        if (snap?.Clients?.Pilots is null) return legs;

        foreach (var p in snap.Clients.Pilots)
        {
            var fp = p.FlightPlan;
            if (fp is null || string.IsNullOrWhiteSpace(p.Callsign)) continue;

            var isArrival = string.Equals(fp.ArrivalId, icao, StringComparison.OrdinalIgnoreCase);
            var isDeparture = string.Equals(fp.DepartureId, icao, StringComparison.OrdinalIgnoreCase);
            if (!isArrival && !isDeparture) continue;

            var etd = EstimateOffBlocks(p, now);
            var eta = isArrival ? EstimateOnBlocks(p, now, etd) : null;

            legs.Add(new FlightLeg
            {
                Callsign = p.Callsign.Trim().ToUpperInvariant(),
                Registration = ExtractRegistration(fp.Remarks),
                AircraftType = fp.AircraftId ?? "",
                Departure = (fp.DepartureId ?? "").ToUpperInvariant(),
                Arrival = (fp.ArrivalId ?? "").ToUpperInvariant(),
                Eta = eta,
                Etd = isDeparture ? etd : null,
                Kind = isArrival && isDeparture ? LegKind.Turnaround
                     : isArrival ? LegKind.Arrival
                     : LegKind.Departure,
                Source = "whazzup",
                Vid = p.UserId,
                IsOnline = true,
                TrackState = p.LastTrack?.State,
                DistanceToArrivalNm = isArrival ? p.LastTrack?.ArrivalDistance : null,
            });
        }

        return legs;
    }

    /// <summary>
    /// Aerei fermi a terra entro un raggio dall'aeroporto, con o senza piano di volo. Chi si
    /// piazza su uno stand senza prenotazione spesso non ha ancora depositato il piano: se
    /// guardassimo solo i voli con destinazione o partenza qui, non lo vedremmo.
    /// </summary>
    public async Task<List<GroundTraffic>> GetParkedNearAsync(
        double lat, double lon, double radiusMeters, CancellationToken ct = default)
    {
        var snap = await GetAsync(ct);
        var list = new List<GroundTraffic>();
        if (snap?.Clients?.Pilots is null) return list;

        foreach (var p in snap.Clients.Pilots)
        {
            var t = p.LastTrack;
            if (t is null || string.IsNullOrWhiteSpace(p.Callsign)) continue;
            if (!t.OnGround || t.GroundSpeed is > 2) continue;
            if (t.Latitude is not { } plat || t.Longitude is not { } plon) continue;
            if (Domain.Occupancy.DistanceMeters(lat, lon, plat, plon) > radiusMeters) continue;

            list.Add(new GroundTraffic(
                p.Callsign.Trim().ToUpperInvariant(), plat, plon, p.FlightPlan?.AircraftId));
        }

        return list;
    }

    /// <summary>
    /// On-blocks stimato. In volo: distanza residua diviso ground speed, più il rullaggio.
    /// A terra prima del decollo: orario di partenza più tempo di volo previsto.
    /// </summary>
    private static DateTimeOffset? EstimateOnBlocks(WzPilot p, DateTimeOffset now, DateTimeOffset? etd)
    {
        var track = p.LastTrack;

        if (track is { OnGround: false, GroundSpeed: > 60, ArrivalDistance: > 0 })
        {
            var hours = track.ArrivalDistance.Value / track.GroundSpeed.Value;
            // Le ultime miglia si fanno più lenti, più il rullaggio fino al piazzale.
            return now.AddHours(hours).AddMinutes(6);
        }

        if (etd is { } off && p.FlightPlan?.Eet is > 0)
            return off.AddSeconds(p.FlightPlan.Eet.Value).AddMinutes(6);

        if (track is { OnGround: true } && p.FlightPlan?.Eet is > 0)
            return now.AddSeconds(p.FlightPlan.Eet.Value);

        return null;
    }

    /// <summary>
    /// Off-blocks. Whazzup dà gli orari come secondi dalla mezzanotte UTC, quindi il giorno
    /// va ricostruito dalla data del piano di volo tenendo conto del cambio di data.
    /// </summary>
    private static DateTimeOffset? EstimateOffBlocks(WzPilot p, DateTimeOffset now)
    {
        var fp = p.FlightPlan;
        if (fp is null) return null;

        var seconds = fp.ActualDepartureTime ?? fp.DepartureTime;
        if (seconds is null) return null;

        var reference = fp.CreatedAt ?? p.CreatedAt ?? now;
        var candidate = new DateTimeOffset(reference.UtcDateTime.Date, TimeSpan.Zero).AddSeconds(seconds.Value);

        // Piano creato a tarda sera per un volo dopo la mezzanotte, o viceversa.
        if (candidate - reference > TimeSpan.FromHours(12)) candidate = candidate.AddDays(-1);
        else if (reference - candidate > TimeSpan.FromHours(12)) candidate = candidate.AddDays(1);

        return candidate;
    }

    /// <summary>Le marche stanno nei remarks come <c>REG/I-ABCD</c>: servono per legare le rotazioni.</summary>
    public static string? ExtractRegistration(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks)) return null;

        const string tag = "REG/";
        var i = remarks.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        var start = i + tag.Length;
        var end = start;
        while (end < remarks.Length && !char.IsWhiteSpace(remarks[end])) end++;

        var reg = remarks[start..end].Trim();
        return reg.Length is >= 3 and <= 10 ? reg.ToUpperInvariant() : null;
    }
}

public sealed record GroundTraffic(string Callsign, double Lat, double Lon, string? AircraftType);

// --- Modello Whazzup (solo i campi che usiamo) ------------------------------------

public sealed record WhazzupSnapshot
{
    public DateTimeOffset? UpdatedAt { get; init; }
    public WzClients? Clients { get; init; }
}

public sealed record WzClients
{
    public List<WzPilot>? Pilots { get; init; }
    public List<WzAtc>? Atcs { get; init; }
}

public sealed record WzPilot
{
    public int Id { get; init; }
    public int UserId { get; init; }
    public string? Callsign { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public WzTrack? LastTrack { get; init; }
    public WzFlightPlan? FlightPlan { get; init; }
}

public sealed record WzAtc
{
    public string? Callsign { get; init; }
    public int UserId { get; init; }
}

public sealed record WzTrack
{
    public int? Altitude { get; init; }
    public double? ArrivalDistance { get; init; }
    public double? DepartureDistance { get; init; }
    public double? GroundSpeed { get; init; }
    public int? Heading { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public bool OnGround { get; init; }

    /// <summary>Boarding, Departing, Initial Climb, En Route, Approaching, Landed, On Blocks.</summary>
    public string? State { get; init; }

    public DateTimeOffset? Timestamp { get; init; }
}

public sealed record WzFlightPlan
{
    public string? AircraftId { get; init; }
    public string? DepartureId { get; init; }
    public string? ArrivalId { get; init; }
    public string? AlternativeId { get; init; }
    public string? Route { get; init; }
    public string? Remarks { get; init; }
    public string? Level { get; init; }

    /// <summary>Tempo di volo previsto, in secondi.</summary>
    public int? Eet { get; init; }

    /// <summary>Orario di partenza, secondi dalla mezzanotte UTC.</summary>
    public int? DepartureTime { get; init; }

    public int? ActualDepartureTime { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("aircraft")]
    public WzAircraft? Aircraft { get; init; }
}

public sealed record WzAircraft
{
    public string? IcaoCode { get; init; }
    public string? WakeTurbulence { get; init; }
    public string? Description { get; init; }
}
