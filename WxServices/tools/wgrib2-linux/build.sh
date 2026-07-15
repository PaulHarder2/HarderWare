#!/usr/bin/env bash
# Rebuild tools/wgrib2-linux/wgrib2 — the Linux x86-64 wgrib2 for the containerized WxParser.Svc.
# Produces the binary next to this script. Requires Docker. See README.md for the full rationale.
#
# WHY docker + debian:bookworm (do NOT change casually): the binary must run on the WxParser
# runtime image (mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim = glibc 2.36). Building on the
# SAME distro family guarantees the glibc it needs is <= the runtime's. Building on a NEWER distro
# (e.g. Ubuntu 24.04 = glibc 2.39) yields a binary that fails on bookworm-slim with
# "version `GLIBC_2.xx' not found" — which is exactly why the previous Ubuntu-built binary was
# replaced. If the runtime base image ever moves off bookworm, change the base here to match it.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --platform=linux/amd64: the committed binary must be x86-64 (to match the amd64 container
# runtime); pin the build so a rebuild on an ARM host still produces an amd64 binary rather than
# a wrong-arch one that would fail the verify step below and at container start.
docker run --rm --platform=linux/amd64 -v "$HERE":/out debian:bookworm bash -c '
  set -e
  apt-get update -qq
  # cmake is needed to build the bundled AEC (CCSDS) library; gfortran for the Fortran bits.
  apt-get install -y -qq build-essential gfortran cmake wget file
  cd /tmp
  wget -q https://www.ftp.cpc.ncep.noaa.gov/wd51we/wgrib2/wgrib2.tgz
  # This is NOAA rolling "latest" (not version-pinned) - log the checksum so a rebuild is at least
  # traceable to what it fetched. Deterministic pinning (fixed URL + verified checksum) is WX-296.
  echo "fetched wgrib2.tgz: $(sha256sum wgrib2.tgz | cut -c1-16)... ($(stat -c%s wgrib2.tgz) bytes)"
  tar xf wgrib2.tgz
  cd grib2
  # Full build: bundles jasper/libpng/libaec statically (so decode covers GFS packing) and leaves
  # USE_IPOLATES/USE_OPENMP on. A lean build (both 0) fights the makefile (USE_SPECTRAL requires
  # USE_IPOLATES); the two extra runtime deps (libgfortran5, libgomp1) are trivial. See README.
  export CC=gcc FC=gfortran
  make
  ./wgrib2/wgrib2 -version
  cp wgrib2/wgrib2 /out/wgrib2
'

echo ""
echo "Rebuilt: $HERE/wgrib2"
echo "Now verify it runs on the runtime base (not just wherever it built):"
echo "  docker run --rm --platform=linux/amd64 -v \"$HERE\":/t:ro debian:bookworm-slim \\"
echo "    bash -c 'apt-get update -qq && apt-get install -y libgfortran5 libgomp1 >/dev/null && /t/wgrib2 -version'"
