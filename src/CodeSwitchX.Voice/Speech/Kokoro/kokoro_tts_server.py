"""CodeSwitchX's Kokoro sidecar: speaks text with one of Kokoro-82M's voices, on the CPU (kokoro-onnx). The server, its
protocol and its life are tts_sidecar.py's; this loads the model from the folder given by --model, which holds
kokoro-v1.0.onnx and voices-v1.0.bin.

Raven sends a reply's sentences together (#290); each is generated whole, then sent, so the first is heard while the next
is made. kokoro-onnx's own stream does not help: it cuts the text by length, and a short reply is one piece.
"""

import os
import re

import numpy as np

import tts_sidecar

# What kokoro-onnx puts between two sentences of one text.
SENTENCE_PAUSE = 0.25


def language_of(voice):
    """Kokoro's voices are named by language and sex: "af_heart" is American English, "bf_emma" British."""
    return "en-gb" if voice.startswith("b") else "en-us"


def sentences(text):
    return [s for s in re.split(r"(?<=[.!?])\s+", text) if s]


def speak(model, text, voice, language):
    pause = np.zeros(int(SENTENCE_PAUSE * tts_sidecar.SAMPLE_RATE), dtype=np.float32)
    for i, sentence in enumerate(sentences(text)):
        audio, rate = model.create(sentence, voice=voice, speed=1.0, lang=language_of(voice))
        if rate != tts_sidecar.SAMPLE_RATE:
            raise RuntimeError(f"the model speaks at {rate} Hz, not {tts_sidecar.SAMPLE_RATE}")
        yield audio if i == 0 else np.concatenate([pause, audio])


def load(folder):
    from kokoro_onnx import Kokoro

    model = Kokoro(os.path.join(folder, "kokoro-v1.0.onnx"), os.path.join(folder, "voices-v1.0.bin"))
    # The first sentence pays for the session's first run; this one keeps that off the first reply.
    for _ in speak(model, "Ready.", "af_heart", "English"):
        pass
    return model


if __name__ == "__main__":
    tts_sidecar.run(load, speak, default_voice="af_heart")
