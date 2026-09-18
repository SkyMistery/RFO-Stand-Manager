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
chi lo usa è "scarica una cartella e fai doppio click". In più la chiave del booking resta
sul disco e non passa mai dal browser.

---

## Avvio rapido

1. In Aurora: **F7 → Other → 3rd Party Software Access = YES**. Senza questo la porta 1130
   non risponde e l'app resta in sola lettura.
2. Facoltativo: metti in `data\` il file `lirn.gts` del sector file, per le coordinate.
   Gli stand di Napoli ci sono già in `data\stands.LIRN.json`.
3. Apri `secrets\booking.json` e inserisci la chiave del booking nel campo `bookingXKey`
   (l'app la manda come header `x-api-key`). Per lavorare con le altre postazioni aggiungi
   anche `sharedStateUrl` e `sharedStateToken`, vedi *Stato condiviso*.
4. Doppio click su `RfoGateManager.exe`. Si apre il browser.
5. Clicca sul chip **Aurora non connesso** in alto per agganciare Aurora. Se in
   `booking.json` il campo `operator` è vuoto, l'app usa come nome il callsign con cui sei
   connesso (per esempio `LIRN_GND`): è quello che le altre postazioni vedono accanto alle
   tue modifiche.

Argomenti da riga di comando: `--no-browser`, `--port=5099`.

### Cosa c'è nella pagina

- **In alto** i chip di stato: Aurora, Whazzup, booking, stand caricati, sincronizzazione.
  Verde va, ambra è una limitazione, rosso non va; passandoci sopra si legge il motivo.
- **Traffico selezionato in Aurora**: lo stand del piano per l'aereo che hai cliccato in
  Aurora, gli stand alternativi, chi l'ha assunto, e i pulsanti *Solo PM* e *Assegna in Aurora*.
- **Partenze**: la strippiera, in ordine di EOBT.
- **Piano stand**: tutte le occupazioni, con gli ultimi cambi di stand in cima e, per ogni
  riga, *Stand ▾*, *Mostra*, *PM* e *Assegna*.
- **Occupazione del piazzale**: la timeline per stand.

Colori delle righe: **rosso** due aerei sullo stesso stand, **azzurro** riassegnato perché il
suo stand è occupato, **ambra** aereo più grande dello stand, accettato perché lo stand era già
deciso.

---

## Il file degli stand

`data\stands.LIRN.json` contiene i **38 stand di Capodichino**, presi dall'AIP Italia
(AD 2 LIRN 2-11 e 2-12, AIRAC 02 OCT 2025, dati GESAC): apron di appartenenza, lettera di
codice, **apertura alare e lunghezza massima**.

Il file `lirn.gts` del sector file Aurora è **facoltativo** ma consigliato: aggiunge le
coordinate, che servono a riconoscere gli aerei parcheggiati quando Aurora non li vede. Senza,
l'assegnazione funziona lo stesso. Non è nella repo, che è pubblica: copialo in `data\` dal
settore italiano (`Include\IT\lirn.gts`). Nel `.gts` attuale manca lo stand 67.

```jsonc
{
  "id": "12",
  "maxSize": "C",            // lettera di codice ICAO
  "maxWingspanM": 36,        // limiti AIP: se ci sono, battono la lettera di codice
  "maxLengthM": 45,
  "apron": 1,
  "priority": 100,           // ordine di comodità: più basso = più comodo (vedi sotto)
  "reservedFor": [],         // "large" o "ga": a chi va di solito (vedi sotto)
  "contact": false,          // con pontile: preferito per i passeggeri  ← da completare
  "uses": [],                // vuoto = qualsiasi uso; altrimenti pax, cargo, ga, mil
  "airlines": [],            // compagnie preferenziali (prefisso ICAO)   ← da completare
  "blocks": [],              // MARS: occupando questo, quelli diventano inagibili ← da completare
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
partenza avviene entro una finestra di 8 ore; come si riconosce è spiegato più sotto.

Un arrivo di cui non si conosce la ripartenza occupa per la sosta predefinita (90 minuti). Una
partenza senza arrivo abbinato occupa da un'ora prima dell'off-blocks.

**Gli orari.** Per un volo in rotta, l'ora di arrivo viene dalla distanza residua dalla
destinazione diviso la ground speed, più il rullaggio. Per un volo non ancora partito, dalla
partenza prevista più il tempo di volo del piano. Le prenotazioni danno l'orario di chi non è
ancora online.

**Come si riconosce una rotazione.** Il segnale migliore non è il callsign: nel booking di un
evento cambia quasi sempre fra andata e ritorno (AAL180 arriva, AAL781 riparte). È **lo stand
prenotato**: il sistema assegna alla coppia lo stesso gate, quindi sta dicendo che è un
turnaround. I segnali sono provati in ordine di affidabilità — marche, stesso stand con lo
stesso tipo, stesso callsign, stesso VID — e vince il più forte. Sui dati veri dell'RFO questo
porta le rotazioni riconosciute da 20 a 68, e i conflitti da 68 a 5.

Il controllo sul tipo si ammorbidisce quando uno dei due non è in catalogo: nelle prenotazioni
capitano refusi (un B738 ripartito come "B378") e rifiutare l'abbinamento per quello produrrebbe
un conflitto inventato al posto di una rotazione reale.

**L'ordine delle decisioni.** Prima si posa quello che è già deciso, perché sono vincoli:
chi è fisicamente fermo su uno stand, poi le assegnazioni fatte a mano, poi le prenotazioni. Il
resto viene assegnato in ordine di arrivo scegliendo, fra gli stand compatibili e liberi, quello
con il punteggio migliore. Il punteggio è a strati, dal più pesante: stand della compagnia,
conferma del piano precedente, stand riservati, ordine di comodità, e solo dentro lo stesso
gruppo lo spazio sprecato, il pontile per i passeggeri e gli stand MARS tenuti per ultimi perché
occuparne uno ne brucia altri.

**Lo stand già deciso vince sulle misure.** Se la prenotazione dice stand 12, l'aereo va sullo
stand 12 anche se non ci sta: la riga diventa ambra con l'etichetta *fuori misura* e il motivo
spiega quale limite è stato sforato. Vale anche per le assegnazioni fatte a mano. La scelta
automatica invece le misure le rispetta: quando è il programma a decidere, non forza nulla.

Ambra e rosso vogliono dire cose diverse: **ambra** è un aereo più grande dello stand, accettato
per decisione altrui; **rosso** è un conflitto vero, due aerei sullo stesso stand nella stessa
finestra, e va risolto.

**Quando non trova niente, lo dice.** Nessun volo sparisce in silenzio: la riga riporta quanti
stand erano troppo piccoli, quanti occupati, quanti bloccati da un MARS adiacente.

### Il piazzale vero comanda sul piano

Ogni 15 secondi l'app ricalcola il piano e guarda chi è **fermo** su quale stand. La fonte principale è Aurora:
il campo 17 di `#TRPOS` è lo stand che Aurora stesso calcola sul suo file dei gate, e si svuota
appena l'aereo fa pushback. Quando Aurora non c'è o non vede l'aereo, fa da riserva Whazzup:
dalla posizione si ricava lo stand più vicino con le coordinate di `lirn.gts`, entro 40 metri.
Sui dati veri gli aerei parcheggiati stavano fra 3 e 19 metri dal punto dello stand, e uno appena
spinto indietro a 64: la soglia distingue le due cose. Conta solo chi è entro 3 km da Capodichino,
perché i nomi degli stand si ripetono fra aeroporti.

Chi è fisicamente su uno stand passa davanti a tutto, anche alle prenotazioni e alle scelte a
mano. Se lo stand di un arrivo è occupato da un aereo che è lì adesso, **all'arrivo ne viene dato
un altro** invece di far spostare chi è a terra. La riga diventa azzurra con *riassegnato: 14
occupato*; se lo stand perso era stato fissato a mano compare anche *da ricomunicare*, perché
il pilota probabilmente lo sapeva già. Il sostituto rispetta le misure: la regola "lo stand
deciso vince sulle misure" valeva per quello stand, non per quello che sceglie il programma.

Un aereo fermo che non è nel piano diventa un occupante *non programmato*. Non sappiamo quando
ripartirà, quindi tiene lo stand per una finestra di 60 minuti che scorre col tempo
(`unscheduledOccupancyMinutes`): chi arriva entro quella finestra viene dirottato, chi arriva
più tardi tiene il suo stand, perché magari l'intruso se ne sarà andato.

Due prenotazioni che si sovrappongono restano invece un **conflitto**, in rosso: lì non c'è
ancora nessuno a terra e decide il controllore.

Il piano appena calcolato fa da ancora per il successivo, così gli ETA che cambiano a ogni
aggiornamento di Whazzup non fanno saltare le scelte automatiche da uno stand all'altro: sui dati
veri, quattro ricalcoli di fila senza nessun cambio. Quando uno stand cambia davvero compare in
cima al piano, nell'elenco *Ultimi cambi di stand*, e con un avviso.

### Ordine di comodità e stand suggeriti

Il campo `priority` di `stands.LIRN.json` è l'ordine di comodità: prima gli stand **10-20**, poi
**51-57**, poi **41-46** (tutti apron 1), poi l'**apron 2**, poi l'**apron 3**. Un gruppo si riempie
prima di passare al successivo; dentro il gruppo vince lo stand che spreca meno spazio. Per
cambiare l'ordine basta cambiare i numeri nel file.

Alcuni stand vanno di solito a un certo traffico (`reservedFor`). Non è un divieto: chi non
corrisponde li usa solo quando è pieno tutto il resto, apron 3 compreso.

- **22 e 23: jet privati.** Un business jet ci va prima di qualsiasi altro stand. Si riconosce
  dal tipo (Citation, Challenger, Gulfstream, Falcon, Phenom, PC-12, TBM...) o dal callsign
  quando è una marca e non un volo di compagnia (`N900FZ`, `IABCD`, `D-IABC`).
- **11, 21, 44, 52: gli unici da 61 m, per gli aerei grandi.** I narrowbody li lasciano liberi:
  nel booking vero EJU14MA, un A320 senza stand, prima finiva sull'11 e ora sul 56. Fra gli aerei
  grandi resta il solito ordine, e lo stand riservato vince solo a parità di tutto il resto: un
  787 va sul 21 prima che sull'11, ma un 757 (38 m) resta sul 13 invece di prendere un 61 m.

Quando assegni, **Stand ▾** sulla riga apre gli stand adatti a quell'aereo, liberi, nello
stesso ordine in cui li sceglierebbe il programma, con lo stand attuale evidenziato e gli
occupati (con chi li occupa) dietro "mostra anche". Un clic fissa lo stand per tutte le
postazioni; "Assegna" poi lo manda in Aurora. Gli stessi suggerimenti compaiono nel riquadro del
traffico selezionato, dove il clic riempie la casella dello stand.

**Parametri regolabili durante l'evento** senza ricompilare, via `GET`/`POST /api/options`:
margine fra un occupante e il successivo (5 minuti, quanto basta per come il booking impacchetta
gli slot), sosta predefinita, rullaggio, finestra di rotazione.

---

## Stato condiviso fra controllori

Se DEL, GND e TWR usano ciascuno la propria copia dell'app, le decisioni devono essere le
stesse per tutti: stand fissati a mano, stand chiusi, piloti avvisati, partenze che hanno
chiamato. Le postazioni condividono un documento per evento su un server che fa da ponte
(atc.it.ivao.aero). Il contratto è in [`docs/SYNC-API.md`](docs/SYNC-API.md), con schema MySQL
e un'implementazione di riferimento in ASP.NET.

In breve: ogni 3 secondi l'app chiede se è cambiato qualcosa (se no, `304` senza corpo); le
scritture sono condizionate alla versione (`If-Match`), e se due postazioni scrivono nello
stesso istante la seconda riceve `409`, riapplica la sua modifica sulla versione nuova e
riprova. Le modifiche si sommano invece di cancellarsi a vicenda.

In `secrets\booking.json` di ogni postazione:

```json
"sharedStateUrl": "https://atc.it.ivao.aero/api/rfo/events/lirn-20260919/state",
"sharedStateToken": "<chiave>"
```

Senza URL l'app lavora da sola e salva lo stato in `state.local.json`, nello stesso formato:
alla prima connessione al server porta su le decisioni prese fin lì.

---

## Messaggio al pilota

Il pulsante **PM** su ogni riga (e **Solo PM** nel riquadro del traffico selezionato) manda al
pilota un messaggio privato da Aurora con lo stand da aspettarsi. Prima dell'invio l'app
mostra il testo esatto e chiede conferma. Il modello si cambia in `secrets\booking.json`:

```json
"pilotMessageTemplate": "{callsign}, expect stand {stand} on arrival at Naples. Welcome to the RFO!"
```

Chi è stato avvisato viene scritto nello stato condiviso: la riga mostra *pilota avvisato*, e
se poi lo stand cambia diventa *da ricomunicare (detto 14)*, su tutte le postazioni.

---

## Strippiera partenze

Le partenze da Napoli in ordine di EOBT, divise in **Da chiamare** e **Hanno chiamato**. Ogni
strip mostra stand, destinazione, se il pilota è connesso, chi l'ha assunto e quanto manca
all'EOBT (in rosso se è passato).

"Ha chiamato" l'app non lo può sentire, quindi lo deduce: un traffico assunto in Aurora ha
chiamato, perché DEL o GND lo assumono quando il pilota si fa vivo. Il pulsante sposta la
strip a mano, e la scelta manuale vince sulla deduzione in tutti e due i sensi ed è condivisa
fra le postazioni; **auto** la toglie e torna alla deduzione. Chi è già in volo esce dalla
strippiera.

---

## Cosa parla con cosa

| Sorgente | Cosa dà | Note |
|---|---|---|
| `booking.it.ivao.aero/api/flights` | prenotazioni dell'evento | header `x-api-key`, nessuna data nel path |
| `api.ivao.aero/v2/tracker/whazzup` | tutti i voli online, posizione e distanza | pubblico, nessuna chiave, ~15 s |
| Aurora TCP `127.0.0.1:1130` | selezione, posizione reale, assegnazione | va abilitato in Aurora |

**Aurora accetta lo stand solo su un traffico assunto da te.** Su un traffico non assunto
`#LBGTE` viene rifiutato con `Traffic not assumed` e non scrive nulla. Quando selezioni un aereo
l'app legge chi l'ha assunto (`#TRPOS` campo 12) e te lo dice prima che tu provi ad assegnare.
Per questo all'evento lo stand lo assegna chi ha il traffico in carico in quel momento.

Collaudato fino in fondo su Aurora vero: lo stand scritto con `#LBGTE` ricompare nel campo 21
di `#TRPOS` dopo uno o due secondi. Mandando il gate vuoto (`#LBGTE;CALLSIGN;`) l'etichetta si
cancella: il manuale non lo dice, l'app lo usa in `POST /api/aurora/clear`.

Il manuale sbaglia in quattro punti: `#CONN` e `#CTRLRWY` rispondono
col proprio nome e non con `#CTRL`; nel piano di volo (`#FP`) i campi 7 e 8 sono invertiti (prima
le regole, poi il tipo); gli errori arrivano come `@ERR;#COMANDO;argomenti;messaggio` e non con `$`.

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

Altri documenti: [`docs/SYNC-API.md`](docs/SYNC-API.md) è il contratto del server di
sincronizzazione; [`CLAUDE.md`](CLAUDE.md) raccoglie convenzioni, trappole note e lavoro
aperto per chi riprende il progetto.
