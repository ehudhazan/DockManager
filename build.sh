#!/usr/bin/env sh
# Build the image with a git-commit-count build number for the date-based version
# (yyyy.M.d.<N>). Use `./build.sh` instead of `docker compose build` to stamp it; a plain
# `docker compose build` leaves BUILD_NUMBER empty and falls back to a build-time clock value.
#
#   ./build.sh                 # docker compose build with BUILD_NUMBER = git rev-list --count HEAD
#   ./build.sh --no-cache      # extra arguments are forwarded to `docker compose build`
set -eu

BUILD_NUMBER="$(git rev-list --count HEAD 2>/dev/null || echo "")"
export BUILD_NUMBER
echo "eh.DockManager build: BUILD_NUMBER=${BUILD_NUMBER:-<fallback>}"
exec docker compose build "$@"
