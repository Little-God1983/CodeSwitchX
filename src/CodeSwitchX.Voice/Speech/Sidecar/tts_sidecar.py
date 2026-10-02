"""What CodeSwitchX's speech sidecars share: the HTTP server, the token, the events on stdout and the exit with CodeSwitchX.
An engine's script (qwen_tts_server.py, kokoro_tts_server.py) only loads its model and speaks a sentence; it calls run().

Started and stopped by CodeSwitchX (SidecarLauncher.cs). Listens on 127.0.0.1 only, and every request must carry the
token from the CSX_TTS_TOKEN variable. Writes one JSON object per line to stdout as it goes:

    {"event": "listening", "port": 51234}
    {"event": "ready"}                       the model is loaded and warmed up
    {"event": "failed", "reason": "..."}     then exits

POST /v1/audio/speech with {"input": "...", "voice": "...", "language": "English"} answers with 16-bit little-endian mono
PCM at 24 kHz (OpenAI's "pcm" format), sent chunk by chunk while it is generated. A client that hangs up stops the
generation. One request is spoken at a time. GET /health answers {"state": "loading" | "ready"}.

Exits when the process given by --parent-pid ends, so it never outlives CodeSwitchX, however that ends. (Not when its
standard input closes: on Windows a thread blocked reading stdin holds the C runtime's lock on it, and loading torch
then hangs on that lock.)
"""

import argparse
import json
import os
import threading
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOKEN = os.environ.get("CSX_TTS_TOKEN", "")
SAMPLE_RATE = 24000

state = {"model": None, "ready": False, "speak": None, "voice": None}
generate_lock = threading.Lock()


def say(event, **fields):
    print(json.dumps({"event": event, **fields}), flush=True)


def exit_with_parent(pid):
    import ctypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    synchronize, infinite = 0x00100000, 0xFFFFFFFF
    handle = kernel32.OpenProcess(synchronize, False, pid)
    if handle:
        kernel32.WaitForSingleObject(ctypes.c_void_p(handle), infinite)
    os._exit(0)  # the parent ended (or was gone already)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass  # stdout is the event stream; CodeSwitchX logs the requests itself

    def _authorized(self):
        if TOKEN and self.headers.get("Authorization") == f"Bearer {TOKEN}":
            return True
        self._reply(401, {"error": "unauthorized"})
        return False

    def _reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if not self._authorized():
            return
        if self.path == "/health":
            self._reply(200, {"state": "ready" if state["ready"] else "loading"})
        else:
            self._reply(404, {"error": "not found"})

    def do_POST(self):
        # The body is read first, whatever the answer: on a kept-alive connection, a body left unread would be taken
        # for the next request.
        body = self.rfile.read(int(self.headers.get("Content-Length", "0") or 0))
        if not self._authorized():
            return
        if self.path != "/v1/audio/speech":
            self._reply(404, {"error": "not found"})
            return
        if not state["ready"]:
            self._reply(503, {"error": "loading"})
            return
        try:
            request = json.loads(body or b"{}")
            text = str(request["input"]).strip()
            voice = str(request.get("voice") or state["voice"]).lower()
            language = str(request.get("language") or "English")
        except (ValueError, KeyError) as error:
            self._reply(400, {"error": f"bad request: {error}"})
            return
        if not text:
            self._reply(400, {"error": "no input"})
            return

        with generate_lock:
            self._speak(text, voice, language)

    def _speak(self, text, voice, language):
        import numpy as np

        chunks = state["speak"](state["model"], text, voice, language)
        self.send_response(200)
        self.send_header("Content-Type", "audio/pcm")
        self.send_header("X-Sample-Rate", str(SAMPLE_RATE))
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        try:
            for audio in chunks:
                pcm = (np.clip(audio, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()
                if pcm:
                    self.wfile.write(f"{len(pcm):X}\r\n".encode() + pcm + b"\r\n")
                    self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.close_connection = True  # the client hung up: Raven was interrupted
        except Exception:  # noqa: BLE001 - the headers are out: hanging up without the last chunk tells the client
            traceback.print_exc()
            self.close_connection = True
        finally:
            chunks.close()


def run(load, speak, default_voice):
    """Serves until CodeSwitchX ends.

    load(model): loads and warms up the model named by --model; returns it.
    speak(model, text, voice, language): a generator of float32 audio arrays at 24 kHz, closed when the client hangs up.
    """
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--parent-pid", type=int)
    args = parser.parse_args()

    if args.parent_pid:
        threading.Thread(target=exit_with_parent, args=(args.parent_pid,), daemon=True).start()
    state["speak"] = speak
    state["voice"] = default_voice
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    say("listening", port=server.server_address[1])
    threading.Thread(target=server.serve_forever, daemon=True).start()

    try:
        state["model"] = load(args.model)
        state["ready"] = True
        say("ready")
    except Exception as error:  # noqa: BLE001 - whatever stops the load is reported, then the sidecar ends
        traceback.print_exc()
        say("failed", reason=f"{type(error).__name__}: {error}")
        os._exit(1)

    threading.Event().wait()
