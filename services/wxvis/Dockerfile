# WX-65 - Containerize WxVis.Svc (Step 3 of WX-7). The most complex of the service images: the
# .NET worker shells out to the WxVis Python scripts (matplotlib + cartopy), so the runtime stage
# carries a Python render stack alongside the .NET 8 runtime. Like WX-63/64 the image is
# region-agnostic - no connection string, secrets, or install root baked in; all injected at run
# time by services/docker-compose.yml.
#
# Build context is the HarderWare repo root (the compose service uses `context: ..`).

# -- STAGE 1: build (full SDK; discarded once the publish output is copied out) --
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish WxServices/src/WxVis.Svc/WxVis.Svc.csproj \
      -c Release -r linux-x64 --self-contained false \
      -o /app

# -- STAGE 2: runtime (.NET runtime + the Python render stack; this is what ships) --
FROM mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim AS runtime

# System deps for the render pipeline:
#  - python3/pip           : the .NET workers spawn the WxVis Python scripts.
#  - fonts-dejavu/liberation: matplotlib's default fonts, so map/meteogram text matches the host.
#  - unixodbc + msodbcsql17 : pyodbc's driver, matching WxVis.DbDriver ("ODBC Driver 17 for SQL
#                             Server") so the container's Python DB path is identical to the host's
#                             except for the auth mode (WX-65 adds SQL-login support to db.py).
# msodbcsql17 lives in Microsoft's apt repo (registered by packages-microsoft-prod.deb) and its
# install requires accepting the EULA via ACCEPT_EULA=Y.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
    && curl -sSL -O https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends \
         python3 python3-pip \
         fonts-dejavu fonts-liberation \
         unixodbc msodbcsql17 \
    && rm -rf /var/lib/apt/lists/*

# WxVis Python dependencies (pinned in requirements.txt). cartopy/shapely ship manylinux wheels
# that bundle their native GEOS/PROJ, so no -dev libraries or C compiler are needed here.
COPY WxServices/src/WxVis/requirements.txt /tmp/requirements.txt
RUN pip3 install --no-cache-dir --break-system-packages -r /tmp/requirements.txt

# matplotlib and cartopy need a writable HOME for their font/config caches and cartopy's Natural
# Earth data. Point HOME at a dedicated writable dir owned by the runtime user (the /opt/wxservices
# WORKDIR below is root-owned and read-only to this UID); MPLCONFIGDIR pins matplotlib's cache there.
ENV HOME=/var/cache/wxvis
ENV MPLCONFIGDIR=/var/cache/wxvis/matplotlib
# Pre-bake cartopy's 50m Natural Earth data - the only scale the WxVis scripts use (LAND, OCEAN,
# LAKES, COASTLINE, BORDERS, STATES, RIVERS) - so the first render does not depend on a runtime
# download from naturalearth.s3.amazonaws.com (which would otherwise re-fetch on every recreate and
# fail when the host is offline). Placed right after the pip layer it depends on and BEFORE the app
# COPY, so Docker caches it across code changes instead of re-downloading on every source edit. Runs
# as root with HOME set, then chowns the cache tree so the runtime UID can read it.
RUN mkdir -p "$MPLCONFIGDIR" \
    && python3 -c "import cartopy.feature as c; [list(f.with_scale('50m').geometries()) for f in (c.LAND, c.OCEAN, c.LAKES, c.COASTLINE, c.BORDERS, c.STATES, c.RIVERS)]" \
    && chown -R 1000:1000 /var/cache/wxvis

WORKDIR /opt/wxservices
COPY --from=build /app .
# The WxVis Python scripts live at InstallRoot/WxVis (WxPaths.WxVisDir); the .NET workers shell out
# to them there. Source is WxServices/src/WxVis (not WxServices/WxVis).
COPY WxServices/src/WxVis /opt/wxservices/WxVis

# The code seam: WxPaths.ReadInstallRoot() honours this env var, and Program.cs's AddInstallRoot
# projects it into IConfiguration["InstallRoot"] so the map workers resolve the script dir and the
# plots output dir to this Linux root instead of the appsettings.shared.json C:\HarderWare default.
ENV WXSERVICES_INSTALL_ROOT=/opt/wxservices
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Least privilege: drop from root to an unprivileged UID (matches the other service images).
USER 1000:1000
# One main process = the container's lifecycle. When WxVis.Svc exits, the container stops.
ENTRYPOINT ["./WxVis.Svc"]
