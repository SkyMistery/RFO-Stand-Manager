using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using RfoGateManager;
using RfoGateManager.Aurora;
using RfoGateManager.Data;
using RfoGateManager.Domain;
using RfoGateManager.Services;
using RfoGateManager.Sync;

var config = AppConfig.Load(AppPaths.Root, out var configPath);

// Utile per avviarlo da script o da un secondo monitor senza che apra il browser.
if (args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase)) config.OpenBrowserOnStart = false;

if (args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)) is { } portArg &&
    int.TryParse(portArg[7..], out var portOverride))
    config.UiPort = portOverride;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.WebHost.UseUrls($"http://127.0.0.1:{config.UiPort}");

builder.Services.AddSingleton(config);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AuroraClient>(sp => new AuroraClient(
    sp.GetRequiredService<ILogger<AuroraClient>>(), config.AuroraHost, config.AuroraPort));
builder.Services.AddSingleton<WhazzupClient>(sp => new WhazzupClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<ILogger<WhazzupClient>>()));
builder.Services.AddSingleton<BookingClient>(sp => new BookingClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(), config,
    sp.GetRequiredService<ILogger<BookingClient>>()));
builder.Services.AddSingleton<SharedStateStore>(sp => new SharedStateStore(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(), config,
    sp.GetRequiredService<ILogger<SharedStateStore>>()));
builder.Services.AddSingleton<PlanService>();
builder.Services.AddHostedService<RefreshWorker>();
builder.Services.AddHostedService<SyncWorker>();

var app = builder.Build();

// La UI viaggia dentro l'exe: nessun file da distribuire accanto.
var embedded = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded });
app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });

var plan = app.Services.GetRequiredService<PlanService>();
var aurora = app.Services.GetRequiredService<AuroraClient>();
var whazzup = app.Services.GetRequiredService<WhazzupClient>();
var shared = app.Services.GetRequiredService<SharedStateStore>();

plan.ReloadStands();
await shared.InitAsync();

// --- Stato generale ---------------------------------------------------------------

app.MapGet("/api/status", () => Results.Ok(new
{
    airport = config.Airport,
    eventDate = config.EventDate,
    configPath,
    dataDirectory = AppPaths.Data,
    operatorName = config.Operator,
    aurora = new
    {
        connected = aurora.IsConnected,
        host = $"{config.AuroraHost}:{config.AuroraPort}",
        connectedAt = aurora.ConnectedAt,
        selected = aurora.LastSelectedTraffic,
        error = aurora.LastError,
    },
    whazzup = new
    {
        lastFetch = whazzup.LastFetch,
        pilots = whazzup.LastPilotCount,
        error = whazzup.LastError,
    },
    booking = new
    {
        configured = config.HasBookingKey,
        url = plan.LastBookingResult?.RequestUrl,
        success = plan.LastBookingResult?.Success,
        legs = plan.LastBookingResult?.Legs.Count ?? 0,
        error = plan.LastBookingResult?.Error,
    },
    stands = new
    {
        count = plan.Catalog.Stands.Count,
        file = plan.Catalog.GtsPath,
        attributes = plan.Catalog.OverridePath,
        warnings = plan.Catalog.Warnings,
    },
    sharedState = new
    {
        enabled = shared.Shared,
        url = config.SharedStateUrl,
        version = shared.Current.Version,
        updatedBy = shared.Current.UpdatedBy,
        updatedAt = shared.Current.UpdatedAt,
        lastSync = shared.LastSync,
        error = shared.LastError,
    },
}));

// --- Piano stand ------------------------------------------------------------------

// Restituisce il piano già calcolato: lo tengono fresco i due worker in sottofondo, così la
// pagina può chiederlo spesso senza far ripartire ogni volta le letture da Aurora e Whazzup.
// Con ?fresh=true lo ricalcola subito.
app.MapGet("/api/plan", async (bool? fresh, CancellationToken ct) =>
{
    var s = fresh == true || plan.Snapshot.GeneratedAt == default
        ? await plan.RecomputeAsync(ct)
        : plan.Snapshot;
    return Results.Ok(Present(s, shared.Current, plan.Changes));
});

app.MapPost("/api/plan/publish", async (CancellationToken ct) =>
{
    await plan.PublishPlanAsync(ct);
    return Results.Ok(new { ok = true, version = shared.Current.Version });
});

app.MapPost("/api/pin", async (PinRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Key)) return Results.BadRequest(new { error = "Chiave mancante." });
    await plan.PinAsync(body.Key, body.Stand, ct);
    return Results.Ok(Present(plan.Snapshot, shared.Current, plan.Changes));
});

app.MapPost("/api/stand/closed", async (StandClosedRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Stand)) return Results.BadRequest(new { error = "Stand mancante." });
    await plan.SetStandClosedAsync(body.Stand, body.Closed, ct);
    return Results.Ok(Present(plan.Snapshot, shared.Current, plan.Changes));
});

// --- Sorgenti dati ----------------------------------------------------------------

app.MapPost("/api/booking/refresh", async (CancellationToken ct) =>
{
    var r = await plan.RefreshBookingAsync(ct);
    if (r.Success) await plan.RecomputeAsync(ct);
    return Results.Ok(r);
});

app.MapPost("/api/booking/import", async (HttpRequest req, CancellationToken ct) =>
{
    using var reader = new StreamReader(req.Body);
    var json = await reader.ReadToEndAsync(ct);

    try
    {
        var r = plan.LoadBookingFromJson(json);
        if (r.Success) await plan.RecomputeAsync(ct);
        return Results.Ok(r);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/stands/reload", async (CancellationToken ct) =>
{
    var c = plan.ReloadStands();
    await plan.RecomputeAsync(ct);
    return Results.Ok(new { count = c.Stands.Count, file = c.GtsPath, warnings = c.Warnings });
});

app.MapPost("/api/stands/template", () =>
{
    if (plan.Catalog.Stands.Count == 0)
        return Results.BadRequest(new { error = "Nessuno stand caricato: serve prima il file .gts." });

    var path = StandCatalog.WriteTemplate(AppPaths.Data, config.Airport, plan.Catalog.Stands);
    return Results.Ok(new { path, count = plan.Catalog.Stands.Count });
});

/// Parametri di calcolo, regolabili durante l'evento senza ricompilare.
app.MapGet("/api/options", () => Results.Ok(plan.Options));

app.MapPost("/api/options", async (AllocationOptions body, CancellationToken ct) =>
{
    if (body.BufferMinutes < 0 || body.BufferMinutes > 120)
        return Results.BadRequest(new { error = "Il margine deve stare fra 0 e 120 minuti." });

    if (body.DefaultTurnaroundMinutes is < 10 or > 600)
        return Results.BadRequest(new { error = "La sosta predefinita deve stare fra 10 e 600 minuti." });

    plan.Options = body;
    await plan.RecomputeAsync(ct);
    return Results.Ok(plan.Options);
});

// --- Aurora -----------------------------------------------------------------------

app.MapPost("/api/aurora/connect", async (CancellationToken ct) =>
{
    var ok = await aurora.ConnectAsync(ct);

    // Nello stato condiviso serve sapere chi ha scritto: se il nome non è configurato, il
    // callsign della posizione ATC connessa è l'identità più utile per gli altri.
    if (ok && config.OperatorFromAurora)
    {
        try
        {
            if (await aurora.GetConnectedCallsignAsync(ct) is { } me) config.Operator = me;
        }
        catch (AuroraException) { /* si tiene il nome che c'era */ }
    }

    return Results.Ok(new { connected = ok, error = aurora.LastError });
});

app.MapPost("/api/aurora/disconnect", async () =>
{
    await aurora.DisconnectAsync();
    return Results.Ok(new { connected = false });
});

/// Il traffico selezionato in Aurora, con lo stand che il piano gli riserva.
app.MapGet("/api/aurora/selected", async (CancellationToken ct) =>
{
    try
    {
        var callsign = await aurora.GetSelectedTrafficAsync(ct);
        if (callsign is null) return Results.Ok(new { callsign = (string?)null });

        var snapshot = plan.Snapshot.Assignments.Count > 0 ? plan.Snapshot : await plan.RecomputeAsync(ct);
        var assignment = snapshot.Assignments
            .FirstOrDefault(a => a.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));

        // Aurora accetta #LBGTE solo sul traffico assunto da chi lo manda: meglio dirlo
        // prima che il controllore clicchi e riceva un rifiuto.
        string? assumedBy = null;
        string? me = null;
        try
        {
            assumedBy = (await aurora.GetTrafficPositionAsync(callsign, ct)).AssumedStation;
            me = await aurora.GetConnectedCallsignAsync(ct);
        }
        catch (AuroraException) { /* informazione accessoria: senza, si prova comunque */ }

        var assumedByMe = !string.IsNullOrWhiteSpace(assumedBy) && me is not null &&
                          assumedBy.Equals(me, StringComparison.OrdinalIgnoreCase);

        return Results.Ok(new
        {
            callsign,
            // Un traffico che non tocca l'aeroporto (un sorvolo) non e' "senza stand": e' fuori piano.
            inPlan = assignment is not null,
            suggestion = assignment?.StandId,
            key = assignment?.Key,
            reason = assignment?.Reason
                     ?? $"{callsign} non arriva né parte da {config.Airport}: nessuno stand da assegnare.",
            conflict = assignment?.Conflict ?? false,
            oversize = assignment?.Oversize ?? false,
            assumedBy = string.IsNullOrWhiteSpace(assumedBy) ? null : assumedBy,
            assumedByMe,
        });
    }
    catch (AuroraException ex)
    {
        return Results.Ok(new { callsign = (string?)null, error = ex.Message });
    }
});

app.MapPost("/api/aurora/assign", async (AssignRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Callsign) || string.IsNullOrWhiteSpace(body.Stand))
        return Results.BadRequest(new { error = "Callsign e stand sono obbligatori." });

    try
    {
        var message = await plan.PushToAuroraAsync(body.Callsign, body.Stand, body.Key, body.PrivateMessage, ct);

        // Assegnare a mano significa bloccare quello stand per tutti.
        if (!string.IsNullOrWhiteSpace(body.Key)) await plan.PinAsync(body.Key, body.Stand, ct);

        return Results.Ok(new { ok = true, message });
    }
    catch (AuroraException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

/// Manda al pilota il PM con lo stand da aspettarsi.
app.MapPost("/api/aurora/notify", async (NotifyRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Callsign) || string.IsNullOrWhiteSpace(body.Stand))
        return Results.BadRequest(new { error = "Callsign e stand sono obbligatori." });

    try
    {
        var text = await plan.NotifyPilotAsync(body.Callsign, body.Stand, body.Key, ct);
        return Results.Ok(new { ok = true, message = $"Inviato a {body.Callsign}: \"{text}\"" });
    }
    catch (AuroraException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

/// Il testo che riceverebbe il pilota, per farlo vedere al controllore prima dell'invio.
app.MapGet("/api/aurora/message", (string callsign, string stand) =>
    Results.Ok(new { text = plan.PilotMessage(callsign, stand) }));

// --- Strippiera partenze -------------------------------------------------------------

app.MapGet("/api/departures", () =>
{
    var strips = plan.Departures();
    return Results.Ok(new
    {
        waiting = strips.Where(s => !s.Called),
        called = strips.Where(s => s.Called),
    });
});

app.MapPost("/api/departures/called", async (CalledRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Callsign))
        return Results.BadRequest(new { error = "Callsign obbligatorio." });

    await plan.SetCalledAsync(body.Callsign, body.Called, ct);
    return Results.Ok(new { ok = true });
});

/// Toglie lo stand da Aurora, e se c'era un'assegnazione fissata la libera per tutti.
app.MapPost("/api/aurora/clear", async (ClearRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Callsign))
        return Results.BadRequest(new { error = "Callsign obbligatorio." });

    try
    {
        await aurora.ClearGateAsync(body.Callsign, ct);
        if (!string.IsNullOrWhiteSpace(body.Key)) await plan.PinAsync(body.Key, null, ct);
        return Results.Ok(new { ok = true, message = $"Stand tolto a {body.Callsign}." });
    }
    catch (AuroraException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/aurora/show", async (ShowRequest body, CancellationToken ct) =>
{
    try
    {
        await aurora.ZoomAndSelectAsync(body.Callsign, ct);
        return Results.Ok(new { ok = true });
    }
    catch (AuroraException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// --- Avvio ------------------------------------------------------------------------

var url = $"http://127.0.0.1:{config.UiPort}";
app.Logger.LogInformation("RFO Gate Manager — {Airport} — {Url}", config.Airport, url);
app.Logger.LogInformation("Configurazione: {Path}", configPath);
app.Logger.LogInformation("Dati: {Path}", AppPaths.Data);

if (config.OpenBrowserOnStart) OpenBrowser(url, app.Logger);

await app.RunAsync();
return;

static void OpenBrowser(string url, ILogger logger)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        logger.LogWarning("Browser non aperto ({Error}). Apri a mano: {Url}", ex.Message, url);
    }
}

/// <summary>Proiezione del piano per la UI: i record di dominio restano interni.</summary>
static object Present(PlanSnapshot s, SharedDocument doc, IReadOnlyList<StandChange> changes)
{
    var byKey = s.Requests.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    return new
    {
        generatedAt = s.GeneratedAt,
        unassigned = s.Unassigned,
        conflicts = s.Conflicts,
        warnings = s.Warnings,
        sharedVersion = doc.Version,
        occupants = s.Occupants.Select(o => new { callsign = o.Callsign, stand = o.StandId, source = o.Source }),
        changes = changes.Take(30).Select(c => new
        {
            at = c.At,
            key = c.Key,
            callsign = c.Callsign,
            from = c.FromStand,
            to = c.ToStand,
            reason = c.Reason,
        }),
        stands = s.Stands.Select(st => new
        {
            id = st.Id,
            lat = st.Lat,
            lon = st.Lon,
            maxSize = st.MaxSize.ToString(),
            contact = st.Contact,
            uses = st.Uses,
            airlines = st.Airlines,
            blocks = st.Blocks,
            disabled = st.Disabled,
            note = st.Note,
        }),
        assignments = s.Assignments.Select(a =>
        {
            byKey.TryGetValue(a.Key, out var r);
            return new
            {
                key = a.Key,
                callsign = a.Callsign,
                stand = a.StandId,
                from = a.From,
                to = a.To,
                reason = a.Reason,
                conflict = a.Conflict,
                oversize = a.Oversize,
                pinned = doc.Pins.ContainsKey(a.Key),
                aircraft = r?.AircraftType,
                size = r?.Size.ToString(),
                use = r?.Use,
                origin = r?.Inbound?.Departure,
                destination = r?.Outbound?.Arrival,
                inboundOnline = r?.Inbound?.IsOnline ?? false,
                trackState = r?.Inbound?.TrackState ?? r?.Outbound?.TrackState,
                distanceNm = r?.Inbound?.DistanceToArrivalNm,
                bookedStand = r?.BookedStand,
                actualStand = r?.ActualStand,
                source = r?.Inbound?.Source ?? r?.Outbound?.Source,
                hasRotation = r?.Inbound is not null && r?.Outbound is not null,
                reassigned = a.Reassigned,
                displacedFrom = a.DisplacedFrom,
                displacedBy = a.DisplacedBy,
                wasPinned = a.WasPinned,
                unscheduled = a.Unscheduled,
                notifiedStand = doc.Notified.TryGetValue(a.Key, out var told) ? told : null,
            };
        }),
    };
}

internal sealed record PinRequest(string Key, string? Stand);
internal sealed record StandClosedRequest(string Stand, bool Closed);
internal sealed record AssignRequest(string Callsign, string Stand, string? Key, bool PrivateMessage);
internal sealed record ShowRequest(string Callsign);
internal sealed record ClearRequest(string Callsign, string? Key);
internal sealed record NotifyRequest(string Callsign, string Stand, string? Key);
internal sealed record CalledRequest(string Callsign, bool? Called);

/// <summary>Tiene il piano aggiornato in sottofondo, così la UI trova sempre dati freschi.</summary>
internal sealed class RefreshWorker(PlanService plan, ILogger<RefreshWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Un attimo di respiro: l'host deve finire di partire.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await plan.RecomputeAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Ricalcolo del piano fallito: {Error}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Tiene allineata questa postazione con le altre. Ogni pochi secondi chiede al server se lo
/// stato condiviso è cambiato (se no, risponde 304 senza corpo) e, quando un'altra postazione
/// ha scritto, ricalcola subito invece di aspettare il prossimo giro.
/// </summary>
internal sealed class SyncWorker(PlanService plan, SharedStateStore shared, ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!shared.Shared) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var before = shared.Current.Version;
                await shared.PullAsync(stoppingToken);

                if (shared.Current.Version != before)
                {
                    log.LogInformation("Stato condiviso aggiornato da {Who}: versione {Version}",
                        shared.Current.UpdatedBy, shared.Current.Version);
                    await plan.RecomputeAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Sincronizzazione fallita: {Error}", ex.Message);
            }
        }
    }
}
