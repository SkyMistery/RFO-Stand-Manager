# RFO Gate Manager

Assegnazione degli stand per l'RFO di Napoli (LIRN). Un solo eseguibile: nessun runtime,
nessun installer, nessun permesso di amministratore.

L'app tiene insieme tre sorgenti — le prenotazioni dell'evento, il traffico online di IVAO
e Aurora — calcola quale stand è libero e per quanto, e spinge l'assegnazione dentro Aurora
con un click.

---

## Perché un eseguibile e non un sito

Il server 3rd party di Aurora parla **TCP sulla porta 1130**. Un browser non può aprire una
socket TCP: non è una limitazione aggirabile, è come sono fatti i browser. Quindi una web app
ospitata non potrà mai comandare Aurora.

La soluzione è un eseguibile che fa entrambe le cose: tiene la connessione TCP verso Aurora e
serve la sua interfaccia su `http://127.0.0.1:5057`, che si apre nel browser di sistema. Per
chi lo usa è "scarica una cartella e fai doppio click". In più la `x-key` del booking resta
sul disco e non passa mai dal browser.

---

## Avvio rapido

1. In Aurora: **F7 → Other → 3rd Party Software Access = YES**. Senza questo la porta 1130
   non risponde e l'app resta in sola lettura.
2. Facoltativo: metti in `data\` il file `lirn.gts` del sector file, per le coordinate.
   Gli stand di Napoli ci sono già in `data\stands.LIRN.json`.
3. Apri `secrets\booking.json` e inserisci la `x-key` e la data dell'evento.
4. Doppio click su `RfoGateManager.exe`. Si apre il browser.
5. Clicca sul chip **Aurora non connesso** in alto per agganciare Aurora.

Argomenti da riga di comando: `--no-browser`, `--port=5099`.

---

## Il file degli stand

`data\stands.LIRN.json` contiene i **38 stand di Capodichino**, presi dall'AIP Italia
(AD 2 LIRN 2-11 e 2-12, AIRAC 02 OCT 2025, dati GESAC): apron di appartenenza, lettera di
codice, **apertura alare e lunghezza massima**.

Il file `lirn.gts` del sector file Aurora è **facoltativo**: aggiunge le coordinate, che
servono solo per disegnare il piazzale. Senza, l'assegnazione funziona lo stesso.

```jsonc
{
  "id": "23",
  "maxSize": "C",            // lettera di codice ICAO
  "maxWingspanM": 32,        // limiti AIP: se ci sono, battono la lettera di codice
  "maxLengthM": 37,
  "apron": 1,
  "contact": true,           // con pontile: preferito per i passeggeri  ← da completare
  "uses": ["pax"],           // vuoto = qualsiasi uso; altrimenti pax, cargo, ga, mil
  "airlines": ["AZA"],       // compagnie preferenziali (prefisso ICAO)   ← da completare
  "blocks": ["23L","23R"],   // MARS: occupando questo, quelli diventano inagibili ← da completare
  "priority": 100,           // a parità di punteggio vince il più basso
  "disabled": false
}
```

La relazione `blocks` viene resa simmetrica al caricamento: basta dichiararla da un lato.

**Tre campi restano da compilare a mano**, perché l'AIP non li pubblica: `contact` (quali
stand hanno il pontile), `airlines` e `blocks` (le coppie MARS).

### Perché le misure contano più della lettera di codice

La lettera di codice è troppo grossolana per assegnare uno stand. A Napoli:

- lo stand **23** è codice C ma accetta 32 m di apertura: un **A320** (35,8 m) **non ci entra**;
- lo stand **16** ha 36 m di apertura ma solo 39 m di lunghezza: l'**A321** (44,5 m) non ci sta,
  pur essendo largo quanto un A320 che invece ci sta;
- nessuno stand supera i **61 m** di apertura: **B777-300ER e A350 non hanno posto a Capodichino**,
  e il programma lo dice invece di inventarsi una collocazione.

Quando conosce sia i limiti dello stand sia le misure del tipo, il motore confronta i metri.
Per un tipo che non ha in tabella ricade sulla lettera di codice, che è meno precisa.

---

## Come vengono decisi gli stand

**Le rotazioni contano come una sola occupazione.** Se un aereo atterra alle 14:10 e riparte
alle 15:40, lo stand è impegnato tutto quel tempo, senza buchi. L'abbinamento fra arrivo e
partenza avviene per marche (`REG/` nei remarks) o per callsign, entro una finestra di 8 ore.

Un arrivo di cui non si conosce la ripartenza occupa per la sosta predefinita (90 minuti). Una
partenza senza arrivo abbinato occupa da un'ora prima dell'off-blocks.

**Gli orari.** Per un volo in rotta, l'ora di arrivo viene dalla distanza residua dalla
destinazione diviso la ground speed, più il rullaggio. Per un volo non ancora partito, dalla
partenza prevista più il tempo di volo del piano. Le prenotazioni danno l'orario di chi non è
ancora online.

**L'ordine delle decisioni.** Prima si posa quello che è già deciso — assegnazioni manuali,
poi la posizione reale letta da Aurora, poi le prenotazioni — perché sono vincoli. Il resto
viene assegnato in ordine di arrivo scegliendo, fra gli stand compatibili e liberi, quello con
il punteggio migliore: compagnia di casa, conferma del piano precedente, misura giusta senza
sprecare uno stand grande, pontile per i passeggeri, e gli stand MARS tenuti per ultimi perché
occuparne uno ne brucia altri.

**Quando non trova niente, lo dice.** Nessun volo sparisce in silenzio: la riga riporta quanti
stand erano troppo piccoli, quanti occupati, quanti bloccati da un MARS adiacente.

---

## Stato condiviso fra controllori

Se DEL, GND e TWR usano ciascuno la propria copia dell'app, le assegnazioni divergono e due
posizioni mandano due aerei sullo stesso stand.

In `tools\state.php` c'è un endpoint da caricare su un qualsiasi spazio PHP raggiungibile.
Cambia il token in testa al file, poi su ogni postazione metti in `secrets\booking.json`:

```json
"sharedStateUrl": "https://tuodominio/state.php?event=lirn20260919",
"sharedStateToken": "lo-stesso-token"
```

Da quel momento le assegnazioni manuali, gli stand chiusi e il piano pubblicato sono gli stessi
per tutti. Senza URL l'app lavora da sola e salva lo stato in `state.local.json`.

---

## Cosa parla con cosa

| Sorgente | Cosa dà | Note |
|---|---|---|
| `booking.it.ivao.aero/api/flights/{data}` | prenotazioni dell'evento | serve la `x-key` |
| `api.ivao.aero/v2/tracker/whazzup` | tutti i voli online, posizione e distanza | pubblico, nessuna chiave, ~15 s |
| Aurora TCP `127.0.0.1:1130` | selezione, posizione reale, assegnazione | va abilitato in Aurora |

Comandi Aurora usati: `#SELTFC` (traffico selezionato), `#TRPOS` (campo 17 = stand reale),
`#TR` (traffico in raggio radar), `#LBGTE` (assegna lo stand), `#MSGPM` (avvisa il pilota),
`#ZSTR` (mostra il volo in Aurora).

Whazzup vede tutti i voli del mondo, Aurora solo quelli nel raggio radar: è per questo che la
pianificazione con ore di anticipo si appoggia a Whazzup e non a `#TR`.

---

## Sviluppo

```bash
dotnet build RfoGateManager.slnx     # compila
.\test.ps1                           # esegue i test
.\publish.ps1                        # produce dist\RfoGateManager.exe
```

`dotnet test` non aggancia il runner Microsoft.Testing.Platform su questo SDK: `test.ps1`
lancia direttamente l'eseguibile dei test, che funziona.

```
src/RfoGateManager/
  Domain/     modelli, catalogo aeromobili, rotazioni, motore di allocazione
  Data/       parser .gts, catalogo stand, client booking e Whazzup, configurazione
  Aurora/     client TCP e record del protocollo
  Sync/       documento condiviso
  Services/   orchestrazione e ricalcolo
  wwwroot/    interfaccia, incorporata nell'exe
```

Il motore di allocazione non conosce né HTTP né Aurora: prende stand e richieste, restituisce
assegnazioni con la motivazione. È la parte coperta dai test.
