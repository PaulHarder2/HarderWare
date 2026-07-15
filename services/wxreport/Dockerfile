# WX-64 - Containerize WxReport.Svc (second of the four WxServices; reuses the WX-63 pattern that
# WX-65/66 also follow). Multi-stage: a fat SDK stage compiles, a lean runtime stage ships. The
# image is deliberately region-agnostic - no connection string, no secrets, no install root baked
# in - all injected at run time by services/docker-compose.yml.
#
# Build context is the HarderWare repo root (the compose service uses `context: ..`), kept
# consistent across all four services so each Dockerfile sees the same repo layout.

# -- STAGE 1: build (full SDK; discarded once the publish output is copied out) --
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish WxServices/src/WxReport.Svc/WxReport.Svc.csproj \
      -c Release -r linux-x64 --self-contained false \
      -o /app

# -- STAGE 2: runtime (only the .NET runtime, no compiler; this is what ships) --
FROM mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim AS runtime
WORKDIR /opt/wxservices
COPY --from=build /app .
# The code seam: WxPaths.ReadInstallRoot() honours this env var, and WX-64's AddInstallRoot projects
# it into IConfiguration["InstallRoot"] too - so BOTH startup paths AND the workers (which read that
# config key to find plots/heartbeat) resolve to this Linux root instead of the appsettings.shared.json
# C:\HarderWare default. Windows deploys leave it unset and fall through to the shared config.
ENV WXSERVICES_INSTALL_ROOT=/opt/wxservices
ENV DOTNET_RUNNING_IN_CONTAINER=true
# Least privilege: drop from the stock root user to an unprivileged UID. Unlike WxMonitor (which
# persists wxmonitor-state.json to this WORKDIR and therefore needs it chown'd), WxReport writes
# NOTHING to the image layer - it only READS baked-in content from here (AboutPaul.md, the shared
# config). Every runtime write - log, heartbeat, meteogram reads, translation-qa packages - targets
# a bind-mounted dir whose host permissions govern access. COPY leaves those read files root-owned
# and world-readable, which is all this UID needs, so no chown is required.
USER 1000:1000
# One main process = the container's lifecycle. When it exits, the container stops.
ENTRYPOINT ["./WxReport.Svc"]