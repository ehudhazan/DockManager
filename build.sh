#!/usr/bin/env sh
# Build the image, tag it as `latest` and as the date-based version (yyyy.M.d.<N>),
# and push both tags to Docker Hub.
#
#   ./build.sh                 # build, tag latest + version, push both tags
#   ./build.sh --no-cache      # extra arguments are forwarded to `docker compose build`
#   ./build.sh --no-push       # build and tag, but do not push
#
# BUILD_NUMBER is the git commit count (git rev-list --count HEAD) for the date-based
# version; a plain `docker compose build` leaves it empty and falls back to a build-time
# clock value. Use this script (not `docker compose build`) to stamp the version, tag it,
# and publish it.
set -eu

IMAGE="ehudhazan/dockmanager"

BUILD_NUMBER="$(git rev-list --count HEAD 2>/dev/null || echo "")"
export BUILD_NUMBER

# Mirror the csproj `ResolveBuildVersion` version: yyyy.M.d.<buildNumber>, with the UTC
# build date and no leading zeros in the month or day (portable `date` + sed form).
if [ -n "$BUILD_NUMBER" ]; then
  VERSION="$(date -u +%Y.%m.%d | sed 's/\.0/./g').${BUILD_NUMBER}"
else
  VERSION="$(date -u +%Y.%m.%d | sed 's/\.0/./g').$(date -u +%H%M)"
fi
echo "eh.DockManager build: BUILD_NUMBER=${BUILD_NUMBER:-<fallback>} VERSION=${VERSION}"

# Split our own flags from the arguments forwarded to `docker compose build`.
PUSH=1
BUILD_ARGS=""
for arg in "$@"; do
  case "$arg" in
    --no-push) PUSH=0 ;;
    *) BUILD_ARGS="${BUILD_ARGS} ${arg}" ;;
  esac
done

# The compose `image:` tags the result `latest`; the version tag is added below.
# shellcheck disable=SC2086
docker compose build ${BUILD_ARGS}

docker tag "${IMAGE}:latest" "${IMAGE}:${VERSION}"
echo "Tagged ${IMAGE}:latest and ${IMAGE}:${VERSION}"

if [ "$PUSH" -eq 1 ]; then
  docker push "${IMAGE}:latest"
  docker push "${IMAGE}:${VERSION}"
else
  echo "Skipping push (--no-push). To publish later:"
  echo "  docker push ${IMAGE}:latest && docker push ${IMAGE}:${VERSION}"
fi
