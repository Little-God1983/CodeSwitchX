"""CodeSwitchX's Qwen3-TTS sidecar: speaks text with a preset voice, streaming the audio as it is generated. The server,
its protocol and its life are tts_sidecar.py's; this loads the model given by --model (a Hugging Face id) on the GPU.
"""

import tts_sidecar

# Without an instruction the model picks its own mood, often flat: Raven sounded bored or sleepy (#290). Only the 1.7B
# model follows it; faster-qwen3-tts drops it for the 0.6B one.
INSTRUCT = "Speak in a warm, friendly and lively tone, with clear and natural English pronunciation."

# Sampled cooler than the model's 0.9. Measured with Whisper on 60 short replies ("Okay.", "Let me check.") spoken with
# INSTRUCT, a reply to a request (#290): 0.7 added no word, 0.9 one ("Done. Could you?") and misheard more. Without the
# instruction (#72), 0.7 and 0.9 babbled in about one sentence in eight, 0.5 in none of 56; 0.3 drones.
INSTRUCTED_TEMPERATURE = 0.7
PLAIN_TEMPERATURE = 0.5


def follows_instruct(model):
    """As faster-qwen3-tts tells it: the 0.6B model's size is "0b6"."""
    return model.model.model.tts_model_size not in "0b6"


def max_frames(text):
    """Speech takes about one frame (1/12 s) per character: half again and two seconds, so a run-away generation stops
    soon after the sentence should have ended."""
    return min(2048, 24 + (3 * len(text)) // 2)


def speak(model, text, voice, language):
    instructed = follows_instruct(model)
    chunks = model.generate_custom_voice_streaming(
        text=text, speaker=voice, language=language, instruct=INSTRUCT if instructed else None, chunk_size=4,
        max_new_tokens=max_frames(text), temperature=INSTRUCTED_TEMPERATURE if instructed else PLAIN_TEMPERATURE)
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
