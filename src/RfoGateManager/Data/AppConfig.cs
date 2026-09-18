using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfoGateManager.Data;

/// <summary>
/// Configurazione letta da <c>secrets/booking.json</c> accanto all'eseguibile. Sta su disco
/// e non nel browser: la x-key non deve mai finire lato client.
/// </summary>
public sealed class AppConfig
{
    public string BookingBaseUrl { get; set; } = "https://booking.it.ivao.aero/api";
    public string BookingXKey { get; set; } = "";

    /// <summary>Data evento in formato yyyyMMdd, come la vuole l'endpoint del booking.</summary>
    public string EventDate { get; set; } = DateTimeOffset.UtcNow.ToString("yyyyMMdd");

    public string Airport { get; set; } = "LIRN";

    /// <summary>
    /// Punto di riferimento dell'aeroporto (ARP di Capodichino). Serve a capire se un aereo
    /// fermo su "stand 12" è a Napoli: i nomi degli stand si ripetono fra aeroporti, e Aurora
    /// riporta lo stand anche per gli aerei parcheggiati a Fiumicino.
    /// </summary>
    public double AirportLat { get; set; } = 40.8860;

    public double AirportLon { get; set; } = 14.2908;

    /// <summary>URL dello stato condiviso fra controllori. Vuoto = modalità solo locale.</summary>
    public string SharedStateUrl { get; set; } = "";

    public string SharedStateToken { get; set; } = "";

    public string AuroraHost { get; set; } = "127.0.0.1";
    public int AuroraPort { get; set; } = 1130;

    /// <summary>Porta della UI locale.</summary>
    public int UiPort { get; set; } = 5057;

    public bool OpenBrowserOnStart { get; set; } = true;

    /// <summary>
    /// Il PM che riceve il pilota. Segnaposto: {callsign}, {stand}, {airport}. In inglese
    /// perché all'RFO arrivano piloti da tutto il mondo. Il ';' non è ammesso da Aurora e
    /// viene sostituito da una virgola.
    /// </summary>
    public string PilotMessageTemplate { get; set; } =
        "{callsign}, expect stand {stand} on arrival at Naples. Welcome to the RFO!";

    /// <summary>Nome operatore, per sapere chi ha fatto cosa nello stato condiviso.</summary>
    public string Operator { get; set; } = Environment.UserName;

    /// <summary>Il nome operatore non era configurato: lo prendiamo da Aurora appena connessi.</summary>
    [JsonIgnore]
    public bool OperatorFromAurora { get; private set; }

    [JsonIgnore]
    public bool HasBookingKey => !string.IsNullOrWhiteSpace(BookingXKey)
                                 && !BookingXKey.StartsWith("METTI-QUI", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool SharedMode => !string.IsNullOrWhiteSpace(SharedStateUrl);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static AppConfig Load(string baseDirectory, out string path)
    {
        path = Path.Combine(baseDirectory, "secrets", "booking.json");

        if (!File.Exists(path))
        {
            var example = Path.Combine(baseDirectory, "secrets", "booking.example.json");
            if (File.Exists(example))
            {
                // Riportiamo il file davvero letto: dire "booking.json" quando non esiste
                // manderebbe l'utente a cercare un file che non c'e'.
                path = example;
                return Read(example);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fresh = new AppConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, Options));
            return fresh;
        }

        return Read(path);
    }

    private static AppConfig Read(string file)
    {
        try
        {
            var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), Options) ?? new AppConfig();
            if (string.IsNullOrWhiteSpace(c.Operator))
            {
                c.Operator = Environment.UserName;
                c.OperatorFromAurora = true;
            }
            return c;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Il file di configurazione '{file}' non è JSON valido: {ex.Message}", ex);
        }
    }
}
