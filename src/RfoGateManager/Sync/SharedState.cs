using System.Net.Http.Json;
using System.Text.Json;

namespace RfoGateManager.Sync;

/// <summary>
/// Documento condiviso fra i controllori dell'evento: chi assegna uno stand lo scrive qui,
/// tutti gli altri lo vedono. Senza questo, due posizioni assegnerebbero lo stesso stand.
/// </summary>
public sealed class SharedDocument
{
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    /// <summary>Assegnazioni decise a mano: chiave rotazione -> stand. Sono vincolanti.</summary>
    public Dictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ultimo piano automatico pubblicato: chiave rotazione -> stand.</summary>
    public Dictionary<string, string> Plan { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stand chiusi durante l'evento (lavori, aereo fermo, decisione del supervisore).</summary>
    public List<string> ClosedStands { get; set; } = [];
}

/// <summary>
/// Sincronizza il documento con un endpoint HTTP che supporta GET e PUT su un JSON.
/// Quando non è configurato nessun URL lavora solo in memoria e su file locale, così
/// l'app resta utilizzabile anche da sola.
/// </summary>
public sealed class SharedStateStore(HttpClient http, Data.AppConfig config, ILogger<SharedStateStore> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _localPath = Path.Combine(AppPaths.Root, "state.local.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private SharedDocument _doc = new();

    public SharedDocument Current => _doc;
    public bool Shared => config.SharedMode;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSync { get; private set; }

    public async Task InitAsync(CancellationToken ct = default)
    {
        if (!config.SharedMode)
        {
            LoadLocal();
            return;
        }

        await PullAsync(ct);
    }

    /// <summary>Scarica il documento remoto e lo adotta se più recente del nostro.</summary>
    public async Task<SharedDocument> PullAsync(CancellationToken ct = default)
    {
        if (!config.SharedMode) return _doc;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, config.SharedStateUrl);
            AddAuth(req);

            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                LastError = null;
                return _doc; // documento non ancora creato: lo creeremo al primo push
            }

            res.EnsureSuccessStatusCode();
            var remote = await res.Content.ReadFromJsonAsync<SharedDocument>(Options, ct);

            if (remote is not null && remote.Version >= _doc.Version)
            {
                _doc = remote;
                SaveLocal();
            }

            LastError = null;
            LastSync = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.LogWarning("Stato condiviso non raggiungibile in lettura: {Error}", ex.Message);
        }

        return _doc;
    }

    /// <summary>Applica una modifica e la pubblica. La scrittura locale non si perde se la rete cade.</summary>
    public async Task<SharedDocument> MutateAsync(Action<SharedDocument> change, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (config.SharedMode) await PullAsync(ct);

            change(_doc);
            _doc.Version++;
            _doc.UpdatedAt = DateTimeOffset.UtcNow;
            _doc.UpdatedBy = config.Operator;

            SaveLocal();

            if (config.SharedMode) await PushAsync(ct);

            return _doc;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PushAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, config.SharedStateUrl)
            {
                Content = JsonContent.Create(_doc, options: Options),
            };
            AddAuth(req);

            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            LastError = null;
            LastSync = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.LogWarning("Stato condiviso non raggiungibile in scrittura: {Error}", ex.Message);
        }
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(config.SharedStateToken))
            req.Headers.TryAddWithoutValidation("x-token", config.SharedStateToken);
    }

    private void LoadLocal()
    {
        try
        {
            if (File.Exists(_localPath))
                _doc = JsonSerializer.Deserialize<SharedDocument>(File.ReadAllText(_localPath), Options) ?? new SharedDocument();
        }
        catch (Exception ex)
        {
            log.LogWarning("Stato locale illeggibile, riparto da zero: {Error}", ex.Message);
            _doc = new SharedDocument();
        }
    }

    private void SaveLocal()
    {
        try
        {
            File.WriteAllText(_localPath, JsonSerializer.Serialize(_doc, Options));
        }
        catch (Exception ex)
        {
            log.LogWarning("Stato locale non salvato: {Error}", ex.Message);
        }
    }
}
