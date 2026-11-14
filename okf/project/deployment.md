---
type: Runbook
title: Host preparation
description: Host-side steps required before the compose stack starts: bind-mount ownership, the host docker group id, and the empty-password bootstrap fallback.
tags: [deployment, compose, permissions, bootstrap]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T12:14:28Z }
sources:
  - id: compose-file
    resource: ../../docker-compose.yml
    title: docker-compose.yml — the stack definition
  - id: dockerfile
    resource: ../../Dockerfile
    title: Dockerfile — distroless runtime image
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — startup, data directories, bootstrap admin
  - id: env-example
    resource: ../../.env.example
    title: .env.example — host-local values template
---

# Purpose

`docker compose up` on a fresh host fails with `UnauthorizedAccessException` on
`/data/stacks` before the app starts. The image is distroless and runs as uid `64198`
(`USER $APP_UID` in the `Dockerfile`), while Docker creates a missing bind-mount
directory as `root:root`. The container therefore cannot create the `/data/stacks`,
`/data/certs`, `/data/keys`, or `/data/dockmanager.db` entries. This runbook records the
host-side steps that make the first run succeed.

# Host preparation

1. **Bind-mount ownership.** The compose file mounts `./data:/data`. Create the host
   directory and give it to the container's uid/gid:

   ```bash
   mkdir -p data
   sudo chown -R 64198:64198 data
   ```

   `64198` is the fixed `$APP_UID` in the `Dockerfile`; no host user or group of that
   name needs to exist. The app creates its own subdirectories (`stacks`, `certs`,
   `keys`, `.master.key`) under the writable `/data` root.

2. **Docker group id.** The container needs the host's docker group id to read
   `/var/run/docker.sock` (mode `660 root:docker`). The compose file passes
   `group_add: "${DOCKER_GID:-999}"`; set the host's actual gid in `.env`:

   ```bash
   getent group docker | cut -d: -f3
   # then write e.g. DOCKER_GID=988 into .env
   ```

   `.env.example` is the committed template; `.env` is host-local and gitignored. The
   default `999` is only correct on hosts whose docker group is 999.

3. **First-run bootstrap admin.** The compose file exports
   `EHDM_ADMIN_PASSWORD=${EHDM_ADMIN_PASSWORD:-}`. An empty value is treated as unset
   after the fix in `Program.cs`: the app generates a random 16-byte password and logs
   `Initial admin credentials -> user: admin password: <random>` once. Set
   `EHDM_ADMIN_PASSWORD` in `.env` to choose a password; the generated value is only
   logged when the users table is empty.

# Verification

```bash
docker compose up -d
docker compose ps                       # container is Up
curl -sk https://127.0.0.1/api/system/health   # 200
docker logs eh-dockmanager | grep -E 'Initial admin|Seeded the local Docker host'
```

A successful login with the generated password and `GET /api/containers` returning the
container list confirms the Docker socket is reachable through the `group_add` gid.

# Notes

* The host's `./data` directory is owned by `64198:64198`; the app's own subdirectories
  are `64198:root` because the image's `USER $APP_UID` sets the uid but leaves the
  primary gid at 0. That is expected and does not affect the app.
* If the host already ran the app with an empty password, the users table is not empty;
  delete `/data/dockmanager.db` (or change the password in the UI) before the next
  bootstrap. The bootstrap only runs when the users table is empty.
* **`.env` changes to `EHDM_ADMIN_USER`/`EHDM_ADMIN_PASSWORD` have no effect once the
  users table is non-empty** — the values only seed the *first* admin. A login with the
  `.env` password then fails with `401 {"error":"Invalid credentials."}` (surfaced in the
  UI only as a ~3.2 s toast), which looks like the env not loading but is not. To make the
  `.env` value take effect without wiping the rest of the database, stop the stack and
  clear just the `Users` rows instead of the whole file:

  ```bash
  docker compose stop          # checkpoints the WAL so no -wal/-shm sidecar remains
  # edit data/dockmanager.db: DELETE FROM Users; (and Passkeys), or delete the file
  docker compose up -d         # bootstrap re-runs and logs the credentials once
  ```

  The `.db` is owned by `64198:root` mode `644`, so on a host where the operator is not
  `64198`/root it can only be edited through a root container (e.g. `busybox`) or `sudo`.
* **Image name.** The stack runs the Docker Hub image `ehudhazan/dockmanager:latest`
  (`docker-compose.yml` keeps its `build:` section, so `./build.sh` / `docker compose build`
  still retag it locally). The container and compose project are still named `eh-dockmanager`,
  so `docker logs eh-dockmanager` and the `.env`/`./data` steps above are unchanged.
