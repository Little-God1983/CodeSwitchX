"""CodeSwitchX's Kokoro sidecar: speaks text with one of Kokoro-82M's voices, on the CPU (kokoro-onnx). The server, its
protocol and its life are tts_sidecar.py's; this loads the model from the folder given by --model, which holds
kokoro-v1.0.onnx and voices-v1.0.bin.

A sentence is generated whole, then sent: Kokoro makes one about three times faster than it is spoken, and Raven's
answers come a sentence at a time.
"""

import os

import tts_sidecar


def language_of(voice):
    """Kokoro's voices are named by language and sex: "af_heart" is American English, "bf_emma" British."""
    return "en-gb" if voice.startswith("b") else "en-us"


def speak(model, text, voice, language):
    audio, rate = model.create(text, voice=voice, speed=1.0, lang=language_of(voice))
    if rate != tts_sidecar.SAMPLE_RATE:
        raise RuntimeError(f"the model speaks at {rate} Hz, not {tts_sidecar.SAMPLE_RATE}")
    yield audio


def load(folder):
    from kokoro_onnx import Kokoro

    model = Kokoro(os.path.join(folder, "kokoro-v1.0.onnx"), os.path.join(folder, "voices-v1.0.bin"))
    # The first sentence pays for the session's first run; this one keeps that off the first reply.
    for _ in speak(model, "Ready.", "af_heart", "English"):
        pass
    return model


if __name__ == "__main__":
    tts_sidecar.run(load, speak, default_voice="af_heart")
