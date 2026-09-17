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

    /// <summary>URL dello stato condiviso fra controllori. Vuoto = modalità solo locale.</summary>
    public string SharedStateUrl { get; set; } = "";

    public string SharedStateToken { get; set; } = "";

    public string AuroraHost { get; set; } = "127.0.0.1";
    public int AuroraPort { get; set; } = 1130;

    /// <summary>Porta della UI locale.</summary>
    public int UiPort { get; set; } = 5057;

    public bool OpenBrowserOnStart { get; set; } = true;

    /// <summary>Nome operatore, per sapere chi ha fatto cosa nello stato condiviso.</summary>
    public string Operator { get; set; } = Environment.UserName;

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
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), Options) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Il file di configurazione '{file}' non è JSON valido: {ex.Message}", ex);
        }
    }
}
