<?php
/**
 * local-ai/relay.php
 *
 * Ponte de mensagens entre o celular (fora do Wi-Fi) e o agente da Mike Local
 * rodando no PC do dono. Hostinger e so PHP (sem WebSocket), entao a ponte e
 * uma fila em arquivos por CODIGO de pareamento, com autenticacao por SECRET
 * que so o agente conhece (o celular so conhece o CODIGO).
 *
 * Fluxo:
 *   1) O agente gera {code, secret} localmente e chama action=register para
 *      avisar que esta online com aquele secret.
 *   2) O celular (que recebeu o CODIGO por QR/digitacao) chama action=send
 *      para enfileirar uma mensagem para o agente.
 *   3) O agente faz polling com action=poll (autenticado por secret) e pega
 *      as mensagens novas.
 *   4) O agente responde com action=reply (autenticado por secret).
 *   5) O celular busca a resposta com action=recv.
 *   6) Qualquer lado pode checar action=status para saber se o agente esta
 *      online (visto ha menos de 20s).
 *
 * Todas as mensagens e metadados de um codigo somem sozinhos depois de 1h
 * sem atividade. Nada e guardado em banco de dados; e so filesystem com
 * .htaccess bloqueando acesso direto aos arquivos.
 */

declare(strict_types=1);

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

const RELAY_DATA_DIR = __DIR__ . '/mike-relay-data';
const RELAY_ONLINE_WINDOW_SEC = 20;
const RELAY_EXPIRE_SEC = 3600;
const RELAY_CODE_PATTERN = '/^[A-Z2-9]{8}$/';
const RELAY_MAX_TEXT_LEN = 8000;
const RELAY_MAX_QUEUE = 200;

function relay_fail(string $message, int $httpCode = 400): void
{
    http_response_code($httpCode);
    echo json_encode(['ok' => false, 'error' => $message], JSON_UNESCAPED_UNICODE);
    exit;
}

function relay_ensure_base_dir(): void
{
    if (!is_dir(RELAY_DATA_DIR)) {
        mkdir(RELAY_DATA_DIR, 0755, true);
    }
    $htaccess = RELAY_DATA_DIR . '/.htaccess';
    if (!is_file($htaccess)) {
        file_put_contents($htaccess, "Require all denied\nDeny from all\n");
    }
}

function relay_code_dir(string $code): string
{
    return RELAY_DATA_DIR . '/' . $code;
}

/** Faxina leve: apaga mensagens e pastas de codigo sem atividade ha muito tempo. */
function relay_gc(): void
{
    $dirs = @scandir(RELAY_DATA_DIR);
    if ($dirs === false) {
        return;
    }
    $now = time();
    foreach ($dirs as $entry) {
        if ($entry === '.' || $entry === '..' || $entry === '.htaccess') {
            continue;
        }
        $dir = RELAY_DATA_DIR . '/' . $entry;
        if (!is_dir($dir) || !preg_match(RELAY_CODE_PATTERN, $entry)) {
            continue;
        }
        $metaPath = $dir . '/meta.json';
        $lastSeen = is_file($metaPath) ? (int)(json_decode((string)file_get_contents($metaPath), true)['last_seen'] ?? 0) : 0;
        if ($lastSeen > 0 && ($now - $lastSeen) > RELAY_EXPIRE_SEC) {
            relay_rrmdir($dir);
            continue;
        }
        foreach (['inbox', 'outbox'] as $sub) {
            $subDir = $dir . '/' . $sub;
            if (!is_dir($subDir)) {
                continue;
            }
            foreach ((array)@scandir($subDir) as $file) {
                if ($file === '.' || $file === '..') {
                    continue;
                }
                $path = $subDir . '/' . $file;
                if (is_file($path) && ($now - (int)filemtime($path)) > RELAY_EXPIRE_SEC) {
                    @unlink($path);
                }
            }
        }
    }
}

function relay_rrmdir(string $dir): void
{
    foreach ((array)@scandir($dir) as $file) {
        if ($file === '.' || $file === '..') {
            continue;
        }
        $path = $dir . '/' . $file;
        is_dir($path) ? relay_rrmdir($path) : @unlink($path);
    }
    @rmdir($dir);
}

function relay_read_meta(string $code): ?array
{
    $path = relay_code_dir($code) . '/meta.json';
    if (!is_file($path)) {
        return null;
    }
    $data = json_decode((string)file_get_contents($path), true);
    return is_array($data) ? $data : null;
}

function relay_write_meta(string $code, array $meta): void
{
    file_put_contents(relay_code_dir($code) . '/meta.json', json_encode($meta, JSON_UNESCAPED_UNICODE), LOCK_EX);
}

function relay_text_length(string $text): int
{
    return function_exists('mb_strlen') ? mb_strlen($text, 'UTF-8') : strlen($text);
}

function relay_text_limit(string $text, int $limit): string
{
    if (relay_text_length($text) <= $limit) {
        return $text;
    }
    return function_exists('mb_substr') ? mb_substr($text, 0, $limit, 'UTF-8') : substr($text, 0, $limit);
}

/** Autentica o agente comparando o hash do secret recebido, sem timing leak. */
function relay_require_secret(string $code, string $secret): array
{
    $meta = relay_read_meta($code);
    if (!$meta || empty($meta['secret_hash'])) {
        relay_fail('codigo nao registrado', 404);
    }
    if ($secret === '' || !hash_equals((string)$meta['secret_hash'], hash('sha256', $secret))) {
        relay_fail('secret invalido', 401);
    }
    return $meta;
}

/** Autentica o celular sem persistir sua chave em texto claro no servidor. */
function relay_require_client_key(string $code, string $clientKey): array
{
    $meta = relay_read_meta($code);
    if (!$meta) {
        relay_fail('codigo nao registrado', 404);
    }
    $expected = (string)($meta['client_key_hash'] ?? '');
    // Registros anteriores continuam acessiveis ate o PC registrar novamente.
    // Todo registro novo da Mike envia a chave e passa a exigir autenticacao.
    if ($expected !== '' && ($clientKey === '' || !hash_equals($expected, hash('sha256', $clientKey)))) {
        relay_fail('pareamento do celular invalido', 401);
    }
    return $meta;
}

function relay_queue_push(string $code, string $sub, array $item): string
{
    $dir = relay_code_dir($code) . '/' . $sub;
    if (!is_dir($dir)) {
        mkdir($dir, 0755, true);
    }
    $existing = glob($dir . '/*.json') ?: [];
    if (count($existing) >= RELAY_MAX_QUEUE) {
        relay_fail('fila cheia, tente novamente em instantes', 429);
    }
    $id = bin2hex(random_bytes(8));
    $item['id'] = $id;
    $item['at'] = microtime(true);
    file_put_contents($dir . '/' . sprintf('%020.6f', $item['at']) . '-' . $id . '.json', json_encode($item, JSON_UNESCAPED_UNICODE), LOCK_EX);
    return $id;
}

/** Le e remove as mensagens mais antigas da fila (fila = "pull" destrutivo). */
function relay_queue_pop_all(string $code, string $sub, int $limit = 50): array
{
    $dir = relay_code_dir($code) . '/' . $sub;
    if (!is_dir($dir)) {
        return [];
    }
    $files = glob($dir . '/*.json') ?: [];
    sort($files);
    $out = [];
    foreach (array_slice($files, 0, $limit) as $file) {
        $data = json_decode((string)file_get_contents($file), true);
        @unlink($file);
        if (is_array($data)) {
            $out[] = $data;
        }
    }
    return $out;
}

/** Le mensagens sem remover, a partir de um cursor (usado pelo celular no recv). */
function relay_queue_peek_since(string $code, string $sub, float $sinceAt, int $limit = 50): array
{
    $dir = relay_code_dir($code) . '/' . $sub;
    if (!is_dir($dir)) {
        return [];
    }
    $files = glob($dir . '/*.json') ?: [];
    sort($files);
    $out = [];
    foreach ($files as $file) {
        $data = json_decode((string)file_get_contents($file), true);
        if (!is_array($data) || (float)($data['at'] ?? 0) <= $sinceAt) {
            continue;
        }
        $out[] = $data;
        if (count($out) >= $limit) {
            break;
        }
    }
    return $out;
}

relay_ensure_base_dir();
if (mt_rand(1, 20) === 1) {
    relay_gc();
}

$method = $_SERVER['REQUEST_METHOD'] ?? 'GET';
$input = [];
if ($method === 'POST') {
    $raw = file_get_contents('php://input');
    $decoded = json_decode((string)$raw, true);
    if (is_array($decoded)) {
        $input = $decoded;
    }
}
$action = (string)($input['action'] ?? $_GET['action'] ?? '');
$code = strtoupper(trim((string)($input['code'] ?? $_GET['code'] ?? '')));

if ($action === '') {
    relay_fail('parametro action obrigatorio');
}
if (!preg_match(RELAY_CODE_PATTERN, $code)) {
    relay_fail('codigo invalido (8 caracteres A-Z2-9)');
}

switch ($action) {
    case 'register': {
        $secret = (string)($input['secret'] ?? '');
        $clientKey = (string)($input['client_key'] ?? '');
        if (strlen($secret) < 16) {
            relay_fail('secret muito curto');
        }
        if (strlen($clientKey) < 24) {
            relay_fail('chave do celular muito curta');
        }
        $dir = relay_code_dir($code);
        if (!is_dir($dir)) {
            mkdir($dir, 0755, true);
        }
        relay_write_meta($code, [
            'secret_hash' => hash('sha256', $secret),
            'client_key_hash' => hash('sha256', $clientKey),
            'created_at' => (int)(relay_read_meta($code)['created_at'] ?? time()),
            'last_seen' => time(),
        ]);
        echo json_encode(['ok' => true], JSON_UNESCAPED_UNICODE);
        break;
    }

    case 'unregister': {
        $secret = (string)($input['secret'] ?? '');
        relay_require_secret($code, $secret);
        relay_rrmdir(relay_code_dir($code));
        echo json_encode(['ok' => true, 'registered' => false], JSON_UNESCAPED_UNICODE);
        break;
    }

    case 'status': {
        $meta = relay_read_meta($code);
        if ($meta) {
            relay_require_client_key($code, (string)($input['client_key'] ?? ''));
        }
        $online = $meta && (time() - (int)($meta['last_seen'] ?? 0)) <= RELAY_ONLINE_WINDOW_SEC;
        echo json_encode(['ok' => true, 'registered' => (bool)$meta, 'online' => $online], JSON_UNESCAPED_UNICODE);
        break;
    }

    case 'send': {
        // Celular -> agente. Exige a chave entregue apenas no QR/pareamento.
        relay_require_client_key($code, (string)($input['client_key'] ?? ''));
        $text = trim((string)($input['text'] ?? ''));
        if ($text === '') {
            relay_fail('mensagem vazia');
        }
        $text = relay_text_limit($text, RELAY_MAX_TEXT_LEN);
        if (!relay_read_meta($code)) {
            relay_fail('codigo nao registrado (agente offline pela primeira vez?)', 404);
        }
        $id = relay_queue_push($code, 'inbox', ['role' => 'user', 'text' => $text]);
        echo json_encode(['ok' => true, 'id' => $id], JSON_UNESCAPED_UNICODE);
        break;
    }

    case 'poll': {
        // Agente -> relay. Exige secret. Consome (remove) as mensagens do celular.
        $secret = (string)($input['secret'] ?? '');
        relay_require_secret($code, $secret);
        $meta = relay_read_meta($code);
        $meta['last_seen'] = time();
        relay_write_meta($code, $meta);
        $messages = relay_queue_pop_all($code, 'inbox');
        echo json_encode(['ok' => true, 'messages' => $messages], JSON_UNESCAPED_UNICODE);
        break;
    }

    case 'reply': {
        // Agente -> celular. Exige secret.
        $secret = (string)($input['secret'] ?? '');
        relay_require_secret($code, $secret);
        $text = trim((string)($input['text'] ?? ''));
        if ($text === '') {
            relay_fail('resposta vazia');
        }
        $text = relay_text_limit($text, RELAY_MAX_TEXT_LEN);
        $inReplyTo = (string)($input['in_reply_to'] ?? '');
        $id = relay_queue_push($code, 'outbox', ['role' => 'assistant', 'text' => $text, 'in_reply_to' => $inReplyTo]);
        echo json_encode(['ok' => true, 'id' => $id], JSON_UNESCAPED_UNICODE);
        break;
    }

    case 'recv': {
        // Celular -> relay. Le respostas novas desde o cursor "since" (timestamp float).
        relay_require_client_key($code, (string)($input['client_key'] ?? ''));
        $since = (float)($input['since'] ?? $_GET['since'] ?? 0);
        $messages = relay_queue_peek_since($code, 'outbox', $since);
        $cursor = $since;
        foreach ($messages as $m) {
            $cursor = max($cursor, (float)($m['at'] ?? 0));
        }
        echo json_encode(['ok' => true, 'messages' => $messages, 'cursor' => $cursor], JSON_UNESCAPED_UNICODE);
        break;
    }

    default:
        relay_fail('action desconhecida: ' . $action);
}
