# File: Dockerfile
# Stage 1: Build & Pre-Compress Vite + React 19 UI (.zst & .br)
FROM node:22-alpine AS ui-build
WORKDIR /ui
COPY src/eh.DockManager.Client/package*.json ./
RUN npm install
COPY src/eh.DockManager.Client/ ./
RUN npm run build

# Stage 2: Build .NET 11 Trimmed + ReadyToRun Composite Single-File Binary
FROM mcr.microsoft.com/dotnet/sdk:11.0-noble AS server-build
WORKDIR /src
COPY global.json ./
COPY src/eh.DockManager.Server/eh.DockManager.Server.csproj ./eh.DockManager.Server/
RUN dotnet restore eh.DockManager.Server/eh.DockManager.Server.csproj -r linux-x64
COPY src/eh.DockManager.Server/ ./eh.DockManager.Server/
COPY --from=ui-build /eh.DockManager.Server/wwwroot ./eh.DockManager.Server/wwwroot/
WORKDIR /src/eh.DockManager.Server
RUN dotnet publish -c Release -r linux-x64 --no-restore -o /app/publish

# Stage 3: Hardened Ubuntu 24.04 Chiseled Distroless Runtime (No shell, No root)
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled AS final
WORKDIR /app
COPY --from=server-build /app/publish/ ./
USER $APP_UID
EXPOSE 443 8443
ENTRYPOINT ["/app/eh-dockmanager"]