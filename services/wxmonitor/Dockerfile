# WX-63 - Containerize WxMonitor.Svc (first of the four WxServices; establishes the pattern
# that WX-64/65/66 reuse). Multi-stage: a fat SDK stage compiles, a lean runtime stage ships.
# The image is deliberately region-agnostic - no connection string, no OPREGION, no secrets -
# all of that is injected at run time by services/docker-compose.yml.
#
# Build context is the HarderWare repo root (the compose service uses `context: ..`), kept
# consistent across all four services so each Dockerfile sees the same repo layout.

# -- STAGE 1: build (full SDK; discarded once the publish output is copied out) --
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish WxServices/src/WxMonitor.Svc/WxMonitor.Svc.csproj \
      -c Release -r linux-x64 --self-contained false \
      -o /app

# -- STAGE 2: runtime (only the .NET runtime, no compiler; this is what ships) --
FROM mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim AS runtime
WORKDIR /opt/wxservices
COPY --from=build /app .
# The one code seam (WxPaths.ReadInstallRoot): resolves the install root to this Linux path
# instead of the appsettings.shared.json C:\HarderWare default. Windows deploys leave it unset.
ENV WXSERVICES_INSTALL_ROOT=/opt/wxservices
ENV DOTNET_RUNNING_IN_CONTAINER=true
# Make the app dir writable by the non-root user. The monitor persists wxmonitor-state.json to
# AppContext.BaseDirectory (= this WORKDIR), which COPY leaves root-owned; without this, the
# UID-1000 process cannot write it (permission-denied every save, and state is never persisted).
RUN chown -R 1000:1000 /opt/wxservices
# Least privilege: drop from the stock root user to an unprivileged UID. The bind-mounted log
# dir must permit this UID to write (verified at first `docker compose up`).
USER 1000:1000
# One main process = the container's lifecycle. When it exits, the container stops.
ENTRYPOINT ["./WxMonitor.Svc"]
