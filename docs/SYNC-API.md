# Contratto di sincronizzazione — RFO Gate Manager

Più postazioni ATC dello stesso evento (DEL, GND, TWR, APP) usano ciascuna la propria copia
del Gate Manager. Per vedere le stesse decisioni — stand fissati a mano, stand chiusi, piloti
avvisati, partenze che hanno chiamato — condividono **un documento JSON per evento**, tenuto
da un server che fa da ponte.

Il server **non interpreta** il contenuto: conserva un oggetto JSON opaco e gli dà una
versione. Tutta la logica sta nell'app. Questo tiene il server piccolo e fa sì che nuove
funzioni dell'app non richiedano modifiche lato server.

Il comportamento atteso è anche scritto in forma eseguibile: `FakeSyncServer` in
[`tests/RfoGateManager.Tests/SharedStateTests.cs`](../tests/RfoGateManager.Tests/SharedStateTests.cs).
Un server che si comporta come quello è compatibile.

---

## Endpoint

```
GET  /api/rfo/events/{eventId}/state
PUT  /api/rfo/events/{eventId}/state
```

`eventId`: `[a-z0-9-]{1,64}`, per esempio `lirn-20260919`. Qualsiasi altro valore → `400`.

L'app è configurata con l'URL completo, quindi il percorso può cambiare senza toccare il codice.

### Autenticazione

Header `x-api-key: <chiave>`. Chiave assente o sbagliata → `401` senza corpo.

Le chiavi stanno nella configurazione del server (non nel codice, non nella repo). Basta una
chiave per evento o una chiave globale; se una chiave è legata a certi eventi, usarla su un
altro evento → `403`.

Le chiamate arrivano da un'applicazione desktop, non da un browser: **CORS non serve**.
Solo HTTPS.

---

## Il documento

Il server risponde sempre con questa busta:

```json
{
  "version": 12,
  "updatedAt": "2026-09-19T09:41:07.512Z",
  "updatedBy": "LIRN_GND",
  "data": { "...": "contenuto dell'app, opaco per il server" }
}
```

| Campo | Chi lo decide | Note |
|---|---|---|
| `version` | **il server** | intero, parte da 1, +1 a ogni scrittura riuscita |
| `updatedAt` | il server | UTC, ISO 8601 |
| `updatedBy` | il client | lo manda nel corpo del PUT; il server lo salva così com'è (max 64 caratteri) |
| `data` | il client | oggetto JSON; il server lo salva e lo restituisce senza modificarlo |

Ogni risposta con busta porta anche l'header `ETag: "<version>"` (tra virgolette).

---

## GET — leggere

| Situazione | Risposta |
|---|---|
| Documento mai scritto per questo evento | `404`, corpo vuoto |
| `If-None-Match: "<v>"` e la versione è ancora `v` | `304`, corpo vuoto |
| Altrimenti | `200`, busta, `ETag` |

Ogni postazione chiede ogni **3 secondi** con `If-None-Match`. Finché non cambia niente la
risposta è un `304` senza corpo, quindi il carico è trascurabile: dieci postazioni fanno
circa tre richieste al secondo, quasi tutte vuote.

## PUT — scrivere

Corpo:

```json
{ "updatedBy": "LIRN_GND", "data": { "...": "..." } }
```

Header **obbligatorio** `If-Match: "<versione su cui il client ha basato la modifica>"`.
Per creare il documento il client manda `If-Match: "0"`.

| Situazione | Risposta |
|---|---|
| `If-Match` assente | `428` |
| Documento inesistente e `If-Match` diverso da `"0"` | `404` (il client riparte da zero e lo ricrea) |
| `data` assente o non è un oggetto | `422` |
| Corpo oltre 1 MB | `413` |
| `If-Match` diverso dalla versione attuale (qualcun altro ha scritto nel frattempo) | **`409`, con la busta attuale** e `ETag` |
| `If-Match` uguale alla versione attuale | `200`, busta nuova (versione +1), `ETag` |

**Il 409 deve contenere la busta attuale.** Il client la usa per riapplicare la propria
modifica sulla versione giusta e riprovare, senza una lettura in più. È questo che permette a
due postazioni che scrivono nello stesso secondo di sommare le modifiche invece di cancellarsi
a vicenda.

**Il controllo e la scrittura devono essere atomici.** Non "leggo la versione, poi aggiorno",
ma un'unica istruzione condizionata (vedi sotto). Altrimenti due PUT simultanei passano
entrambi il controllo e uno dei due si perde.

---

## MySQL

```sql
CREATE TABLE rfo_shared_state (
  event_id    VARCHAR(64)  NOT NULL PRIMARY KEY,
  version     BIGINT       NOT NULL,
  data        LONGTEXT     NOT NULL,          -- JSON opaco
  updated_by  VARCHAR(64)  NULL,
  updated_at  DATETIME(3)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

Scrittura condizionata:

```sql
-- If-Match: "0" → creazione. Se la riga esiste già, qualcuno è arrivato prima: 409.
INSERT INTO rfo_shared_state (event_id, version, data, updated_by, updated_at)
VALUES (@id, 1, @data, @by, UTC_TIMESTAMP(3));
-- errore 1062 (chiave duplicata) → rispondere 409 con la busta attuale

-- If-Match: "N" con N > 0 → aggiornamento solo se la versione è ancora N.
UPDATE rfo_shared_state
   SET version = version + 1, data = @data, updated_by = @by, updated_at = UTC_TIMESTAMP(3)
 WHERE event_id = @id AND version = @expected;
-- 0 righe toccate → 409 con la busta attuale
```

Facoltativo ma utile per il debriefing dopo l'evento: una tabella `rfo_shared_state_history`
con una riga per ogni scrittura riuscita (`event_id`, `version`, `data`, `updated_by`,
`updated_at`).

---

## Implementazione di riferimento (ASP.NET Core, minimal API)

Da adattare allo stile del sito (controller, Dapper, EF Core...). Usa `MySqlConnector`.

```csharp
app.MapGet("/api/rfo/events/{eventId}/state", async (string eventId, HttpContext ctx, MySqlDataSource db) =>
{
    if (!ValidEvent(eventId)) return Results.BadRequest();
    if (!Authorized(ctx, eventId)) return Results.Unauthorized();

    var current = await Load(db, eventId);
    if (current is null) return Results.NotFound();

    if (ctx.Request.Headers.IfNoneMatch.ToString() == Tag(current.Version))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    return Envelope(ctx, current, StatusCodes.Status200OK);
});

app.MapPut("/api/rfo/events/{eventId}/state", async (string eventId, HttpContext ctx, MySqlDataSource db) =>
{
    if (!ValidEvent(eventId)) return Results.BadRequest();
    if (!Authorized(ctx, eventId)) return Results.Unauthorized();

    var ifMatch = ctx.Request.Headers.IfMatch.ToString();
    if (string.IsNullOrEmpty(ifMatch)) return Results.StatusCode(StatusCodes.Status428PreconditionRequired);
    if (!long.TryParse(ifMatch.Trim('"'), out var expected)) return Results.BadRequest();

    if (ctx.Request.ContentLength > 1_048_576) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    var body = await JsonNode.ParseAsync(ctx.Request.Body);
    if (body?["data"] is not JsonObject data) return Results.UnprocessableEntity();
    var by = body["updatedBy"]?.GetValue<string>();
    if (by is { Length: > 64 }) by = by[..64];

    await using var conn = await db.OpenConnectionAsync();
    int written;
    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = expected == 0
            ? "INSERT INTO rfo_shared_state (event_id, version, data, updated_by, updated_at) VALUES (@id, 1, @data, @by, UTC_TIMESTAMP(3))"
            : "UPDATE rfo_shared_state SET version = version + 1, data = @data, updated_by = @by, updated_at = UTC_TIMESTAMP(3) WHERE event_id = @id AND version = @expected";
        cmd.Parameters.AddWithValue("@id", eventId);
        cmd.Parameters.AddWithValue("@data", data.ToJsonString());
        cmd.Parameters.AddWithValue("@by", (object?)by ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@expected", expected);
        written = await cmd.ExecuteNonQueryAsync();
    }
    catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
    {
        written = 0;
    }

    var current = await Load(db, eventId);

    // Il client credeva di aggiornare una versione che qui non esiste (database svuotato,
    // evento nuovo): 404, e il client ricrea il documento partendo da If-Match "0".
    if (current is null) return Results.NotFound();

    return Envelope(ctx, current, written == 1 ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
});

static string Tag(long version) => $"\"{version}\"";

static IResult Envelope(HttpContext ctx, StateRow row, int status)
{
    ctx.Response.Headers.ETag = Tag(row.Version);
    return Results.Json(new
    {
        version = row.Version,
        updatedAt = row.UpdatedAt,
        updatedBy = row.UpdatedBy,
        data = JsonNode.Parse(row.Data),
    }, statusCode: status);
}

// Load: SELECT version, data, updated_by, updated_at FROM rfo_shared_state WHERE event_id = @id
// ValidEvent: regex ^[a-z0-9-]{1,64}$
// Authorized: confronto a tempo costante di x-api-key con le chiavi in configurazione
record StateRow(long Version, string Data, string? UpdatedBy, DateTime UpdatedAt);
```

---

## Come verificare che funziona

Con la chiave in `$KEY` e l'URL in `$URL`:

```bash
# 1. Documento mai scritto → 404
curl -i -H "x-api-key: $KEY" "$URL"

# 2. Creazione → 200, "version": 1, ETag: "1"
curl -i -X PUT -H "x-api-key: $KEY" -H 'If-Match: "0"' -H "Content-Type: application/json" \
     -d '{"updatedBy":"TEST","data":{"pins":{"A":"14"}}}' "$URL"

# 3. Niente di nuovo → 304
curl -i -H "x-api-key: $KEY" -H 'If-None-Match: "1"' "$URL"

# 4. Scrittura su versione vecchia → 409 con la busta alla versione 1
curl -i -X PUT -H "x-api-key: $KEY" -H 'If-Match: "0"' -H "Content-Type: application/json" \
     -d '{"updatedBy":"TEST","data":{"pins":{"B":"22"}}}' "$URL"

# 5. Senza If-Match → 428.  Senza chiave → 401.
```

La prova che conta di più è la concorrenza: due PUT con lo stesso `If-Match` lanciati nello
stesso istante devono dare **uno 200 e uno 409**, mai due 200.

Dal lato dell'app: in `secrets/booking.json` di ogni postazione

```json
"sharedStateUrl": "https://atc.it.ivao.aero/api/rfo/events/lirn-20260919/state",
"sharedStateToken": "<chiave>"
```

---

## Cosa succede se il server non risponde

L'app continua a funzionare da sola: la decisione del controllore viene applicata in locale e
parte con la prossima scrittura riuscita. Se però nel frattempo un'altra postazione ha scritto,
alla prima lettura vince la versione del server e la decisione presa offline va rifatta. Il chip
in alto nell'app diventa rosso quando la sincronizzazione non va.
