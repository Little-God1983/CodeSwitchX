"""Makes Fixtures/warmup-features.bin: Pipecat's Smart Turn features of the warm-up sample, the reference the C# port
(WhisperFeatures) is tested against. Run from this folder: uv run --with numpy python make_whisper_features.py
Pipecat is pinned to the commit the port was written from."""
import importlib.util, pathlib, urllib.request
import numpy as np

COMMIT = "3b2dccd843c65d1632631ceac248a5128fb32242"
URL = f"https://raw.githubusercontent.com/pipecat-ai/pipecat/{COMMIT}/src/pipecat/audio/turn/smart_turn/_whisper_features.py"
here = pathlib.Path(__file__).parent
source = here / "_whisper_features.py"
urllib.request.urlretrieve(URL, source)
spec = importlib.util.spec_from_file_location("wf", source)
wf = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wf)

pcm = (here / "../../../src/CodeSwitchX.Voice/Dictation/WarmUpSpeech.pcm").read_bytes()
audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
audio = np.pad(audio, (128000 - audio.size, 0))  # Smart Turn pads at the front (local_smart_turn_v3.py)
features = wf.compute_whisper_log_mel_features(audio, do_normalize=True)
assert features.shape == (80, 800)
(here / "Fixtures").mkdir(exist_ok=True)
(here / "Fixtures/warmup-features.bin").write_bytes(features.astype("<f4").tobytes())
source.unlink()
print("wrote", features.shape, features.min(), features.max())
