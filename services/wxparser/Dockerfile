# WX-66 - Containerize WxParser.Svc (Step 4, final, of WX-7). WxParser fetches METAR/TAF (HTTP) and
# GFS GRIB2 (byte-range) and writes them to the DB; it shells out to wgrib2 to extract the GFS
# sub-grid. The host Windows service uses the native wgrib2.exe (WX-33); this image bundles a Linux
# wgrib2 (see WxServices/tools/wgrib2-linux/). Like WX-63/64/65 the image is region-agnostic - no
# connection string, secrets, or install root baked in; all injected at run time by the compose file.
#
# Build context is the HarderWare repo root (the compose service uses `context: ..`).

# -- STAGE 1: build (full SDK; discarded once the publish output is copied out) --
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish WxServices/src/WxParser.Svc/WxParser.Svc.csproj \
      -c Release -r linux-x64 --self-contained false \
      -o /app

# -- STAGE 2: runtime (.NET runtime + the Linux wgrib2 + its libs; this is what ships) --
FROM mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim AS runtime

# wgrib2's runtime shared-lib deps. The binary bundles jasper/libpng/libaec statically, so only these
# are dynamic; libc/libm/libmvec come from the base. See WxServices/tools/wgrib2-linux/README.md.
RUN apt-get update && apt-get install -y --no-install-recommends libgfortran5 libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# The bundled Linux wgrib2 (built on bookworm for glibc-2.36 parity with this base - see
# tools/wgrib2-linux/). GribExtractor invokes it by absolute path; the container config points
# Gfs:Wgrib2Path here.
COPY WxServices/tools/wgrib2-linux/wgrib2 /usr/local/bin/wgrib2
RUN chmod +x /usr/local/bin/wgrib2

WORKDIR /opt/wxservices
COPY --from=build /app .

# The code seam: WxPaths.ReadInstallRoot() honours this env var, and Program.cs's AddInstallRoot
# projects it into IConfiguration["InstallRoot"] so the workers resolve the heartbeat + GRIB temp dir
# to this Linux root instead of the appsettings.shared.json C:\HarderWare default.
ENV WXSERVICES_INSTALL_ROOT=/opt/wxservices
ENV DOTNET_RUNNING_IN_CONTAINER=true

# GRIB download/extract scratch dir (WxPaths.TempDir = InstallRoot/temp). The root-owned WORKDIR is
# read-only to UID 1000, so create it and hand it to the runtime user. Ephemeral (files are deleted
# after each fetch cycle), so no host mount is needed.
RUN mkdir -p /opt/wxservices/temp && chown 1000:1000 /opt/wxservices/temp

# Least privilege: drop from root to an unprivileged UID (matches the other service images).
USER 1000:1000
# One main process = the container's lifecycle. When WxParser.Svc exits, the container stops.
ENTRYPOINT ["./WxParser.Svc"]
