<?php
/**
 * Stato condiviso del Gate Manager.
 *
 * Caricalo su un qualsiasi spazio PHP raggiungibile dai controllori, poi metti il suo URL
 * in secrets/booking.json (campo "sharedStateUrl") su ogni postazione, con lo stesso token.
 *
 *   GET  -> restituisce il documento JSON (404 se non esiste ancora)
 *   PUT  -> lo sostituisce
 *
 * Accetta solo un documento con la forma attesa e scrive in modo atomico, così due
 * controllori che salvano nello stesso istante non lasciano un file a metà.
 */

declare(strict_types=1);

const TOKEN     = 'CAMBIA-QUESTO-TOKEN';
const STATE_DIR = __DIR__ . '/state';
const MAX_BYTES = 1048576; // 1 MB: il documento è piccolo, oltre è un errore

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

function fail(int $status, string $message): never
{
    http_response_code($status);
    echo json_encode(['error' => $message], JSON_UNESCAPED_UNICODE);
    exit;
}

// --- Autenticazione ---------------------------------------------------------

$token = $_SERVER['HTTP_X_TOKEN'] ?? '';
if (!hash_equals(TOKEN, $token)) {
    fail(401, 'Token mancante o errato.');
}

// Un evento per file: ?event=lirn20260919 tiene separate giornate diverse.
$event = preg_replace('/[^a-z0-9_-]/i', '', $_GET['event'] ?? 'default');
if ($event === '') {
    $event = 'default';
}

if (!is_dir(STATE_DIR) && !mkdir(STATE_DIR, 0775, true) && !is_dir(STATE_DIR)) {
    fail(500, 'Cartella di stato non creabile.');
}

$file = STATE_DIR . '/' . $event . '.json';

// --- Lettura ----------------------------------------------------------------

$method = $_SERVER['REQUEST_METHOD'] ?? 'GET';

if ($method === 'GET') {
    if (!is_file($file)) {
        fail(404, 'Nessuno stato salvato per questo evento.');
    }

    $body = file_get_contents($file);
    if ($body === false) {
        fail(500, 'Stato non leggibile.');
    }

    echo $body;
    exit;
}

// --- Scrittura --------------------------------------------------------------

if ($method !== 'PUT') {
    header('Allow: GET, PUT');
    fail(405, 'Metodo non ammesso.');
}

$raw = file_get_contents('php://input');
if ($raw === false || $raw === '') {
    fail(400, 'Corpo della richiesta vuoto.');
}

if (strlen($raw) > MAX_BYTES) {
    fail(413, 'Documento troppo grande.');
}

$doc = json_decode($raw, true);
if (!is_array($doc)) {
    fail(400, 'Corpo non è JSON valido.');
}

// Controllo di forma: teniamo fuori qualsiasi cosa non sia il nostro documento.
foreach (['version' => 'integer', 'pins' => 'array', 'plan' => 'array', 'closedStands' => 'array'] as $key => $type) {
    if (!array_key_exists($key, $doc) || gettype($doc[$key]) !== $type) {
        fail(422, "Campo '$key' mancante o del tipo sbagliato.");
    }
}

// Scrittura atomica: si scrive di fianco e si rinomina, mai direttamente sul file buono.
$tmp = $file . '.' . bin2hex(random_bytes(6)) . '.tmp';
if (file_put_contents($tmp, $raw, LOCK_EX) === false) {
    fail(500, 'Stato non salvabile.');
}

if (!rename($tmp, $file)) {
    @unlink($tmp);
    fail(500, 'Stato non sostituibile.');
}

echo json_encode([
    'ok'        => true,
    'version'   => $doc['version'],
    'updatedAt' => gmdate('c'),
], JSON_UNESCAPED_UNICODE);
