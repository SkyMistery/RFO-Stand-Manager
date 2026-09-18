using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfoGateManager.Sync;

/// <summary>
/// Lo stato condiviso fra le postazioni dell'evento. Chi assegna uno stand, avvisa un pilota
/// o segna una partenza come "ha chiamato" lo scrive qui, e tutti gli altri lo vedono.
/// </summary>
public sealed class SharedDocument
{
    /// <summary>Versione assegnata dal server. Zero = documento non ancora creato.</summary>
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    /// <summary>Assegnazioni decise a mano: chiave rotazione -> stand. Sono vincolanti.</summary>
    public Dictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ultimo piano automatico pubblicato: chiave rotazione -> stand.</summary>
    public Dictionary<string, string> Plan { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stand chiusi durante l'evento (lavori, aereo fermo, decisione del supervisore).</summary>
    public List<string> ClosedStands { get; set; } = [];

    /// <summary>Piloti avvisati via PM: chiave rotazione -> stand comunicato.</summary>
    public Dictionary<string, string> Notified { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Partenze segnate a mano nella strippiera: callsign -> ha chiamato sì/no. Vince sul
    /// riconoscimento automatico, così il controllore ha sempre l'ultima parola.
    /// </summary>
    public Dictionary<string, bool> Called { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SharedDocument Clone() => new()
    {
        Version = Version,
        UpdatedAt = UpdatedAt,
        UpdatedBy = UpdatedBy,
        Pins = new(Pins, StringComparer.OrdinalIgnoreCase),
        Plan = new(Plan, StringComparer.OrdinalIgnoreCase),
        ClosedStands = [.. ClosedStands],
        Notified = new(Notified, StringComparer.OrdinalIgnoreCase),
        Called = new(Called, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// Sincronizza il documento con il server secondo il contratto di <c>docs/SYNC-API.md</c>:
/// busta <c>{version, updatedAt, updatedBy, data}</c>, versione assegnata dal server,
/// scrittura condizionata con <c>If-Match</c> (409 se qualcun altro ha scritto nel frattempo)
/// e lettura con <c>If-None-Match</c> (304 se non è cambiato niente).
///
/// Senza URL configurato lavora da sola su <c>state.local.json</c>, nello stesso formato.
/// </summary>
public sealed class SharedStateStore(
    HttpClient http, Data.AppConfig config, ILogger<SharedStateStore> log, string? localPath = null)
{
    private const int MaxWriteAttempts = 5;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _localPath = localPath ?? Path.Combine(AppPaths.Root, "state.local.json");

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private SharedDocument _doc = new();

    public SharedDocument Current => _doc;
    public bool Shared => config.SharedMode;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSync { get; private set; }

    /// <summary>Scatta quando arriva una versione nuova scritta da un'altra postazione.</summary>
    public event Action? RemoteChanged;

    public async Task InitAsync(CancellationToken ct = default)
    {
        LoadLocal();
        if (config.SharedMode) await PullAsync(ct);
    }

    /// <summary>
    /// Scarica il documento se è cambiato. Costa poco anche se chiamato spesso: finché la
    /// versione è la stessa il server risponde 304 senza corpo.
    /// </summary>
    public async Task<SharedDocument> PullAsync(CancellationToken ct = default)
    {
        if (!config.SharedMode) return _doc;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, config.SharedStateUrl);
            AddAuth(req);
            if (_doc.Version > 0) req.Headers.IfNoneMatch.Add(ETag(_doc.Version));

            using var res = await http.SendAsync(req, ct);

            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                // Sul server il documento non c'è: evento nuovo, database svuotato, o finora si
                // lavorava da soli. La prossima scrittura lo ricrea da zero con quello che sappiamo.
                _doc.Version = 0;
                MarkSynced();
                return _doc;
            }

            if (res.StatusCode == HttpStatusCode.NotModified)
            {
                MarkSynced();
                return _doc;
            }

            await EnsureOk(res, "lettura", ct);
            Adopt(await ReadEnvelope(res, ct), fromRemote: true);
            MarkSynced();
        }
        catch (Exception ex)
        {
            LastError = $"Lettura dello stato condiviso fallita: {ex.Message}";
            log.LogWarning("{Error}", LastError);
        }

        return _doc;
    }

    /// <summary>
    /// Applica una modifica e la pubblica. Se un'altra postazione ha scritto nel frattempo il
    /// server rifiuta (409) e restituisce la versione corrente: riapplichiamo la stessa
    /// modifica su quella e riproviamo. Così due scritture contemporanee si sommano invece
    /// di cancellarsi a vicenda.
    /// </summary>
    public async Task<SharedDocument> MutateAsync(Action<SharedDocument> change, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!config.SharedMode)
            {
                var local = _doc.Clone();
                change(local);
                local.Version++;
                local.UpdatedAt = DateTimeOffset.UtcNow;
                local.UpdatedBy = config.Operator;
                _doc = local;
                SaveLocal();
                return _doc;
            }

            for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
            {
                var draft = _doc.Clone();
                change(draft);

                var outcome = await TryWriteAsync(draft, ct);
                if (outcome == WriteOutcome.Written) return _doc;
                if (outcome == WriteOutcome.Failed) break;
                // Conflitto: _doc ora è la versione del server, si riapplica e si riprova.
            }

            // Il server non è raggiungibile o rifiuta: applichiamo in locale per non perdere
            // la decisione del controllore, e la prossima scrittura riuscita la porterà su.
            var fallback = _doc.Clone();
            change(fallback);
            _doc = fallback;
            SaveLocal();
            return _doc;
        }
        finally
        {
            _lock.Release();
        }
    }

    private enum WriteOutcome { Written, Conflict, Failed }

    private async Task<WriteOutcome> TryWriteAsync(SharedDocument draft, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, config.SharedStateUrl)
            {
                Content = JsonContent.Create(new WriteBody(config.Operator, ToData(draft)), options: Options),
            };
            AddAuth(req);
            req.Headers.IfMatch.Add(ETag(_doc.Version));

            using var res = await http.SendAsync(req, ct);

            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                // Scrivevamo su una versione che il server non ha più: si riparte da zero.
                _doc.Version = 0;
                return WriteOutcome.Conflict;
            }

            if (res.StatusCode == HttpStatusCode.Conflict || res.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                Adopt(await ReadEnvelope(res, ct), fromRemote: true);
                log.LogInformation("Scrittura concorrente sullo stato condiviso: riapplico sulla versione {Version}", _doc.Version);
                return WriteOutcome.Conflict;
            }

            await EnsureOk(res, "scrittura", ct);
            Adopt(await ReadEnvelope(res, ct), fromRemote: false);
            MarkSynced();
            return WriteOutcome.Written;
        }
        catch (Exception ex)
        {
            LastError = $"Scrittura dello stato condiviso fallita: {ex.Message}";
            log.LogWarning("{Error}", LastError);
            return WriteOutcome.Failed;
        }
    }

    private void Adopt(Envelope? env, bool fromRemote)
    {
        if (env is null) return;

        var changed = env.Version != _doc.Version;
        _doc = FromEnvelope(env);
        SaveLocal();

        if (fromRemote && changed) RemoteChanged?.Invoke();
    }

    private void MarkSynced()
    {
        LastError = null;
        LastSync = DateTimeOffset.UtcNow;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(config.SharedStateToken))
            req.Headers.TryAddWithoutValidation("x-api-key", config.SharedStateToken);
    }

    private static EntityTagHeaderValue ETag(long version) => new($"\"{version}\"");

    private static async Task EnsureOk(HttpResponseMessage res, string what, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;

        var body = await res.Content.ReadAsStringAsync(ct);
        if (body.Length > 300) body = body[..300];
        throw new HttpRequestException($"{what}: HTTP {(int)res.StatusCode} {body}");
    }

    private static async Task<Envelope?> ReadEnvelope(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<Envelope>(text, Options);
    }

    // --- Busta del contratto -----------------------------------------------------

    /// <summary>Quello che il server restituisce: metadati suoi, contenuto nostro.</summary>
    public sealed record Envelope
    {
        public long Version { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
        public string? UpdatedBy { get; init; }
        public Data? Data { get; init; }
    }

    /// <summary>Il contenuto. Per il server è opaco: lo conserva senza interpretarlo.</summary>
    public sealed record Data
    {
        public Dictionary<string, string>? Pins { get; init; }
        public Dictionary<string, string>? Plan { get; init; }
        public List<string>? ClosedStands { get; init; }
        public Dictionary<string, string>? Notified { get; init; }
        public Dictionary<string, bool>? Called { get; init; }
    }

    private sealed record WriteBody(string UpdatedBy, Data Data);

    private static Data ToData(SharedDocument d) => new()
    {
        Pins = d.Pins,
        Plan = d.Plan,
        ClosedStands = d.ClosedStands,
        Notified = d.Notified,
        Called = d.Called,
    };

    private static SharedDocument FromEnvelope(Envelope e) => new()
    {
        Version = e.Version,
        UpdatedAt = e.UpdatedAt ?? DateTimeOffset.MinValue,
        UpdatedBy = e.UpdatedBy ?? "",
        Pins = new(e.Data?.Pins ?? [], StringComparer.OrdinalIgnoreCase),
        Plan = new(e.Data?.Plan ?? [], StringComparer.OrdinalIgnoreCase),
        ClosedStands = e.Data?.ClosedStands ?? [],
        Notified = new(e.Data?.Notified ?? [], StringComparer.OrdinalIgnoreCase),
        Called = new(e.Data?.Called ?? [], StringComparer.OrdinalIgnoreCase),
    };

    private static Envelope ToEnvelope(SharedDocument d) => new()
    {
        Version = d.Version,
        UpdatedAt = d.UpdatedAt,
        UpdatedBy = d.UpdatedBy,
        Data = ToData(d),
    };

    // --- Copia locale ------------------------------------------------------------

    private void LoadLocal()
    {
        try
        {
            if (!File.Exists(_localPath)) return;

            var env = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(_localPath), Options);
            if (env?.Data is not null) _doc = FromEnvelope(env);
        }
        catch (Exception ex)
        {
            // Anche un file del formato vecchio finisce qui: si riparte puliti.
            log.LogWarning("Stato locale illeggibile, riparto da zero: {Error}", ex.Message);
            _doc = new SharedDocument();
        }
    }

    private void SaveLocal()
    {
        try
        {
            File.WriteAllText(_localPath, JsonSerializer.Serialize(ToEnvelope(_doc), Options));
        }
        catch (Exception ex)
        {
            log.LogWarning("Stato locale non salvato: {Error}", ex.Message);
        }
    }
}
