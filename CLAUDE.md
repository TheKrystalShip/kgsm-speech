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
  - **`SpokenVocabulary` is the prior context the recogniser is primed with**, for the same reason
    and on the same terms. A recogniser knows English, not that a server here is called `Ketchup`;
    naming the host's servers as though they were the transcript of a moment ago shifts the spelling
    it picks. `Compose` builds that text within a character budget, and `IsEchoOf` catches the
    failure priming introduces — whisper handing the context back as though somebody had said it.
    A surface owns where the names come from and how often it re-reads them; it does not own how
    they are written down.
  - ⚠ **A sentence ends at terminal punctuation *followed by whitespace*.** `kgsm.sh`, `1.2.3` and
    `ggml-small.en.bin` are full of dots; cutting at one says half a sentence and pays a synthesis
    request for it. A **fenced block is dropped**, an unclosed one swallows the rest of the answer,
    and a fence marker counts **only at the true start of a line** — a sentence ending mid-line leaves
    the rest of it in a fresh buffer, and reading that as a line start takes "Done. \`\`\`yaml" for a
    fence and silences everything after it.
  - **`SpeechStatus` is everything the daemon can say about itself**, measured: which runtime each
    half actually loaded on, the voice being spoken in beside the configured one, when the models
    unload, which processes are attached, and per-lane tallies of what has been heard and said.
    Written as `key=value` lines rather than fixed offsets like the rest of the protocol — this one
    grows, and a reader that skips a key it does not know keeps working against a newer daemon.
    Never a transcript and never the spoken text: what somebody says into a microphone belongs to
    the surface that asked for it.
- **`src/Daemon`** — `kgsm-speech`, the binary: the socket server plus `SpeechRecogniser` (whisper)
  and `SpeechSynthesiser` (kokoro). `LaneTally` holds each half's counters for the life of the
  process — the durations as a fixed ring of the most recent passes, because a mean over an hour
  describes a machine nobody is waiting on.

## Invariants

- **The daemon holds no per-client state.** The names to expect travel with each utterance; the voice
  travels with each sentence, or is omitted to mean "this host's voice". A client that reconnects
  after an idle-exit is answered exactly as one that never left — which is what makes the idle-exit
  invisible and the client's reconnect logic trivial.
- ⚠ **Asking for a status is the one message that does not count as being asked.** Everything else on
  this wire is somebody using the daemon and pushes the idle deadline out; a panel watching it is not,
  and counting it would hold the models resident for as long as anybody had the page open. Connecting
  still starts the daemon — that is systemd's socket doing its job — so a surface reports on this leaf
  when somebody is looking, never on a timer.
- **What loaded is reported, not what was asked for.** Whisper takes the first runtime that
  initialises and Kokoro falls back when cuDNN is absent, so a host that asked for the card and did
  not get it recognises forty times slower and synthesises eight times slower. Both halves carry the
  runtime they actually opened on, and `unknown` stays `unknown`.
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
  install prefix because `deploy.sh` syncs that with `rsync --delete`. What they are — names, URLs,
  digests — is declared once in **`deploy/fetch-models.sh`**, which **adopts** `/var/lib/kgsm-bot/models`
  when a host has them from an earlier kgsm-bot install rather than re-downloading 813MB. `setup.sh`
  runs it here; a node gets the same bytes from the `kgsm-speech-models` package and keeps the script
  as `/usr/bin/kgsm-speech-fetch-models` to verify, repair or relocate them. Never write a second
  copy of a digest — `packaging/models/PKGBUILD` reads its URLs and sums back out of that script
  instead of restating them.
- **A publish carries only the natives this RID can load.** `Whisper.net.Runtime` emits every
  architecture it builds for regardless of `-r`; the `DropForeignWhisperNatives` target keeps
  `runtimes/<rid>` and `runtimes/cuda/<rid>` and drops the rest. Whisper probes both of those and
  prefers the CUDA one, so nothing it can reach is missing.
- **Two packages, two PKGBUILD directories.** `packaging/` is the daemon; `packaging/models/` is
  `kgsm-speech-models`, the 813MB the daemon hard-depends on — a separate directory rather than a
  split package because a split shares one `pkgver` and these weights move on their own clock.
  `.github/workflows/release.yml` builds the daemon on a `v*` tag; `tks/scripts/publish-repo.sh`
  aggregates into the fleet repository and is the one that builds both directories. The workflow is
  **generated** — edit `tks/scripts/ci-template/` and re-run `vendor-ci.sh`, never the copy here.

## Version tracking

- **Version source:** `<Version>` in `src/Daemon/kgsm-speech.csproj`, read by `deploy/version.sh`.
  That is this repo's **release line**: the number `CHANGELOG.md` headings carry, the number a `v*`
  tag must match, and the number the pacman package ships. `--pkgver` prints the pacman-safe form.
  A package never restates a version; it asks for one.
- **`TheKrystalShip.KGSM.Speech` in `src/Speech` versions independently** — bump it on **any** change
  to the framing or the message set, because NuGet caches by id+version and a same-version repack
  serves a stale dll to a consumer. It is not the release line: a CHANGELOG entry names it when it
  moves rather than numbering the heading with it.
- **`kgsm-speech-models` versions independently**, and is the one package here that declares a
  version rather than asking for one: `pkgver` in `packaging/models/PKGBUILD`. It moves when a model
  URL or digest moves and at no other time, so it is not a CHANGELOG heading either — an entry names
  it when it moves.
- Bump for any user-facing change; update `CHANGELOG.md` in the same commit.
