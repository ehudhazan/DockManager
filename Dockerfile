# File: Dockerfile
# syntax=docker/dockerfile:1
#
# eh.DockManager — hardened multi-stage build.
#
# NOTE ON BASE TAGS: as of this build, .NET 11 SDK/runtime images are published on Azure Linux and
# Alpine; an Ubuntu "noble" variant (and its "-chiseled" runtime) is not yet available on MCR.
# The two ARGs below default to the available distroless, non-root images. Once the noble tags ship,
# override them without touching anything else:
#
#   docker build \
#     --build-arg SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0-noble \
#     --build-arg RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled .
#
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0-azurelinux3.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:11.0-azurelinux3.0-distroless

# ── Stage 1: Build & pre-compress the Vite + React 19 UI (.zst / .br / .gz) ──────────────────────
FROM node:22-alpine AS ui-build
WORKDIR /ui
COPY src/eh.DockManager.Client/package*.json ./
RUN npm ci --no-audit --no-fund || npm install --no-audit --no-fund
COPY src/eh.DockManager.Client/ ./
# vite.config.ts emits directly to /eh.DockManager.Server/wwwroot and writes .br/.zst/.gz siblings.
RUN npm run build

# ── Stage 2: .NET 11 trimmed + ReadyToRun composite single-file binary ────────────────────────────
FROM ${SDK_IMAGE} AS server-build
WORKDIR /src
COPY global.json ./
COPY src/eh.DockManager.Server/eh.DockManager.Server.csproj ./eh.DockManager.Server/
RUN dotnet restore eh.DockManager.Server/eh.DockManager.Server.csproj -r linux-x64
COPY src/eh.DockManager.Server/ ./eh.DockManager.Server/
COPY --from=ui-build /eh.DockManager.Server/wwwroot ./eh.DockManager.Server/wwwroot/
WORKDIR /src/eh.DockManager.Server
RUN dotnet publish -c Release -r linux-x64 --no-restore -o /app/publish

# ── Stage 3: Distroless, non-root, read-only runtime (no shell, no package manager) ───────────────
FROM ${RUNTIME_IMAGE} AS final
ENV APP_UID=64198 \
    APP_GID=64198 \
    DOTNET_EnableDiagnostics=0 \
    EHDM_DATA_DIR=/data
WORKDIR /app
COPY --from=server-build /app/publish/ ./
USER $APP_UID
EXPOSE 80 443 8443
ENTRYPOINT ["/app/eh-dockmanager"]
