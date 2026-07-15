# Runbook — Operating the WxServices container stack

How to start, stop, update, and recover the containerized WxServices stack, and what to expect
across a host reboot. Written for WX-68 (Step 6 of the WX-7 containerization epic).

## What's running

As of WX-7, the four headless services run as **Docker containers** (Docker Desktop on the Windows
host), not Windows services. Two Compose projects:

| Compose project | Containers | Defined in |
|---|---|---|
| `services` | `wxmonitor`, `wxreport`, `wxvis`, `wxparser` | `services/docker-compose.yml` |
| `observability` | `otel-collector`, `prometheus`, `grafana` | the observability compose (WX-16) |

Everything reaches the host through `host.docker.internal`:
- **SQL Server** — native `SQLEXPRESS` on the host at `host.docker.internal,1433` (SQL auth, WX-67).
- **OTel collector** — the `otel-collector` container's published `4318`, via `host.docker.internal:4318`.

The four services never talk to each other directly — they coordinate through the shared database.
`WxManager` / `WxViewer` remain **native** Windows apps and read the same host files (logs, plots).

**Host prerequisites** for the stack to run and self-recover (full list: `DEVELOPER-README.md`, WX-298):
- `SQLEXPRESS` running (the containers' database).
- Docker Desktop installed with **autostart-on-login** enabled, and Windows **auto-login** on
  (so the engine starts unattended after a reboot — see *Reboot recovery*).

## Restart policy (WX-68)

Each service container carries **`restart: unless-stopped`** (`services/docker-compose.yml`). This means
the Docker engine restarts the container:
- when it **crashes or exits** non-zero, and
- when the **engine itself starts** (e.g. after a host reboot + Docker Desktop autostart),

**unless** it was **explicitly stopped** (`docker compose stop`). That's the one deliberate exception:
a container you stopped by hand stays stopped across an engine restart — it will not surprise you by
coming back. (Contrast `always`, which restarts even a hand-stopped container, and `no`, the Compose
default, which never auto-restarts — the four services were `no` before WX-68, so a reboot left the
whole forecasting stack down.)

Confirm the live policy any time:
```bash
docker inspect --format '{{.Name}} restart={{.HostConfig.RestartPolicy.Name}} state={{.State.Status}}' \
  services-wxmonitor-1 services-wxreport-1 services-wxvis-1 services-wxparser-1
```
Expect `restart=unless-stopped state=running` on all four.

## Start the stack

**Blessed path** (from an elevated PowerShell — builds, verifies each container reached
`Application started`, writes the deploy-history line, and prints the status summary):
```powershell
cd C:\Code\HarderWare\WxServices
.\Deploy-WxService.ps1 all            # all four services + WxManager/WxViewer
.\Deploy-WxService.ps1 WxParserSvc    # a single service
```

**Direct Compose** (equivalent for the services, no deploy-history line / summary):
```powershell
cd C:\Code\HarderWare\services
docker compose up -d --build          # build if needed, then start/recreate all four
docker compose up -d wxparser         # a single service
```
`docker compose up -d` recreates a container when its definition changed (e.g. after this WX-68
restart-policy change lands) and leaves unchanged containers alone.

## Stop the stack

```powershell
cd C:\Code\HarderWare\services
docker compose stop                   # stop containers, KEEP them (fast restart later; survives reboot as stopped)
docker compose stop wxparser          # one service
docker compose down                   # stop AND REMOVE containers + the default network (takes no service name)
```
Prefer **`stop`** for routine pauses — it's reversible with `start` and, because the policy is
`unless-stopped`, a `stop`ped container stays down until you `start` it (even across a reboot). Use
**`down`** only when you want to tear the containers down entirely (a later `up` recreates them).

## Restart / update a service

```powershell
cd C:\Code\HarderWare\services
docker compose restart wxvis                 # bounce a running service (no rebuild)
docker compose up -d --build wxvis           # rebuild the image + recreate (after a code/Dockerfile change)
```
For a real deploy of new code, use `.\Deploy-WxService.ps1 <Svc>` so the deploy-history line and
verification run.

## Status & health

```powershell
cd C:\Code\HarderWare\services
docker compose ps                            # one-line status per service
docker logs --tail 50 services-wxreport-1    # recent log for one container
docker stats --no-stream                     # live CPU/mem
```
Host-side, each service also writes its log + (WxParser/WxReport) heartbeat under
`C:\HarderWare\Logs\` (e.g. `wxreport-svc.log`, `wxreport-heartbeat.txt`).

## Reboot recovery (the point of WX-68)

On a host reboot:
1. Windows **auto-login** signs in unattended (no one at the keyboard).
2. That fires Docker Desktop's autostart → the **engine** comes up (this can take several minutes on
   a congested boot while security/OEM services contend for CPU — it does come up).
3. The engine restarts every `restart: unless-stopped` container → **all four services + the
   observability stack return**, no `docker compose up` needed.

**Verify after a reboot** (no manual action first):
```bash
docker compose -f C:/Code/HarderWare/services/docker-compose.yml ps        # all four Up
# or, quick policy+state check:
docker inspect --format '{{.Name}} {{.State.Status}}' \
  services-wxmonitor-1 services-wxreport-1 services-wxvis-1 services-wxparser-1
```
Then confirm the forecasting stack actually resumed: a fresh report cycle in `wxreport-svc.log`, and
recent `wxparser-heartbeat.txt` / `wxreport-heartbeat.txt` mtimes.

**If a service did NOT come back:**
- Engine not up yet? Give Docker Desktop a few more minutes on a congested boot; re-check.
- `SQLEXPRESS` not up? The container may be crash-looping on a failed DB connect (until WX-299 adds
  connection-retry). Confirm SQL is running, then `docker compose up -d <svc>`.
- Was it hand-stopped before the reboot? `unless-stopped` keeps it stopped by design — `docker
  compose start <svc>`.

## ⚠️ Reboot test — back up FIRST

Before **any** deliberate reboot test on this (fragile, pre-replacement) host, run the daily backup so
a failed recovery can't cost data:
```bash
bash /mnt/c/Code/tools/backup-refresh.sh
```
Only then reboot. This is a required pre-step in WX-68's §13.

## See also
- `DEVELOPER-README.md` — full host system-configuration for a reboot-survivable box (WX-298).
- Boot watchdog (`C:\HarderWare\service-watchdog.ps1`) — being reconciled for the container world (WX-297).
- DB-connection resiliency at startup — WX-299.
