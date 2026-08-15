# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-speech` is the **speech leaf** of the KGSM ecosystem: one daemon holding whisper and kokoro,
serving recognition and synthesis to every surface on the host over a unix socket. The workspace
keystone is `../system-architecture.md`.

## Why this is a separate process

**Measured, not assumed.** The models cost about **1.6GB of host memory and 1.1GB of video memory**,
and **neither is released by unloading a model inside a running process**: whisper's `Dispose`
returned 9MB of 383MB, kokoro's returned 331MB of 1319MB, after an aggressive compacting GC and
`malloc_trim`. The VRAM does come back. The host memory does not — the CUDA runtime behind the models
is resident for the life of whatever loaded it, and **919MB of kokoro's cost appears on the first
sentence synthesised**, when cuDNN pages in its kernel libraries.

Ending a process releases everything. So the models live somewhere that can end without taking a
Discord bot or a web API with it, and `Speech:IdleMinutes` is the only lever there is.

## Commands

```bash
dotnet build kgsm-speech.slnx -c Release
dotnet test  kgsm-speech.slnx -c Release        # hermetic; no models, no card, no socket
/opt/kgsm-speech/kgsm-speech --voices           # what this host can say (activates the daemon; loads no model)
```

Deploy, like every `kgsm-*` project:

```bash
./deploy/setup.sh     # ONCE per host. Asks for sudo. Fetches the two models (813MB) or adopts kgsm-bot's.
./deploy/deploy.sh    # every deploy. NO sudo, NO prompts.
```

## The shape

- **`src/Speech`** — `TheKrystalShip.KGSM.Speech`, the **published package**: the wire protocol, the
  client, and `SpokenSentences`. Every surface that speaks consumes this, so a contract break is a
  compile break rather than a socket that goes quiet. AOT-safe (hand-rolled framing, no serializer,
  no reflection).
  - **`SpokenSentences` is where a reply is cut into sentences, for everybody.** It takes a reply
    arriving token by token and hands back what is worth speaking, so the first sentence plays while
    the model is still writing the third; `Whole` applies the same rules to a reply that is already
    complete. It is here rather than in each surface because where a reply is cut decides what a
    listener hears and when, and two surfaces answering that separately answer it differently within
    a release. Pure string work — no dependency, no protocol.
  - ⚠ **Every character is consumed exactly once**, which is why `Take` returns a finished list and
    not a lazy sequence. Fence state carries across deltas, so re-reading text already seen toggles it
    twice and starts reading the code aloud — and a caller that never enumerates an iterator consumes
    nothing at all, losing the reply with nothing to say so.
  - ⚠ **A sentence ends at terminal punctuation *followed by whitespace*.** `kgsm.sh`, `1.2.3` and
    `ggml-small.en.bin` are full of dots; cutting at one says half a sentence and pays a synthesis
    request for it. A **fenced block is dropped**, an unclosed one swallows the rest of the answer,
    and a fence marker counts **only at the true start of a line** — a sentence ending mid-line leaves
    the rest of it in a fresh buffer, and reading that as a line start takes "Done. \`\`\`yaml" for a
    fence and silences everything after it.
- **`src/Daemon`** — `kgsm-speech`, the binary: the socket server plus `SpeechRecogniser` (whisper)
  and `SpeechSynthesiser` (kokoro).

## Invariants

- **The daemon holds no per-client state.** The names to expect travel with each utterance; the voice
  travels with each sentence, or is omitted to mean "this host's voice". A client that reconnects
  after an idle-exit is answered exactly as one that never left — which is what makes the idle-exit
  invisible and the client's reconnect logic trivial.
- **The models load on demand, never at startup.** Starting is cheap, so systemd can activate this for
  a question as small as `--voices`. What loads them is `Wake` (a surface saying speech is about to be
  wanted) or the first real request.
- **The host owns the voice, not the surface.** `Speech:Voice` is the voice every surface speaks in,
  and surfaces are expected to send an empty voice name. That is what makes one person's assistant
  sound the same in Discord as in a browser without two settings being kept in step. `SpeakAs` changes
  it for **everyone** until the daemon restarts; the durable value is this leaf's own configuration
  and nothing here writes it back.
- ⚠ **Never call `KokoroVoiceManager`.** Its accessors bulk-load the whole `voices/` tree the first
  time they are asked for anything — **157 `.npy` arrays**, every other language included, measured at
  61MB of resident float32 to speak in one voice, on the LOH and never compacted.
  `KokoroVoice.FromPath` reads exactly one (~0.5MB). `InstalledVoices` lists the **directory** and
  loads nothing.
- **Audio is 16-bit signed little-endian PCM, mono, in both directions**: 16kHz in (whisper's native
  input), 24kHz out (as Kokoro produces it). Converting to what a surface plays — 48kHz stereo for a
  Discord connection — happens on that surface's side, because doing it here would put four times as
  many bytes on the wire.
- **Speech is always an enhancement.** Every client call answers with an absence rather than an
  exception when there is no daemon, no model or no card. A host without this leaf installed is the
  ordinary case, and a surface that treats it as a failure is one that breaks when an optional leaf is
  missing.
- **The socket's permissions are the whole access boundary.** There is no authentication and there
  should not be: every client is another service on this host running as the same account.

## Deploy specifics

- **The `.socket` is what is enabled**, not the `.service`. An inactive service is the resting state,
  which is why the leaf descriptor declares `OnDemand = true` — the Control Panel renders that
  neutrally rather than as "stopped".
- **`deploy.sh` never starts the service.** It stops it (releasing the binary and the models), syncs,
  re-activates the socket, and then *connects* — the health probe is `kgsm-speech --voices`, which
  proves the socket is bound, that connecting activates the daemon, and that the daemon reads its
  configuration and answers. `systemctl is-active` on an on-demand unit proves none of that.
- **The models live in `/var/lib/kgsm-speech/models`** (the unit's `StateDirectory`), outside the
  install prefix because `deploy.sh` syncs that with `rsync --delete`. `setup.sh` **adopts**
  `/var/lib/kgsm-bot/models` when a host has them from an earlier kgsm-bot install rather than
  re-downloading 813MB.

## Version tracking

- **Version source:** `<Version>` in `src/Daemon/kgsm-speech.csproj`, read by `deploy/version.sh`.
  The package in `src/Speech` versions independently — bump it on **any** change to the framing or
  the message set, because NuGet caches by id+version and a same-version repack serves a stale dll to
  a consumer.
- Bump for any user-facing change; update `CHANGELOG.md` in the same commit.
