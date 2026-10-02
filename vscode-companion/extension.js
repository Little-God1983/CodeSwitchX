// CodeSwitchX Companion: lets CodeSwitchX run commands in exactly this VS Code window.
//
// On start it listens on a named pipe of its own and writes where to find it, with the window's folders and a token,
// to %LOCALAPPDATA%\CodeSwitchX\companion\<extension host pid>.json. CodeSwitchX picks the window of a workspace by its
// folders and sends one JSON line per connection; a request without the file's token is refused. The pid is this
// window's extension host, which is also the parent of every claude.exe Claude Code's extension starts here.

const vscode = require('vscode');
const crypto = require('crypto');
const fs = require('fs');
const net = require('net');
const path = require('path');

const MaxRequest = 64 * 1024;

/** How long a connection may take to send its request line; one that stalls is dropped, not kept for the window's life. */
const RequestIdleMs = 10 * 1000;

const ClaudeCode = 'anthropic.claude-code';

let server;
let recordFile;

function activate(context) {
    const directory = path.join(process.env.LOCALAPPDATA || path.join(require('os').homedir(), 'AppData', 'Local'), 'CodeSwitchX', 'companion');
    const token = crypto.randomBytes(24).toString('hex');
    const pipe = `\\\\.\\pipe\\codeswitchx-companion-${crypto.randomBytes(8).toString('hex')}`;
    const version = context.extension.packageJSON.version;
    recordFile = path.join(directory, `${process.pid}.json`);

    server = net.createServer(socket => serve(socket, token, version));
    server.on('error', error => console.error('CodeSwitchX Companion: the pipe failed', error));
    server.listen(pipe, () => {
        writeRecord(directory, { pid: process.pid, pipe, token, version });
    });

    context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(() => {
        writeRecord(directory, { pid: process.pid, pipe, token, version });
    }));

    // Claude Code's extension activates on first use, which can take a while in a window that just started: done now,
    // the first chat CodeSwitchX opens here does not wait for it.
    activateClaudeCode().catch(error => console.error('CodeSwitchX Companion: Claude Code did not activate', error));
}

async function activateClaudeCode() {
    const extension = vscode.extensions.getExtension(ClaudeCode);
    if (extension && !extension.isActive) {
        await extension.activate();
    }

    return extension;
}

function deactivate() {
    removeRecord();
    if (server) {
        server.close();
        server = undefined;
    }
}

/** Writes the record whole (a temp file renamed over it), so CodeSwitchX never reads half of it. */
function writeRecord(directory, base) {
    try {
        fs.mkdirSync(directory, { recursive: true });
        const workspaceFile = vscode.workspace.workspaceFile;
        const record = {
            ...base,
            folders: (vscode.workspace.workspaceFolders || []).filter(f => f.uri.scheme === 'file').map(f => f.uri.fsPath),
            workspaceFile: workspaceFile && workspaceFile.scheme === 'file' ? workspaceFile.fsPath : null,
        };
        const temp = `${recordFile}.${crypto.randomBytes(4).toString('hex')}.tmp`;
        fs.writeFileSync(temp, JSON.stringify(record), { encoding: 'utf8' });
        fs.renameSync(temp, recordFile);
    } catch (error) {
        console.error('CodeSwitchX Companion: could not write its record', error);
    }
}

function removeRecord() {
    try {
        if (recordFile) {
            fs.unlinkSync(recordFile);
        }
    } catch {
        // gone already
    }
}

/** One request per connection: a JSON line in, a JSON line out. */
function serve(socket, token, version) {
    let received = '';
    socket.setEncoding('utf8');
    socket.setTimeout(RequestIdleMs, () => socket.destroy());
    socket.on('error', () => socket.destroy());
    socket.on('data', chunk => {
        received += chunk;
        if (received.length > MaxRequest) {
            socket.destroy();
            return;
        }

        const end = received.indexOf('\n');
        if (end < 0) {
            return;
        }

        // The request is in: answering may take a while (Claude Code activating), which is no stall.
        socket.setTimeout(0);
        socket.removeAllListeners('data');
        handle(received.slice(0, end), token, version).then(
            answer => socket.end(JSON.stringify(answer) + '\n'),
            error => socket.end(JSON.stringify({ ok: false, error: String(error && error.message || error) }) + '\n'));
    });
}

async function handle(line, token, version) {
    let request;
    try {
        request = JSON.parse(line);
    } catch {
        return { ok: false, error: 'The request is not JSON.' };
    }

    if (!request || typeof request.token !== 'string' || !sameToken(request.token, token)) {
        return { ok: false, error: 'Wrong token.' };
    }

    switch (request.command) {
        case 'ping':
            return { ok: true, pid: process.pid, version };
        case 'newChat':
            return newChat();
        default:
            return { ok: false, error: `No command '${request.command}'.` };
    }
}

/** Opens a new Claude Code chat tab in this window; its claude.exe starts at once, as a child of this extension host. */
async function newChat() {
    if (!await activateClaudeCode()) {
        return { ok: false, error: "Claude Code's VS Code extension is not installed in this window." };
    }

    // Undefined, never null: the extension looks a null session id up and throws.
    await vscode.commands.executeCommand('claude-vscode.editor.open', undefined, undefined, undefined, undefined, undefined, { programmatic: true });
    return { ok: true, pid: process.pid };
}

function sameToken(given, token) {
    const a = Buffer.from(given, 'utf8');
    const b = Buffer.from(token, 'utf8');
    return a.length === b.length && crypto.timingSafeEqual(a, b);
}

module.exports = { activate, deactivate };
