#!/usr/bin/env bash
#
# fetch-models.sh — put the two models this leaf is for into place.
#
#   ./deploy/fetch-models.sh          # fetch what is missing, verify what is there
#
# One to hear with (whisper) and one to speak with (kokoro), 813MB together. They live in the unit's
# StateDirectory rather than the install prefix, because deploy.sh syncs that prefix with
# `rsync --delete` and these must survive a deploy.
#
# This script is the single declaration of what those files are — their URLs and their digests. It
# is called by deploy/setup.sh on a development host, and installed as
# /usr/bin/kgsm-speech-fetch-models on a packaged one; packaging/models/PKGBUILD greps the six
# assignments below and hands them to makepkg as its source array. Every path to a model therefore
# reads the same URLs against the same digests, and there is no second copy to drift.
#
# ⚠ Those six lines are parsed as text. Keep them one plain `NAME=value` per line at the start of a
# line — a continuation, an export or an `if` around one leaves the PKGBUILD's source array empty,
# which fails its build rather than packaging something else.
#
# Knobs:
#   KGSM_SPEECH_MODELS=0   do nothing and say so — this host will neither hear nor speak
#   MODEL_DIR              where they go (default /var/lib/kgsm-speech/models)
#   MODEL_OWNER            the account to give the directory to when run as root (default kgsm)
#   ADOPT_FROM_DIR         a directory to take an existing copy from (default kgsm-bot's)
#
set -euo pipefail

MODEL_DIR="${MODEL_DIR:-/var/lib/kgsm-speech/models}"
MODEL_OWNER="${MODEL_OWNER:-kgsm}"

# Where kgsm-bot used to keep them. A host that has been running the bot's own speech already has
# both files here, and moving them is the difference between a minute and 813MB it already has.
ADOPT_FROM_DIR="${ADOPT_FROM_DIR:-/var/lib/kgsm-bot/models}"

RECOGNITION_MODEL_NAME="ggml-small.en.bin"
RECOGNITION_MODEL_URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/${RECOGNITION_MODEL_NAME}"
RECOGNITION_MODEL_SHA256="c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"

SYNTHESIS_MODEL_NAME="kokoro.onnx"
SYNTHESIS_MODEL_URL="https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/${SYNTHESIS_MODEL_NAME}"
SYNTHESIS_MODEL_SHA256="0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05"

# Standalone rather than sourced from deploy-common.sh: a packaged node has no deploy/ directory.
log()  { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m** %s\033[0m\n' "$*" >&2; }

sha256_matches() {   # $1 = file, $2 = expected digest
    [[ "$(sha256sum "$1" | cut -d' ' -f1)" == "$2" ]]
}

# Take over a model an earlier kgsm-bot install fetched, rather than downloading it again. It is a
# move, not a copy, because two 488MB files that must stay identical is a trap and the bot no longer
# reads its own.
adopt_model() {   # $1 = filename
    local name="$1" from="${ADOPT_FROM_DIR}/$1" to="${MODEL_DIR}/$1"

    [[ -f "$to" || ! -f "$from" ]] && return 0

    log "adopting ${name} from ${ADOPT_FROM_DIR} (kgsm-bot fetched it; this leaf owns it now)"
    mv -f "$from" "$to" || warn "could not move ${from} — leaving it where it is"
}

# One model: present and correct is a no-op, present and wrong is replaced, absent is fetched.
# $1 = what it is for, $2 = filename, $3 = url, $4 = digest, $5 = human size
fetch_model() {
    local what="$1" name="$2" url="$3" digest="$4" size="$5"
    local model="${MODEL_DIR}/${name}"

    if [[ -f "$model" ]] && sha256_matches "$model" "$digest"; then
        return 0
    fi

    # A file that is present and wrong is worse than one that is absent: it loads, behaves badly, and
    # reads as a tuning problem. So it goes BEFORE the fetch is attempted rather than after it
    # succeeds — if the download then fails, this host reports having no model, which is true and
    # actionable, instead of quietly producing nonsense.
    if [[ -f "$model" ]]; then
        warn "${model} does not match its expected digest — discarding it and re-fetching"
        rm -f "$model"
    fi

    log "fetching the ${what} model (~${size}, once) → ${model}"

    # Downloaded beside the target and moved into place only once verified, so an interrupted fetch
    # leaves no half-file to load.
    local tmp="${model}.partial"
    if ! curl -fL --retry 3 --retry-delay 2 -o "$tmp" "$url"; then
        rm -f "$tmp"
        warn "could not fetch the ${what} model — this leaf will start without it."
        warn "  try again:  ${0}"
        return 0
    fi

    if ! sha256_matches "$tmp" "$digest"; then
        rm -f "$tmp"
        warn "the downloaded ${what} model does not match its expected digest — discarded."
        return 0
    fi

    mv -f "$tmp" "$model"
    log "${what} model installed ✓"
}

[[ "${KGSM_SPEECH_MODELS:-1}" == "0" ]] && {
    log "skipping the models (KGSM_SPEECH_MODELS=0) — this host will neither hear nor speak"
    exit 0
}

# systemd's StateDirectory= creates this before the daemon starts, so on a running host it is
# already here and owned correctly. Creating it covers the case where nothing has started yet —
# a package installed but never activated.
if [[ ! -d "$MODEL_DIR" ]]; then
    install -d -m 0755 "$MODEL_DIR"
    [[ "$(id -u)" -eq 0 ]] && chown "${MODEL_OWNER}:${MODEL_OWNER}" "$MODEL_DIR"
fi

adopt_model "$RECOGNITION_MODEL_NAME"
adopt_model "$SYNTHESIS_MODEL_NAME"

fetch_model "hearing"  "$RECOGNITION_MODEL_NAME" "$RECOGNITION_MODEL_URL" "$RECOGNITION_MODEL_SHA256" "488MB"
fetch_model "speaking" "$SYNTHESIS_MODEL_NAME"   "$SYNTHESIS_MODEL_URL"   "$SYNTHESIS_MODEL_SHA256"   "325MB"

# Root fetching on behalf of the service account leaves files it owns; the daemon reads them as
# MODEL_OWNER and would find them unreadable if the mode were tighter than this.
if [[ "$(id -u)" -eq 0 ]]; then
    chown "${MODEL_OWNER}:${MODEL_OWNER}" "${MODEL_DIR}/${RECOGNITION_MODEL_NAME}" \
        "${MODEL_DIR}/${SYNTHESIS_MODEL_NAME}" 2> /dev/null || true
fi
