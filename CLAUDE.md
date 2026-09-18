# CLAUDE.md — RFO Gate Manager

Assegnazione stand per l'RFO di Napoli (LIRN) del **19 settembre 2026**. Il README spiega cosa
fa l'app e perché; questo file raccoglie come lavorarci senza rompere niente.

## Convenzioni

- **Tutto in italiano**: interfaccia, commenti, messaggi di errore, commit, README. Il testo del
  PM al pilota invece è in inglese (lo leggono piloti stranieri).
- I commenti spiegano il **perché**, spesso con il caso reale che ha motivato la regola
  (es. "EJU14MA finiva sull'11"). Mantenere questo stile.
- Commit con messaggio lungo che racconta cosa si è verificato e su quali dati.
- La repo (github.com/SkyMistery/RFO-Stand-Manager) è **pubblica**: mai committare
  `secrets/*.json`, `state.local.json`, `data/*.gts` (tutti in `.gitignore`).

## Comandi

```powershell
dotnet build RfoGateManager.slnx
.\test.ps1                 # test: va lanciato da PowerShell, non da bash
.\publish.ps1              # dist\RfoGateManager.exe, single-file self-contained
```

- `dotnet test` **non funziona** su questo SDK (non aggancia Microsoft.Testing.Platform):
  `test.ps1` lancia direttamente l'eseguibile dei test.
- Per provare l'app: `dotnet run --project src/RfoGateManager/RfoGateManager.csproj --no-build -- --no-browser --port=5099`.
  **Spegnerla prima di ricompilare** (tiene bloccato l'exe): chiudere il processo in ascolto
  sulla 5099 con `Get-NetTCPConnection -LocalPort 5099` + `Stop-Process`. `kill` del job bash
  non basta, e `wait` senza argomenti in bash aspetta anche il server e si blocca.
- Endpoint utili per verificare: `/api/status`, `/api/plan?fresh=true`, `/api/departures`,
  `/api/suggest?key=...`, `POST /api/booking/refresh`, `POST /api/aurora/connect`.

## Trappole note

- **Modifiche ai file**: i heredoc bash si rompono sui file C# grandi. Usare lo strumento di
  scrittura o uno script Python salvato su file. Attenzione alle sequenze di escape negli
  script Python non raw: hanno già prodotto un byte NUL letterale in `StandAllocator.cs` (git
  lo trattava come binario) e dei backspace nel README (`\b` di `secrets\booking`).
  Controllo rapido: `grep -rlaP '[\x00-\x08\x0b\x0c\x0e-\x1f]' src tests docs README.md`.
- **L'interfaccia si aggiorna ogni pochi secondi**: ogni pannello si ridisegna solo se i suoi
  dati sono cambiati (firma JSON in `state.*Sig`). Ridisegnare a vuoto sostituisce i pulsanti
  sotto il mouse e fa perdere i clic, e cancella quello che l'utente sta scrivendo.
- Il pannello del browser integrato a volte non disegna (finestra in secondo piano): usare
  `find` / `javascript_tool` invece degli screenshot.

## Aurora sulla rete vera

L'utente collauda con Aurora connesso a IVAO in rete. **Comandi che scrivono (`#LBGTE`,
`#MSGPM`) solo con il suo ok esplicito, volta per volta**: li vedono altri controllori e i
piloti. I comandi di lettura (`#TR`, `#TRPOS`, `#FP`, `#SELTFC`, `#CONN`) si possono usare.
Differenze dal manuale verificate sul campo: vedi la sezione Aurora del README.

## Decisioni prese con l'utente

- Eseguibile unico, non web app: Aurora parla solo TCP.
- Lo stand **prenotato vince sulle misure** (si assegna e si segnala *fuori misura*); la scelta
  automatica invece le misure le rispetta.
- Chi è **fisicamente fermo** su uno stand vince su tutto: l'arrivo che lo trova occupato viene
  riassegnato, non si fa spostare chi è a terra.
- Ordine di comodità: 10-20, 51-57, 41-46, apron 2, apron 3 (`priority` in
  `data/stands.LIRN.json`). 21, 11, 44, 52 riservati agli aerei grandi; 22 e 23 ai jet privati.
  Riserve morbide, non divieti.
- L'app **propone** gli stand ma non scrive in Aurora da sola: l'assegnazione la manda il
  controllore con *Assegna*.
- Sincronizzazione fra postazioni via **atc.it.ivao.aero** (ASP.NET su Plesk, MySQL), sviluppato
  da un altro agente secondo `docs/SYNC-API.md`.

## Dati reali

- Booking: `GET https://booking.it.ivao.aero/api/flights`, header `x-api-key`, nessuna data nel
  path, risposta array. `type_of_flight` è inaffidabile (direzione dagli ICAO), `gate` può
  valere `TBD`, `booked_by` spesso nullo o 0. Il gate prenotato è il segnale di rotazione più
  forte (il callsign cambia fra andata e ritorno).
- Stand: 38 dall'AIP (`data/stands.LIRN.json`); il `lirn.gts` del settore italiano ne ha 37
  (manca il 67) con gli stessi nomi usati da Aurora.
- Sul booking dell'evento: 172 tratte, 104 occupazioni, 5 conflitti (tutti doppie
  prenotazioni vere nel booking), 0 voli senza stand. Usarli come riferimento dopo ogni
  modifica al motore.

## Lavoro aperto

1. **Server di sincronizzazione**: l'altro agente deve implementare `docs/SYNC-API.md`. Quando
   arrivano URL e chiave: configurarli e provare due copie dell'app in parallelo contro il
   server vero (finora solo contro `FakeSyncServer` nei test).
2. **Coppie MARS e pontili** in `data/stands.LIRN.json`: l'AIP non li pubblica, servono
   dall'utente.
3. **PM al pilota mai provato su Aurora**: non si sa se `#MSGPM` richiede il traffico assunto
   come `#LBGTE`. Serve l'ok dell'utente e un destinatario scelto da lui.
4. **Rifare l'exe** con `publish.ps1` e distribuirlo, con `data\lirn.gts` accanto.
