#!/usr/bin/env bash

set -Eeuo pipefail

########################################
# Config
########################################

# Local git checkout of this repo on the server.
REPO_DIR="/opt/spawner/src/spawner"

# Branch to deploy from.
GIT_BRANCH="main"

# Where the backend publish output should be installed.
BACKEND_INSTALL_DIR="/opt/spawner/app/backend"

# Where the built frontend dist should be installed.
FRONTEND_INSTALL_DIR="/opt/spawner/www"

# systemd service name for the backend.
SERVICE_NAME="spawner"

# Ownership to apply after deploy.
BACKEND_OWNER="spawner:spawner"
FRONTEND_OWNER="www-data:www-data"

# Build options.
BUILD_CONFIGURATION="Release"
RUN_BACKEND_TESTS="true"
RESTART_SERVICE="true"
USE_SUDO="true"

# Preserve these files from the live backend install, if present.
BACKEND_PRESERVE_FILES=(
  "appsettings.Production.json"
)

########################################
# Derived paths
########################################

BACKEND_PROJECT_REL="Spawner/Spawner.csproj"
FRONTEND_PROJECT_DIR_REL="Frontend/spawner"
WORK_DIR_NAME=".tmp-deploy"
PUBLISH_DIR_NAME="backend-publish"
PRESERVE_DIR_NAME="backend-preserve"

########################################
# Helpers
########################################

log() {
  printf '\n[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"
}

die() {
  printf '\nERROR: %s\n' "$*" >&2
  exit 1
}

run_root() {
  if [[ "${USE_SUDO}" == "true" ]]; then
    sudo "$@"
  else
    "$@"
  fi
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"
}

sync_dir() {
  local src="$1"
  local dst="$2"

  if command -v rsync >/dev/null 2>&1; then
    run_root mkdir -p "$dst"
    run_root rsync -a --delete "${src}/" "${dst}/"
  else
    run_root rm -rf "$dst"
    run_root mkdir -p "$dst"
    run_root cp -a "${src}/." "$dst/"
  fi
}

########################################
# Preflight
########################################

require_cmd git
require_cmd dotnet
require_cmd npm

[[ -d "${REPO_DIR}" ]] || die "REPO_DIR does not exist: ${REPO_DIR}"
[[ -f "${REPO_DIR}/${BACKEND_PROJECT_REL}" ]] || die "Backend project not found under REPO_DIR"
[[ -f "${REPO_DIR}/${FRONTEND_PROJECT_DIR_REL}/package.json" ]] || die "Frontend package.json not found under REPO_DIR"

WORK_DIR="${REPO_DIR}/${WORK_DIR_NAME}"
PUBLISH_DIR="${WORK_DIR}/${PUBLISH_DIR_NAME}"
PRESERVE_DIR="${WORK_DIR}/${PRESERVE_DIR_NAME}"

mkdir -p "${WORK_DIR}"
rm -rf "${PUBLISH_DIR}" "${PRESERVE_DIR}"
mkdir -p "${PUBLISH_DIR}" "${PRESERVE_DIR}"

########################################
# Update source
########################################

log "Updating git checkout in ${REPO_DIR}"
git -C "${REPO_DIR}" fetch --all --prune
git -C "${REPO_DIR}" checkout "${GIT_BRANCH}"
git -C "${REPO_DIR}" pull --ff-only origin "${GIT_BRANCH}"

########################################
# Build backend
########################################

if [[ "${RUN_BACKEND_TESTS}" == "true" ]]; then
  log "Running backend tests"
  dotnet test "${REPO_DIR}/spawner.sln"
fi

log "Publishing backend"
dotnet publish "${REPO_DIR}/${BACKEND_PROJECT_REL}" \
  -c "${BUILD_CONFIGURATION}" \
  -o "${PUBLISH_DIR}"

########################################
# Build frontend
########################################

log "Installing frontend dependencies"
npm --prefix "${REPO_DIR}/${FRONTEND_PROJECT_DIR_REL}" ci

log "Building frontend"
npm --prefix "${REPO_DIR}/${FRONTEND_PROJECT_DIR_REL}" run build

########################################
# Preserve live backend config
########################################

for rel_path in "${BACKEND_PRESERVE_FILES[@]}"; do
  src_path="${BACKEND_INSTALL_DIR}/${rel_path}"
  if [[ -f "${src_path}" ]]; then
    log "Preserving ${src_path}"
    mkdir -p "${PRESERVE_DIR}/$(dirname "${rel_path}")"
    cp -a "${src_path}" "${PRESERVE_DIR}/${rel_path}"
  fi
done

########################################
# Install backend + frontend
########################################

log "Deploying backend to ${BACKEND_INSTALL_DIR}"
sync_dir "${PUBLISH_DIR}" "${BACKEND_INSTALL_DIR}"

for rel_path in "${BACKEND_PRESERVE_FILES[@]}"; do
  preserved_path="${PRESERVE_DIR}/${rel_path}"
  if [[ -f "${preserved_path}" ]]; then
    log "Restoring preserved backend file ${rel_path}"
    run_root mkdir -p "${BACKEND_INSTALL_DIR}/$(dirname "${rel_path}")"
    run_root cp -a "${preserved_path}" "${BACKEND_INSTALL_DIR}/${rel_path}"
  fi
done

log "Deploying frontend to ${FRONTEND_INSTALL_DIR}"
sync_dir "${REPO_DIR}/${FRONTEND_PROJECT_DIR_REL}/dist" "${FRONTEND_INSTALL_DIR}"

########################################
# Ownership + restart
########################################

log "Applying ownership"
run_root chown -R "${BACKEND_OWNER}" "${BACKEND_INSTALL_DIR}"
run_root chown -R "${FRONTEND_OWNER}" "${FRONTEND_INSTALL_DIR}"

if [[ "${RESTART_SERVICE}" == "true" ]]; then
  log "Restarting systemd service ${SERVICE_NAME}"
  run_root systemctl restart "${SERVICE_NAME}"
  run_root systemctl status "${SERVICE_NAME}" --no-pager
fi

log "Deployment complete"
