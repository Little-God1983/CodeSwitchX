"""CodeSwitchX's Qwen3-TTS sidecar: speaks text with a preset voice, streaming the audio as it is generated. The server,
its protocol and its life are tts_sidecar.py's; this loads the model given by --model (a Hugging Face id) on the GPU.
"""

import tts_sidecar

# Sampled cooler than the model's 0.9: measured with Whisper on short replies ("Okay.", "Let me check."), 0.9 and 0.7
# babble or add fillers ("Hmm", "Haha") in about one sentence in eight, 0.5 in none of 56; 0.3 drones again.
TEMPERATURE = 0.5


def max_frames(text):
    """Speech takes about one frame (1/12 s) per character: half again and two seconds, so a run-away generation stops
    soon after the sentence should have ended."""
    return min(2048, 24 + (3 * len(text)) // 2)


def speak(model, text, voice, language):
    chunks = model.generate_custom_voice_streaming(
        text=text, speaker=voice, language=language, chunk_size=4, max_new_tokens=max_frames(text),
        temperature=TEMPERATURE)
    try:
        for audio, rate, _ in chunks:
            if rate != tts_sidecar.SAMPLE_RATE:
                raise RuntimeError(f"the model speaks at {rate} Hz, not {tts_sidecar.SAMPLE_RATE}")
            yield audio
    finally:
        chunks.close()


def load(model_id):
    from faster_qwen3_tts import FasterQwen3TTS

    model = FasterQwen3TTS.from_pretrained(model_id)
    model.warmup(prefill_len=100)
    # The first generations after the capture are still slow (seconds to the first chunk); two here keep that off the
    # first reply.
    for text in ("Ready.", "The voice is warming up, and this sentence is long enough to take a while."):
        for _ in speak(model, text, "ryan", "English"):
            pass
    return model


if __name__ == "__main__":
    tts_sidecar.run(load, speak, default_voice="ryan")
