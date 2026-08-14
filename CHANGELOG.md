# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
