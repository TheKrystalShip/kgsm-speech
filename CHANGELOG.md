# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.6.0] - 2026-08-15

### Added — this host's speaking rate is a setting

`Speech:SpeechRate` is how fast this host speaks, as a percentage of the voice's natural pace: 100 is
the pace the voice was trained at, and the Control Panel offers 50 to 200. Kokoro takes a float
multiplier and this is divided by a hundred on the way in; the two bounds are one pair of constants,
declared to the panel and applied as the daemon's own clamp, so a slider cannot ask for a rate the
synthesiser will not honour and a hand-edited env file cannot ask for zero.

It belongs to the host for the same reason the voice does — every surface asks this daemon, so one
setting changes how the assistant sounds in Discord and in a browser at once. Rate costs nothing:
synthesis is priced per character, and a faster reading is the same characters in less audio.

## [1.5.0] - 2026-08-15

### Added — the daemon reports on itself

A `Status` message, answered with everything this daemon can measure about itself: whether the models
are loaded and how long loading took, which runtime each half actually opened on, the voice being
spoken in beside the one configured, when the models unload if nothing else is asked, which processes
are attached, and per-lane tallies of what has been heard and said since the process started —
counts, seconds of audio, mean and p95 pass durations, and how each lane's last pass went. No
transcript and no spoken text: what somebody says into a microphone belongs to the surface that asked
for it.

`SpeechClient.StatusAsync` reads it, and `TheKrystalShip.KGSM.Speech` moves to **1.4.0** for the two
new message kinds. The payload is `key=value` lines rather than fixed offsets, because this one grows
— a reader skips a key it does not know and keeps every one it does.

⚠ **Asking for a status is the one message that does not push the idle deadline out.** Everything else
on this wire is somebody using the daemon; a panel watching it is not, and counting it would hold
1.6GB of models resident for as long as anybody had the page open.

### Changed — what loaded, not what was asked for

Both halves report the runtime they actually opened on. Whisper takes the first runtime that
initialises and Kokoro falls back when cuDNN is absent, so a host that asked for the card and did not
get it recognises forty times slower and synthesises eight times slower — a difference that was
otherwise invisible. The `Ready` message's detail line is composed the same way, from what the two
halves loaded rather than from the settings that asked them to.

## [1.4.0] - 2026-08-15

### Added — this leaf is a package

`packaging/PKGBUILD` and `.github/workflows/release.yml` put kgsm-speech in the fleet's pacman
repository on the same terms as every other leaf: tagging `v<version>` builds, tests, packages, signs
and publishes to this repo's release, and `scripts/publish-repo.sh --from-releases` aggregates it into
the fleet database. The package lays down `/opt/kgsm-speech`, both units, the sysusers fragment, the
env file, the leaf descriptor, and `kgsm-speech` on PATH.

`libgomp` is a hard dependency: whisper's CPU backend links it, and that backend is what a host
without a usable card hears on. The card stays optional — `nvidia-utils` for the driver library,
`cuda` for the runtime, `cudnn` for synthesis specifically.

### Added — one script owns what the models are

`deploy/fetch-models.sh` is the single declaration of the two models' names, URLs and digests.
`deploy/setup.sh` runs it on a development host; the package installs it as
`/usr/bin/kgsm-speech-fetch-models` for a node that has no `deploy/` directory. The 813MB stays out of
the package — it belongs in the StateDirectory, where it survives every upgrade.

### Changed — a publish carries only the natives this host can load

`Whisper.net.Runtime` publishes every architecture it builds for regardless of `-r linux-x64`, which
is 164MB of Windows, macOS and ARM shared objects a linux-x64 host will never open. `runtimes/<rid>`
and `runtimes/cuda/<rid>` survive; the rest, and the Metal shader source beside them, do not. The
package weighs 365MB rather than 499MB, and both models still load on the card.

### Changed — the CHANGELOG numbers the release line

A heading here is the version `deploy/version.sh` prints — the daemon's, which is what the tag matches
and what the package ships. `TheKrystalShip.KGSM.Speech` versions independently in its own csproj, and
an entry says so when it moves.

## [1.3.0] - 2026-08-15

### Added — what the recogniser is primed with, decided once for every surface

`SpokenVocabulary` composes the prior context whisper is conditioned on: this host's triggers, its
servers and the games it can install, written as sentences within a character budget. `IsEchoOf`
catches the failure that priming introduces — given audio with nothing recognisable in it, whisper
sometimes continues the context instead of returning nothing, which arrives looking exactly like
somebody reading a list of server names aloud.

It moves here from the Discord bot for the reason `SpokenSentences` did, and the evidence was already
in this repo: `PrimingMeasurement` has been measuring whether priming changes the spelling that comes
back, against a context it composed by hand because the composer lived somewhere it could not reach.
It now measures the composer that ships. A browser sending a voice note and a voice channel carrying
a spoken request are the same host being asked about the same servers, and a second implementation of
which names to name is a second set of servers misheard.

Surfaces keep what is theirs: where the names come from, and how often the list is re-read.

## [1.2.0] - 2026-08-15

### Added — where a reply is cut into sentences, decided once for every surface

`SpokenSentences` takes a reply arriving token by token and hands back the sentences it is worth
speaking, so the first one plays while the model is still writing the third. `SpokenSentences.Whole`
applies the same rules to a reply that is already complete — a short line a surface writes for itself,
or an answer that was never streamed.

Where a reply is cut decides what a listener hears and when, and two surfaces answering that question
separately answer it differently within a release. The Discord bot playing into a voice channel and
the assistant streaming audio to a browser are the same job with different plumbing under it, so the
rules live here, in the package both already depend on.

The rules, each of which cost a failing test somewhere to find:

- ⚠ **Every character is consumed exactly once.** Fence state carries across deltas, so an
  implementation that re-reads text it has already seen toggles that state a second time and starts
  reading the code aloud. `Take` therefore returns a finished list rather than a lazy sequence: a
  caller that never enumerates an iterator consumes nothing at all, and the reply would go missing
  with nothing anywhere to say so.
- ⚠ **A sentence ends at terminal punctuation followed by whitespace**, decided one character late.
  `kgsm.sh`, `1.2.3` and `ggml-small.en.bin` are full of dots, and cutting at one reads half a
  sentence aloud and spends a synthesis request doing it.
- ⚠ **A fenced block is dropped, not spoken** — it spans sentences and its "sentences" are lines of
  syntax. A fence the reply never closes swallows the rest of it, which is correct: an answer that
  opened a fence and stopped is a code block, whatever follows.
- ⚠ **A fence marker counts only at the true start of a line.** A sentence ending part-way along a
  line leaves the rest of it in a fresh buffer, and reading that as a line start takes
  "Done. ```yaml" for a fence — silencing every word after it for the rest of the answer.
- **A line ending is a boundary**, so a heading and a list item are each their own breath.
- **A sentence shorter than `ShortestWorthSaying` waits for the next one**, and whatever is left is
  said by the flush whether it terminated or not. The floor is 24 characters — low on purpose, since a
  complete sentence of six words is the answer and holding it back trades away the latency this
  exists to remove.
- **Markup goes, words never do**: emphasis, code spans, table pipes, list markers, heading hashes,
  and a link's target — which is an address, not a sentence, and whose dots would cut one in the
  middle.

## [1.1.0] - 2026-08-15

### Added — audio in a container, for callers that cannot play raw samples

`SynthesizeAs` asks for the samples wrapped in a named format; `Wave.Mono16` supplies the only one
there is so far, a 44-byte RIFF header a browser decodes with no library. The daemon synthesises once
either way — a format is a wrapper applied on the way out, not a second synthesis.

⚠ **Its own message rather than a field on `Synthesize`.** That one is already spoken by a deployed
client, and a shape that changed under it would be misread rather than refused. A caller that plays
audio itself — a Discord connection takes PCM — keeps sending the shorthand forever and needs no
version bump.

WAV is deliberately not the smallest thing that could go over a wire: a spoken sentence is ~120KB
where Opus would be ~7KB. The trade buys no codec, no container muxer and no dependency, and because
the format is negotiated per request, a smaller one can be added here alone — no client, relay or
contract change.

## [1.0.1] - 2026-08-15

### Fixed — the unit no longer names the Control Panel

`kgsm-speech.service` referenced `/var/lib/kgsm-api/leaf-overrides/speech.env` directly. A leaf must
never name the API — a host with no Control Panel has nothing to point at — and kgsm-api installs a
drop-in for exactly this, which is also what makes the layering right: a drop-in is parsed after the
unit's own directives, so a panel value wins without the unit knowing the panel exists.

## [1.0.0] - 2026-08-15

### Added — the speech leaf

One daemon holding whisper and kokoro, serving recognition and synthesis to every surface on the host
over a unix socket at `/run/kgsm-speech/speech.sock`.

It exists as a separate process for a measured reason. The models cost about 1.6GB of host memory and
1.1GB of video memory, and unloading them inside a running process does not give that back: disposing
whisper returned 9MB of 383MB, disposing kokoro 331MB of 1319MB, after an aggressive compacting GC and
`malloc_trim`. The video memory returns; the host memory belongs to the CUDA runtime until the process
exits — and 919MB of kokoro's figure appears on the *first sentence synthesised*, when cuDNN pages in
its kernels. Ending a process releases everything, so the models live in one that can end.

- **Socket-activated, loading on demand.** systemd holds the socket and starts the daemon on the first
  connection; starting costs a few tens of megabytes because the models load on `Wake` — a surface
  saying speech is about to be wanted — or on the first request. `kgsm-speech --voices` therefore
  answers without loading anything, which is also what makes it the deploy's health probe.
- **Idle-exits** after `Speech:IdleMinutes` with nothing asked (`0`, the default, stays loaded). The
  socket stays bound, so nothing is lost: the next request starts the daemon again.
- **The host owns the voice.** `Speech:Voice` is the voice every surface speaks in, and surfaces send
  no voice name at all — which is what makes one person's assistant sound the same in Discord as in a
  browser without two settings to keep in step.
- **`TheKrystalShip.KGSM.Speech`** carries the wire protocol and the client, so a contract break is a
  compile break rather than a socket that goes quiet.

⚠ `KokoroVoiceManager` is deliberately untouched: its accessors bulk-load the whole `voices/` tree —
157 arrays, every other language included, 61MB of resident float32 to speak in one voice, on the LOH
and never compacted. One voice is read with `KokoroVoice.FromPath`, and listing what is available
reads the directory.
