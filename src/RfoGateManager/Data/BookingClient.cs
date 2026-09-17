using System.Globalization;
using System.Text.Json;
using RfoGateManager.Domain;

namespace RfoGateManager.Data;

public sealed record BookingFetchResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int HttpStatus { get; init; }
    public string RequestUrl { get; init; } = "";
    public List<FlightLeg> Legs { get; init; } = [];

    /// <summary>Primo elemento grezzo della risposta: serve per capire i nomi dei campi senza indovinare.</summary>
    public string? SampleJson { get; init; }
}

/// <summary>
/// Legge le prenotazioni dell'evento. Lo schema esatto della risposta non è documentato,
/// quindi il mapping cerca i nomi di campo più probabili e conserva un campione grezzo:
/// appena si vede una risposta vera, adattare <see cref="FieldNames"/> è questione di minuti.
/// </summary>
public sealed class BookingClient(HttpClient http, AppConfig config, ILogger<BookingClient> log)
{
    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    /// <summary>Nomi accettati per ogni campo, in ordine di preferenza.</summary>
    public static class FieldNames
    {
        public static readonly string[] Callsign = ["callsign", "call_sign", "flightCallsign", "cs"];
        public static readonly string[] Departure = ["origin_icao", "departure", "departureId", "departureIcao", "dep", "origin", "from", "adep"];
        public static readonly string[] Arrival = ["destination_icao", "arrival", "arrivalId", "arrivalIcao", "arr", "destination", "to", "ades"];
        public static readonly string[] Aircraft = ["aircraft_icao", "aircraft", "aircraftId", "aircraftType", "actype", "acType", "equipment", "type"];
        public static readonly string[] Registration = ["registration", "reg", "tailNumber", "aircraftRegistration"];
        public static readonly string[] Eobt = ["eobt", "std", "departureTime", "depTime", "timeDeparture", "slot", "slotTime", "time"];
        public static readonly string[] Eta = ["eat", "eta", "sta", "arrivalTime", "arrTime", "timeArrival"];
        public static readonly string[] Stand = ["gate", "stand", "parking", "standId", "gateId", "bay"];
        public static readonly string[] Vid = ["booked_by", "vid", "userId", "user_id", "memberId"];
        public static readonly string[] Date = ["date", "eventDate", "day"];

        /// <summary>Valori che significano "stand non ancora deciso".</summary>
        public static readonly string[] NoStand = ["TBD", "TBA", "N/A", "-", "NIL", "NONE"];

        /// <summary>Chiavi sotto cui può stare l'array dei voli se la risposta è un oggetto.</summary>
        public static readonly string[] Collections = ["flights", "data", "result", "results", "items", "bookings", "rows"];
    }

    public async Task<BookingFetchResult> FetchAsync(CancellationToken ct = default)
    {
        // La rotta non prende la data: l'endpoint restituisce le prenotazioni dell'evento
        // in corso. Con la data in fondo Slim risponde 404.
        var url = $"{config.BookingBaseUrl.TrimEnd('/')}/flights";

        if (!config.HasBookingKey)
        {
            return new BookingFetchResult
            {
                Success = false,
                RequestUrl = url,
                Error = "Chiave non configurata. Inseriscila in secrets/booking.json (campo bookingXKey).",
            };
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-api-key", config.BookingXKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                return new BookingFetchResult
                {
                    Success = false,
                    HttpStatus = (int)res.StatusCode,
                    RequestUrl = url,
                    Error = $"HTTP {(int)res.StatusCode}: {Truncate(body, 400)}",
                };
            }

            // L'endpoint risponde 200 anche sugli errori PHP: se non è JSON, è un errore.
            if (!LooksLikeJson(body))
            {
                return new BookingFetchResult
                {
                    Success = false,
                    HttpStatus = (int)res.StatusCode,
                    RequestUrl = url,
                    Error = $"La risposta non è JSON: {Truncate(body, 400)}",
                    SampleJson = Truncate(body, 2000),
                };
            }

            return Parse(body, url);
        }
        catch (Exception ex)
        {
            log.LogWarning("Booking non raggiungibile: {Error}", ex.Message);
            return new BookingFetchResult { Success = false, RequestUrl = url, Error = ex.Message };
        }
    }

    public BookingFetchResult Parse(string json, string url = "(file locale)")
    {
        using var doc = JsonDocument.Parse(json, DocOptions);
        var array = FindFlightArray(doc.RootElement);

        if (array is null)
        {
            return new BookingFetchResult
            {
                Success = false,
                RequestUrl = url,
                Error = "Non ho trovato nessun array di voli nella risposta.",
                SampleJson = Truncate(json, 2000),
            };
        }

        var legs = new List<FlightLeg>();
        string? sample = null;

        foreach (var item in array.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            sample ??= Truncate(item.GetRawText(), 2000);

            var leg = MapLeg(item);
            if (leg is not null) legs.Add(leg);
        }

        return new BookingFetchResult
        {
            Success = true,
            RequestUrl = url,
            Legs = legs,
            SampleJson = sample,
        };
    }

    private FlightLeg? MapLeg(JsonElement o)
    {
        var callsign = Str(o, FieldNames.Callsign);
        if (string.IsNullOrWhiteSpace(callsign)) return null;

        var dep = (Str(o, FieldNames.Departure) ?? "").ToUpperInvariant();
        var arr = (Str(o, FieldNames.Arrival) ?? "").ToUpperInvariant();
        if (dep.Length == 0 && arr.Length == 0) return null;

        var airport = config.Airport;
        var isArrival = arr.Equals(airport, StringComparison.OrdinalIgnoreCase);
        var isDeparture = dep.Equals(airport, StringComparison.OrdinalIgnoreCase);
        if (!isArrival && !isDeparture) return null;

        var day = ParseEventDate(Str(o, FieldNames.Date) ?? config.EventDate);
        var etd = ParseTime(Str(o, FieldNames.Eobt), day);
        var eta = ParseTime(Str(o, FieldNames.Eta), day);

        // Un arrivo senza ora stimata resta inutilizzabile per la timeline: lo teniamo
        // comunque, sarà Whazzup a dargli un orario quando il volo va online.
        return new FlightLeg
        {
            Callsign = callsign.Trim().ToUpperInvariant(),
            Registration = Str(o, FieldNames.Registration)?.ToUpperInvariant(),
            AircraftType = Str(o, FieldNames.Aircraft) ?? "",
            Departure = dep,
            Arrival = arr,
            Eta = isArrival ? eta : null,
            Etd = isDeparture ? etd : null,
            Kind = isArrival && isDeparture ? LegKind.Turnaround
                 : isArrival ? LegKind.Arrival
                 : LegKind.Departure,
            BookedStand = Stand(Str(o, FieldNames.Stand)),
            Source = "booking",
            Vid = Int(o, FieldNames.Vid),
        };
    }

    /// <summary>Normalizza lo stand prenotato: "TBD" e simili valgono come non assegnato.</summary>
    private static string? Stand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        return FieldNames.NoStand.Contains(v, StringComparer.OrdinalIgnoreCase) ? null : v;
    }

    private static JsonElement? FindFlightArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;

        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in FieldNames.Collections)
        {
            if (root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.Array) return el;
                if (el.ValueKind == JsonValueKind.Object)
                {
                    var nested = FindFlightArray(el);
                    if (nested is not null) return nested;
                }
            }
        }

        // Ultimo tentativo: il primo array di oggetti che troviamo.
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array &&
                prop.Value.GetArrayLength() > 0 &&
                prop.Value[0].ValueKind == JsonValueKind.Object)
                return prop.Value;
        }

        return null;
    }

    // --- Lettura tollerante dei campi -------------------------------------------

    private static string? Str(JsonElement o, string[] names)
    {
        foreach (var n in names)
        {
            if (!TryGetCaseInsensitive(o, n, out var el)) continue;

            var v = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.ToString(),
                JsonValueKind.Object => Str(el, ["id", "icao", "name", "code", "value"]),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static int? Int(JsonElement o, string[] names)
    {
        var s = Str(o, names);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool TryGetCaseInsensitive(JsonElement o, string name, out JsonElement value)
    {
        if (o.TryGetProperty(name, out value)) return true;

        foreach (var p in o.EnumerateObject())
        {
            if (p.NameEquals(name) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Accetta ISO, "1230", "12:30" e timestamp unix.</summary>
    public static DateTimeOffset? ParseTime(string? raw, DateOnly day)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso)
            && s.Length > 8)
            return iso;

        if (long.TryParse(s, out var number))
        {
            // Timestamp unix (secondi o millisecondi).
            if (number > 100_000_000)
            {
                return number > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                    : DateTimeOffset.FromUnixTimeSeconds(number);
            }

            // "1230" oppure "930".
            if (number is >= 0 and <= 2359)
            {
                var h = (int)(number / 100);
                var m = (int)(number % 100);
                if (h < 24 && m < 60) return Combine(day, h, m);
            }
        }

        var parts = s.Split([':', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var hh) && int.TryParse(parts[1], out var mm) &&
            hh < 24 && mm < 60)
            return Combine(day, hh, mm);

        return null;
    }

    private static DateTimeOffset Combine(DateOnly day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.Zero);

    public static DateOnly ParseEventDate(string yyyymmdd)
    {
        if (DateOnly.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateOnly.TryParse(yyyymmdd, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d;
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static bool LooksLikeJson(string body)
    {
        var t = body.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
