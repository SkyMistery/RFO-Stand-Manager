using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace RfoGateManager.Aurora;

/// <summary>
/// Client del server 3rd party di Aurora: TCP su 127.0.0.1:1130, ASCII, campi separati da
/// ';' e pacchetti terminati da CR/LF. Va abilitato in Aurora con
/// <c>F7 → Other → 3rd Party Software Access = YES</c>, altrimenti la porta non risponde.
/// </summary>
public sealed class AuroraClient : IAsyncDisposable
{
    public const int DefaultPort = 1130;

    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<AuroraClient> _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Una coda di attese per ogni comando: le risposte tornano nell'ordine in cui sono state chieste.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource<string[]>>> _pending = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    public AuroraClient(ILogger<AuroraClient> log, string host = "127.0.0.1", int port = DefaultPort)
    {
        _log = log;
        _host = host;
        _port = port;
    }

    public bool IsConnected => _tcp?.Connected == true;
    public string? LastError { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>Ultimo callsign che Aurora ha riportato come selezionato.</summary>
    public string? LastSelectedTraffic { get; private set; }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        await DisconnectAsync();

        try
        {
            var tcp = new TcpClient { NoDelay = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(_host, _port, timeout.Token);
            }

            _tcp = tcp;
            _stream = tcp.GetStream();
            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token));

            ConnectedAt = DateTimeOffset.UtcNow;
            LastError = null;
            _log.LogInformation("Aurora connesso su {Host}:{Port}", _host, _port);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex is OperationCanceledException
                ? $"Nessuna risposta su {_host}:{_port}. Aurora è aperto e '3rd Party Software Access' è su YES (F7 → Other)?"
                : ex.Message;
            _log.LogDebug("Connessione ad Aurora fallita: {Error}", LastError);
            await DisconnectAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_readerCts is not null)
        {
            await _readerCts.CancelAsync();
            _readerCts.Dispose();
            _readerCts = null;
        }

        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* chiusura */ }
            _readerTask = null;
        }

        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
        ConnectedAt = null;

        FailAllPending("connessione ad Aurora chiusa");
    }

    // --- Comandi -----------------------------------------------------------------

    /// <summary>Callsign del traffico selezionato in Aurora (<c>#SELTFC</c>).</summary>
    public async Task<string?> GetSelectedTrafficAsync(CancellationToken ct = default)
    {
        var f = await RequestAsync("SELTFC", null, ct);
        var cs = f.Length > 0 ? f[0].Trim() : "";
        LastSelectedTraffic = cs.Length > 0 ? cs : null;
        return LastSelectedTraffic;
    }

    /// <summary>Traffico nel raggio radar impostato in Aurora (<c>#TR</c>).</summary>
    public async Task<IReadOnlyList<string>> GetTrafficInRangeAsync(CancellationToken ct = default)
    {
        var f = await RequestAsync("TR", null, ct);
        return f.Where(x => x.Trim().Length > 0).Select(x => x.Trim()).ToList();
    }

    public async Task<AuroraFlightPlan> GetFlightPlanAsync(string callsign, CancellationToken ct = default)
    {
        var f = await RequestAsync("FP", callsign, ct);
        // La risposta ripete il callsign: lo togliamo prima di mappare i 15 campi.
        var body = DropEcho(f, callsign);
        return AuroraFlightPlan.Parse(callsign, body);
    }

    public async Task<AuroraTrafficPosition> GetTrafficPositionAsync(string callsign, CancellationToken ct = default)
    {
        var f = await RequestAsync("TRPOS", callsign, ct);
        var body = DropEcho(f, callsign);
        return AuroraTrafficPosition.Parse(callsign, body);
    }

    /// <summary>
    /// Assegna lo stand (<c>#LBGTE;CALLSIGN;GATE</c>). È il comando che fa il lavoro vero.
    /// Aurora lo accetta solo sul traffico assunto dalla posizione connessa, altrimenti
    /// risponde <c>Traffic not assumed</c>. L'etichetta compare dopo uno o due secondi.
    /// </summary>
    public async Task AssignGateAsync(string callsign, string gate, CancellationToken ct = default)
    {
        Validate(callsign);
        Validate(gate);
        await RequestAsync("LBGTE", $"{callsign};{gate}", ct);
    }

    /// <summary>
    /// Cancella lo stand assegnato mandando <c>#LBGTE;CALLSIGN;</c> con gate vuoto. Il manuale
    /// non lo documenta; verificato su Aurora vero: la risposta e' immediata, ma l'etichetta
    /// sparisce dopo qualche secondo.
    /// </summary>
    public async Task ClearGateAsync(string callsign, CancellationToken ct = default)
    {
        Validate(callsign);
        await RequestAsync("LBGTE", $"{callsign};", ct);
    }

    public async Task SendPrivateMessageAsync(string callsign, string text, CancellationToken ct = default)
    {
        Validate(callsign);
        await RequestAsync("MSGPM", $"{callsign};{Sanitize(text)}", ct);
    }

    /// <summary>Aeroporti sotto controllo della posizione connessa (<c>#CTRL</c>).</summary>
    public async Task<IReadOnlyList<string>> GetControlledAirportsAsync(CancellationToken ct = default)
    {
        var f = await RequestAsync("CTRL", null, ct);
        return f.Where(x => x.Trim().Length == 4).Select(x => x.Trim().ToUpperInvariant()).ToList();
    }

    /// <summary>Callsign della posizione ATC connessa (<c>#CONN</c>).</summary>
    public async Task<string?> GetConnectedCallsignAsync(CancellationToken ct = default)
    {
        var f = await RequestAsync("CONN", null, ct);
        var cs = f.FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(cs) ? null : cs;
    }

    public async Task<string> GetMetarAsync(string icao, CancellationToken ct = default)
    {
        Validate(icao);
        var f = await RequestAsync("METAR", icao, ct);
        return string.Join(";", f).Trim();
    }

    /// <summary>Zoom + selezione di un traffico in Aurora (<c>#ZSTR</c>): utile per "mostrami questo".</summary>
    public async Task ZoomAndSelectAsync(string callsign, CancellationToken ct = default)
    {
        Validate(callsign);
        await RequestAsync("ZSTR", callsign, ct);
    }

    // --- Trasporto ---------------------------------------------------------------

    private async Task<string[]> RequestAsync(string command, string? args, CancellationToken ct)
    {
        if (!IsConnected && !await ConnectAsync(ct))
            throw new AuroraException(LastError ?? "Aurora non è connesso.");

        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = _pending.GetOrAdd(command, _ => new ConcurrentQueue<TaskCompletionSource<string[]>>());
        queue.Enqueue(tcs);

        var packet = args is null ? $"#{command}\r\n" : $"#{command};{args}\r\n";
        var bytes = Encoding.ASCII.GetBytes(packet);

        await _sendLock.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new AuroraException("Aurora non è connesso.");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(new AuroraException($"Invio di #{command} fallito: {ex.Message}"));
            await DisconnectAsync();
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        }
        catch (TimeoutException)
        {
            throw new AuroraException($"Aurora non ha risposto a #{command} entro 4 secondi.");
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return;

        var buffer = new byte[8192];
        var pendingText = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                pendingText.Append(Encoding.ASCII.GetString(buffer, 0, read));

                while (true)
                {
                    var text = pendingText.ToString();
                    var idx = text.IndexOfAny(['\r', '\n']);
                    if (idx < 0) break;

                    var line = text[..idx].Trim();
                    var rest = text[(idx + 1)..].TrimStart('\r', '\n');
                    pendingText.Clear();
                    pendingText.Append(rest);

                    if (line.Length > 0) Dispatch(line);
                }
            }
        }
        catch (OperationCanceledException) { /* chiusura richiesta */ }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogWarning("Lettura da Aurora interrotta: {Error}", ex.Message);
        }
        finally
        {
            FailAllPending("connessione ad Aurora persa");
        }
    }

    private void Dispatch(string line)
    {
        // Gli errori arrivano come "@ERR;#COMANDO;argomenti...;messaggio" (il manuale parla
        // di '$', ma Aurora manda questo). Il nome del comando ci dice a quale richiesta
        // appartiene l'errore, cosi' non lo attribuiamo alla richiesta sbagliata.
        if (line.StartsWith('$') || line.StartsWith("@ERR", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Aurora ha risposto con un errore: {Line}", line);

            var parts = line.Split(';');
            var failed = parts.Length > 1 && parts[1].StartsWith('#')
                ? parts[1][1..].Trim().ToUpperInvariant()
                : null;
            var message = Explain(parts.Length > 1 ? parts[^1].Trim() : line, parts);

            LastError = message;

            if (failed is not null && _pending.TryGetValue(failed, out var q) && q.TryDequeue(out var target))
                target.TrySetException(new AuroraException(message));
            else
                FailOldestPending(message);
            return;
        }

        if (!line.StartsWith('#')) return;

        var fields = line[1..].Split(';');
        var command = fields[0].Trim().ToUpperInvariant();
        var payload = fields.Skip(1).ToArray();

        // Push asincroni del server (stato intercom): non c'è nessuno in attesa.
        if (command.StartsWith("INTERCOM", StringComparison.Ordinal))
        {
            _log.LogDebug("Intercom: {Line}", line);
            return;
        }

        if (command == "SELTFC")
        {
            var cs = payload.FirstOrDefault()?.Trim();
            LastSelectedTraffic = string.IsNullOrEmpty(cs) ? null : cs;
        }

        if (_pending.TryGetValue(command, out var queue) && queue.TryDequeue(out var tcs))
        {
            tcs.TrySetResult(payload);
        }
        else
        {
            _log.LogDebug("Risposta Aurora senza richiesta in attesa: {Line}", line);
        }
    }

    /// <summary>Traduce gli errori noti di Aurora in qualcosa che dica al controllore cosa fare.</summary>
    private static string Explain(string message, string[] parts)
    {
        // parts: @ERR ; #COMANDO ; CALLSIGN ; ... ; messaggio
        var callsign = parts.Length > 2 ? parts[2].Trim() : "il traffico";

        if (message.Contains("not assumed", StringComparison.OrdinalIgnoreCase))
            return $"Aurora accetta lo stand solo su un traffico assunto da te: assumi {callsign} e riprova.";

        return $"Aurora ha rifiutato il comando: {message}";
    }

    private void FailOldestPending(string error)
    {
        foreach (var queue in _pending.Values)
        {
            if (queue.TryDequeue(out var tcs))
            {
                tcs.TrySetException(new AuroraException(error));
                return;
            }
        }
    }

    private void FailAllPending(string error)
    {
        foreach (var queue in _pending.Values)
        {
            while (queue.TryDequeue(out var tcs))
                tcs.TrySetException(new AuroraException(error));
        }
    }

    /// <summary>Il protocollo usa ';' come separatore: un ';' nei dati spaccherebbe il pacchetto.</summary>
    private static void Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AuroraException("Valore vuoto non ammesso.");
        if (value.Contains(';') || value.Contains('\r') || value.Contains('\n'))
            throw new AuroraException($"Valore non valido per il protocollo Aurora: '{value}'.");
    }

    private static string Sanitize(string text) =>
        text.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string[] DropEcho(string[] fields, string callsign)
    {
        if (fields.Length > 0 && fields[0].Trim().Equals(callsign, StringComparison.OrdinalIgnoreCase))
            return fields.Skip(1).ToArray();
        return fields;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }
}
