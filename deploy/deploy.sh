#!/usr/bin/env bash
#
# deploy.sh — build and deploy this project. Fully headless: no sudo, no prompts, ever.
#
#   ./deploy/deploy.sh
#
# Assumes deploy/setup.sh has already provisioned this host (install prefix owned by you, units
# symlinked out of a directory you own, polkit grant in place). If it has not, this script says
# so and stops before touching anything — it never half-deploys and never blocks on a password.
#
# What it does:
#   1. builds as you (a failure here costs nothing — the running service is untouched),
#   2. refreshes the systemd unit if it changed (writing a file you own + daemon-reload),
#   3. swaps the binary tree in with the service briefly stopped,
#   4. verifies with a REAL health probe — success is a service that serves, never just a
#      process that launched.
#
# Knobs: RID, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

RID="${RID:-linux-x64}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — bringing it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (note: it is running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup

# ── 1. Build (unprivileged, before anything is disrupted) ─────────────────────
log "publishing (${RID}) → ${PUBLISH_DIR}"
rm -rf "$PUBLISH_DIR"
dotnet publish "${REPO_DIR}/src/Daemon/kgsm-speech.csproj" -c Release -r "$RID" -o "$PUBLISH_DIR"

# ── 2. Refresh the unit if it changed (we own the file; systemd reads it via the symlink) ──
install_units_unprivileged
if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    sysctl_do daemon-reload
fi

# ── 2b. Publish the leaf config descriptor (a no-op for a project that ships none) ──
# Before the swap, so the surface kgsm-api reads never lags the binary that implements it.
install_leaf_descriptor

# ── 3. The swap ───────────────────────────────────────────────────────────────
# The daemon is socket-activated and may well be idle-exited already; stopping it is how the running
# binary (and the models it is holding) is released before the file under it is replaced. It is never
# STARTED here — the health probe does that by connecting, which is what activation means.
log "stopping ${SERVICE} (release the running binary and its models)"
sysctl_do stop "$SERVICE" || true
STOPPED=1

log "syncing publish tree → ${PREFIX}"
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' "$PUBLISH_DIR/" "$PREFIX/"

# Stop then start rather than restart, so a changed SocketUser/SocketGroup takes effect on re-listen.
log "re-activating ${ENABLE_UNITS[0]}"
sysctl_do stop "${ENABLE_UNITS[0]}" || true
sysctl_do start "${ENABLE_UNITS[0]}"
STOPPED=0

# ── 4. Verify (the real pass/fail) ────────────────────────────────────────────
log "waiting for ${PROJECT} to answer ..."
if wait_health; then
    log "${PROJECT} answers ✓  (daemon wakes on demand and unloads when idle)"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but the health probe did not pass within ${HEALTH_TRIES}s."
    err "recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
