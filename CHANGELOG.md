# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
