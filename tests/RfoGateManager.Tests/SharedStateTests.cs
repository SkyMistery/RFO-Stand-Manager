using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RfoGateManager.Data;
using RfoGateManager.Sync;
using Xunit;

namespace RfoGateManager.Tests;

/// <summary>
/// Un server in memoria che rispetta il contratto di docs/SYNC-API.md. È il comportamento
/// che deve avere il sito che fa da ponte, scritto in forma eseguibile.
/// </summary>
internal sealed class FakeSyncServer : HttpMessageHandler
{
    public const string Key = "chiave-di-prova";

    private readonly object _gate = new();
    private long _version;
    private string? _updatedBy;
    private DateTimeOffset _updatedAt;
    private JsonNode? _data;

    public int Gets;
    public int GetsWithBody;
    public int Puts;
    public int Conflicts;

    public bool Down { get; set; }

    public JsonNode? Data { get { lock (_gate) return _data?.DeepClone(); } }
    public long Version { get { lock (_gate) return _version; } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Down) throw new HttpRequestException("server irraggiungibile");

        if (!request.Headers.TryGetValues("x-api-key", out var keys) || keys.FirstOrDefault() != Key)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        lock (_gate)
        {
            if (request.Method == HttpMethod.Get)
            {
                Gets++;
                if (_version == 0) return new HttpResponseMessage(HttpStatusCode.NotFound);

                var inm = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
                if (inm == Tag(_version)) return new HttpResponseMessage(HttpStatusCode.NotModified);

                GetsWithBody++;
                return Envelope(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Put)
            {
                Puts++;

                // If-Match è obbligatorio: senza, una postazione potrebbe sovrascrivere alla cieca.
                var ifMatch = request.Headers.IfMatch.FirstOrDefault()?.Tag;
                if (ifMatch is null) return new HttpResponseMessage(HttpStatusCode.PreconditionRequired);

                // Documento inesistente e il client crede di aggiornare una versione: 404.
                if (_version == 0 && ifMatch != Tag(0)) return new HttpResponseMessage(HttpStatusCode.NotFound);

                if (ifMatch != Tag(_version))
                {
                    Conflicts++;
                    return Envelope(HttpStatusCode.Conflict);
                }

                var parsed = JsonNode.Parse(body!)!.AsObject();
                if (parsed["data"] is not JsonObject data) return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);

                _data = data.DeepClone();
                _updatedBy = parsed["updatedBy"]?.GetValue<string>();
                _updatedAt = DateTimeOffset.UtcNow;
                _version++;
                return Envelope(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    /// <summary>Il database del server viene svuotato.</summary>
    public void Wipe()
    {
        lock (_gate)
        {
            _version = 0;
            _data = null;
        }
    }

    private HttpResponseMessage Envelope(HttpStatusCode status)
    {
        var env = new JsonObject
        {
            ["version"] = _version,
            ["updatedAt"] = _updatedAt,
            ["updatedBy"] = _updatedBy,
            ["data"] = _data?.DeepClone(),
        };

        var res = new HttpResponseMessage(status)
        {
            Content = new StringContent(env.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(Tag(_version));
        return res;
    }

    private static string Tag(long v) => $"\"{v}\"";
}

public sealed class SharedStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rfo-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();

    public SharedStateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* pulizia */ }
    }

    private SharedStateStore Station(string name) => new(
        new HttpClient(_server, disposeHandler: false),
        new AppConfig
        {
            SharedStateUrl = "https://ponte.example/api/rfo/events/lirn20260919/state",
            SharedStateToken = FakeSyncServer.Key,
            Operator = name,
        },
        NullLogger<SharedStateStore>.Instance,
        Path.Combine(_dir, name + ".json"));

    [Fact]
    public async Task La_prima_scrittura_crea_il_documento_alla_versione_uno()
    {
        var gnd = Station("GND");
        await gnd.InitAsync();

        await gnd.MutateAsync(d => d.Pins["AZA100"] = "14");

        Assert.Equal(1, _server.Version);
        Assert.Equal("14", _server.Data!["pins"]!["AZA100"]!.GetValue<string>());
        Assert.Equal(1, gnd.Current.Version);
    }

    [Fact]
    public async Task Quello_che_scrive_una_postazione_lo_vede_l_altra()
    {
        var gnd = Station("GND");
        var twr = Station("TWR");
        await gnd.InitAsync();
        await twr.InitAsync();

        await gnd.MutateAsync(d => d.Called["VLG8AZ"] = true);
        await twr.PullAsync();

        Assert.True(twr.Current.Called["VLG8AZ"]);
        Assert.Equal("GND", twr.Current.UpdatedBy);
    }

    [Fact]
    public async Task Due_scritture_contemporanee_si_sommano_invece_di_cancellarsi()
    {
        // Il caso per cui esiste il controllo di versione: tutte e due partono dalla stessa
        // versione, e senza If-Match la seconda cancellerebbe la prima senza che nessuno se
        // ne accorga.
        var gnd = Station("GND");
        var twr = Station("TWR");
        await gnd.InitAsync();
        await twr.InitAsync();

        await gnd.MutateAsync(d => d.Pins["AZA100"] = "14");
        await twr.MutateAsync(d => d.Pins["RYR200"] = "22");   // twr è ancora alla versione 0

        var pins = _server.Data!["pins"]!.AsObject();
        Assert.Equal("14", pins["AZA100"]!.GetValue<string>());
        Assert.Equal("22", pins["RYR200"]!.GetValue<string>());
        Assert.Equal(1, _server.Conflicts);
        Assert.Equal(2, _server.Version);
    }

    [Fact]
    public async Task Molte_postazioni_che_scrivono_insieme_non_perdono_niente()
    {
        var stations = Enumerable.Range(1, 5).Select(i => Station($"POS{i}")).ToList();
        foreach (var s in stations) await s.InitAsync();

        await Task.WhenAll(stations.Select((s, i) => s.MutateAsync(d => d.Pins[$"K{i}"] = $"{10 + i}")));

        var pins = _server.Data!["pins"]!.AsObject();
        Assert.Equal(5, pins.Count);
        Assert.Equal(5, _server.Version);
    }

    [Fact]
    public async Task Se_non_e_cambiato_niente_il_server_non_rimanda_il_documento()
    {
        var gnd = Station("GND");
        await gnd.InitAsync();
        await gnd.MutateAsync(d => d.Pins["AZA100"] = "14");

        var before = _server.GetsWithBody;
        for (var i = 0; i < 10; i++) await gnd.PullAsync();

        Assert.Equal(before, _server.GetsWithBody);   // dieci 304, nessun corpo
    }

    [Fact]
    public async Task Togliere_una_voce_si_propaga_come_aggiungerla()
    {
        var gnd = Station("GND");
        var twr = Station("TWR");
        await gnd.InitAsync();
        await twr.InitAsync();

        await gnd.MutateAsync(d => d.Pins["AZA100"] = "14");
        await twr.PullAsync();
        await twr.MutateAsync(d => d.Pins.Remove("AZA100"));
        await gnd.PullAsync();

        Assert.False(gnd.Current.Pins.ContainsKey("AZA100"));
    }

    [Fact]
    public async Task Col_server_giu_la_decisione_non_si_perde_e_parte_alla_scrittura_successiva()
    {
        var gnd = Station("GND");
        await gnd.InitAsync();

        _server.Down = true;
        await gnd.MutateAsync(d => d.Pins["AZA100"] = "14");
        Assert.Equal("14", gnd.Current.Pins["AZA100"]);
        Assert.NotNull(gnd.LastError);

        _server.Down = false;
        await gnd.MutateAsync(d => d.Pins["RYR200"] = "22");

        var pins = _server.Data!["pins"]!.AsObject();
        Assert.Equal("14", pins["AZA100"]!.GetValue<string>());
        Assert.Equal("22", pins["RYR200"]!.GetValue<string>());
        Assert.Null(gnd.LastError);
    }

    [Fact]
    public async Task Se_il_server_perde_il_documento_la_postazione_lo_ricrea_con_quello_che_sa()
    {
        var gnd = Station("GND");
        await gnd.InitAsync();
        await gnd.MutateAsync(d => d.Pins["AZA100"] = "14");
        await gnd.MutateAsync(d => d.Pins["RYR200"] = "22");
        Assert.Equal(2, gnd.Current.Version);

        _server.Wipe();
        await gnd.MutateAsync(d => d.Called["VLG8AZ"] = true);   // scrive con If-Match "2"

        Assert.Equal(1, _server.Version);
        var data = _server.Data!;
        Assert.Equal("14", data["pins"]!["AZA100"]!.GetValue<string>());
        Assert.Equal("22", data["pins"]!["RYR200"]!.GetValue<string>());
        Assert.True(data["called"]!["VLG8AZ"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Chi_lavorava_da_solo_porta_le_sue_decisioni_sul_server_alla_prima_connessione()
    {
        var path = Path.Combine(_dir, "prima-da-solo.json");

        var solo = new SharedStateStore(new HttpClient(_server, false),
            new AppConfig { SharedStateUrl = "", Operator = "GND" },
            NullLogger<SharedStateStore>.Instance, path);
        await solo.InitAsync();
        await solo.MutateAsync(d => d.Pins["AZA100"] = "14");
        await solo.MutateAsync(d => d.Pins["RYR200"] = "22");   // versione locale 2

        var shared = new SharedStateStore(new HttpClient(_server, false),
            new AppConfig { SharedStateUrl = "https://ponte.example/state", SharedStateToken = FakeSyncServer.Key, Operator = "GND" },
            NullLogger<SharedStateStore>.Instance, path);
        await shared.InitAsync();
        await shared.MutateAsync(d => d.ClosedStands.Add("61"));

        Assert.Equal(1, _server.Version);
        Assert.Equal(2, _server.Data!["pins"]!.AsObject().Count);
    }

    [Fact]
    public async Task Senza_chiave_giusta_il_server_rifiuta_e_l_app_lo_dice()
    {
        var intruder = new SharedStateStore(
            new HttpClient(_server, disposeHandler: false),
            new AppConfig { SharedStateUrl = "https://ponte.example/state", SharedStateToken = "sbagliata" },
            NullLogger<SharedStateStore>.Instance,
            Path.Combine(_dir, "intruso.json"));

        await intruder.InitAsync();
        await intruder.MutateAsync(d => d.Pins["X"] = "1");

        Assert.Equal(0, _server.Version);
        Assert.Contains("401", intruder.LastError);
    }

    [Fact]
    public async Task Senza_server_configurato_lavora_da_sola_e_ritrova_lo_stato_al_riavvio()
    {
        var path = Path.Combine(_dir, "solo.json");
        var config = new AppConfig { SharedStateUrl = "", Operator = "SOLO" };

        var first = new SharedStateStore(new HttpClient(_server, false), config, NullLogger<SharedStateStore>.Instance, path);
        await first.InitAsync();
        await first.MutateAsync(d => d.Notified["AZA100:1"] = "14");

        var restarted = new SharedStateStore(new HttpClient(_server, false), config, NullLogger<SharedStateStore>.Instance, path);
        await restarted.InitAsync();

        Assert.Equal("14", restarted.Current.Notified["AZA100:1"]);
        Assert.Equal(0, _server.Puts);
    }
}
