// CodeSwitchX Companion: lets CodeSwitchX open and close Claude Code chats in exactly this VS Code window.
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

/** Part of the view type of Claude Code's chat tabs (VS Code prefixes it). */
const ChatViewType = 'claudeVSCodePanel';

const SessionId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** How long a chat's tab may take to come to the front once Claude Code is asked to show it. */
const RevealMs = 3 * 1000;

let server;
let recordFile;

function activate(context) {
    const directory = path.join(process.env.LOCALAPPDATA || path.join(require('os').homedir(), 'AppData', 'Local'), 'CodeSwitchX', 'companion');
    const token = crypto.randomBytes(24).toString('hex');
    const pipe = `\\\\.\\pipe\\codeswitchx-companion-${crypto.randomBytes(8).toString('hex')}`;
    const version = context.extension.packageJSON.version;
    recordFile = path.join(directory, `${process.pid}.json`);

    server = net.createServer(socket => serve(socket, token, version));
    server.on('error', error => {
        // A record must never point at a pipe nobody serves.
        console.error('CodeSwitchX Companion: the pipe failed', error);
        removeRecord();
    });
    server.listen(pipe, () => {
        writeRecord(directory, { pid: process.pid, pipe, token, version });
    });

    context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(() => {
        if (server && server.listening) {
            writeRecord(directory, { pid: process.pid, pipe, token, version });
        }
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
        case 'closeChat':
            return closeChat(request.sessionId);
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

/**
 * Closes the tab of the chat with this session id; its claude.exe ends with it, and the conversation stays in Claude Code's
 * session list. Tabs show titles, not ids, so the tab is found by asking Claude Code to bring the chat to the front: the
 * Claude tab that becomes the active one is it. A Claude tab active already could be it, or the chat could be shown in
 * the side bar, so a blank editor is put in front first, and taken away again after.
 */
async function closeChat(sessionId) {
    if (typeof sessionId !== 'string' || !SessionId.test(sessionId)) {
        return { ok: false, error: 'No chat id.' };
    }

    if (!await activateClaudeCode()) {
        return { ok: false, error: "Claude Code's VS Code extension is not installed in this window." };
    }

    const before = new Set(claudeTabs());
    let blank;
    if (isClaudeTab(activeTab())) {
        await vscode.commands.executeCommand('workbench.action.files.newUntitledFile');
        blank = activeTab();
    }

    try {
        await vscode.commands.executeCommand('claude-vscode.editor.open', sessionId, undefined, undefined, undefined, undefined, { programmatic: 'pin-to-panel' });
        const tab = await waitFor(() => isClaudeTab(activeTab()) ? activeTab() : undefined, RevealMs);
        if (!tab) {
            return { ok: false, error: 'That chat is not in a tab of this VS Code window; it may be in the side bar. It can be closed there.' };
        }

        const opened = !before.has(tab);
        const closed = await vscode.window.tabGroups.close(tab);
        if (opened) {
            // It had no tab here, so Claude Code opened one: taken away again, nothing was closed.
            return { ok: false, error: 'That chat is not open in this VS Code window.' };
        }

        return closed ? { ok: true } : { ok: false, error: 'VS Code did not close the tab.' };
    } finally {
        if (blank && blank.input instanceof vscode.TabInputText && !blank.isDirty) {
            await vscode.window.tabGroups.close(blank).then(undefined, () => { });
        }
    }
}

function activeTab() {
    return vscode.window.tabGroups.activeTabGroup.activeTab;
}

function claudeTabs() {
    return vscode.window.tabGroups.all.flatMap(group => group.tabs).filter(isClaudeTab);
}

function isClaudeTab(tab) {
    return !!tab && tab.input instanceof vscode.TabInputWebview && tab.input.viewType.includes(ChatViewType);
}

async function waitFor(find, ms) {
    for (const end = Date.now() + ms; ; await new Promise(resolve => setTimeout(resolve, 50))) {
        const found = find();
        if (found || Date.now() >= end) {
            return found;
        }
    }
}

function sameToken(given, token) {
    const a = Buffer.from(given, 'utf8');
    const b = Buffer.from(token, 'utf8');
    return a.length === b.length && crypto.timingSafeEqual(a, b);
}

module.exports = { activate, deactivate };
