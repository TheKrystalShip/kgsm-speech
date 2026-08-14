# kgsm-speech

The host's voice. One daemon holding [whisper](https://github.com/ggerganov/whisper.cpp) and
[Kokoro](https://github.com/Lyrcaxis/KokoroSharp), serving speech recognition and synthesis to every
KGSM surface on the machine over a unix socket.

It is a **leaf**: optional, independently deployable, and depended on by nobody. A host without it
still runs everything else — surfaces that would have spoken answer in text instead.

## Why a daemon and not a library

The models cost about **1.6GB of memory and 1.1GB of video memory**, and a process that has loaded
them cannot give that back. Measured on a live host: disposing whisper returned 9MB of 383MB;
disposing kokoro returned 331MB of 1319MB. The video memory comes back, the host memory does not —
the CUDA runtime behind the models is resident until the process exits, and most of kokoro's cost
appears on the *first sentence synthesised*, when cuDNN pages in its kernels.

Ending a process releases everything. So the models live in one that can end:

- **Socket-activated.** systemd holds the socket; the first connection starts the daemon. Nothing
  supervises it and no surface starts a process.
- **Loads on demand.** Starting costs a few tens of megabytes. The models load when a surface says
  speech is about to be wanted, or when the first request arrives.
- **Idle-exits** after `Speech:IdleMinutes` with nothing asked (`0` stays loaded). The socket stays
  bound, so the next request brings it back at the cost of a few seconds.

## Using it

```bash
kgsm-speech --voices          # what this host can say, and which voice it uses
```

From C#, with the `TheKrystalShip.KGSM.Speech` package:

```csharp
await using var speech = new SpeechClient(logger: logger);

speech.Wake();                                          // load now — speech is about to be wanted

(var outcome, string text) = await speech.TranscribeAsync(pcm16k, vocabulary: "factorio, terraria");
byte[]? audio = await speech.SynthesizeAsync("Factorio is running.");   // 24kHz mono PCM
```

`SynthesizeAsync` with no voice named speaks in **this host's** voice, which is the point: a person
hears the same assistant in Discord as they do in a browser, and it is one setting in one place.

## Configuration

`/etc/kgsm-speech/kgsm-speech.env` (see `deploy/kgsm-speech.env.example`), or the Control Panel — the
leaf descriptor declares the whole surface. The defaults are in `kgsm-speech.settings.json`.

| Key | Default | |
|---|---|---|
| `Speech__Voice` | `af_heart` | The voice every surface speaks in |
| `Speech__IdleMinutes` | `0` | Minutes with nothing asked before unloading. `0` stays |
| `Speech__UseGpu` | `true` | Recognise on the card (~40× faster; falls back on its own) |
| `Speech__SpeakUseGpu` | `true` | Synthesise on the card (~8× faster; needs cuDNN) |

## Installing

```bash
./deploy/setup.sh     # once per host — asks for sudo, fetches the models (813MB)
./deploy/deploy.sh    # every deploy — no sudo, no prompts
```

`setup.sh` adopts the models from an earlier `kgsm-bot` install if it finds them, rather than
downloading them again.

## Licence

GPL-3.0-or-later.
