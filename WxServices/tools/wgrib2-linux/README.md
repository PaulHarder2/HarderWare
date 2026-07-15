# Linux `wgrib2` for the containerized WxParser.Svc (WX-66)

A prebuilt **Linux x86-64** `wgrib2` binary, committed here so the WxParser container image can
`COPY` it in rather than compile it on every build.

| | |
|---|---|
| **Version** | `wgrib2 v3.1.3` (10/2023) |
| **Source** | NOAA CPC — `https://www.ftp.cpc.ncep.noaa.gov/wd51we/wgrib2/wgrib2.tgz` |
| **Built on** | `debian:bookworm` (glibc 2.36) — see *Why bookworm* below |
| **Built** | 2026-07-15 (WX-66) |
| **Config** | Full build (`USE_IPOLATES`/`USE_OPENMP` on; `jasper`/`libpng`/`libaec` bundled **statically**) |
| **Rebuild** | run `./build.sh` (this folder) — Docker required |

## What it's for

`WxParser.Svc` reads GFS numerical-forecast data from NOMADS/AWS as GRIB2 and extracts a regional
sub-grid via `wgrib2`. `GribParser/GribExtractor.cs` shells out to it twice per file:

1. `wgrib2 <in> -small_grib <lon0:lon1> <lat0:lat1> <out>` — crop to the bounding box, then
2. `wgrib2 <sub> -csv <out.csv>` — emit the sub-grid as CSV, which the parser reads into `GfsGrid`.

The **Linux container is the deployment path** (WX-66; all four services are containers, WX-7) — and
`wgrib2` is **not in Debian's apt repos**, so we bundle this prebuilt binary. The WxParser Dockerfile
copies it in and points `Gfs:Wgrib2Path` at it. The **native Windows service** — now only a
reversible/manual fallback — used NOAA's Windows `wgrib2.exe` at `{InstallRoot}\wgrib2\wgrib2.exe`
(WX-33 moved off the old WSL-invoked build); that host executable is not used by the container.

## ⚠️ Why bookworm (the glibc trap)

This binary is **dynamically linked** and must run on the WxParser runtime image
(`mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim`, **glibc 2.36**). It is therefore built on
`debian:bookworm` so every glibc symbol it needs is present on the runtime base.

The previous binary in this repo was built in **WSL Ubuntu** (glibc 2.39) and required
`GLIBC_2.38`, which bookworm-slim lacks — so it failed at container start with
``version `GLIBC_2.38' not found``. **Always rebuild on the same distro family as the runtime
image** (or older). `build.sh` does this by building inside `debian:bookworm`.

## Runtime dependencies (what the image must provide)

Only `libc`/`libm`/`libmvec` come from the base image; the full build also needs:

```
libgfortran5   libgomp1   (which pull libquadmath0, libgcc-s1)
```

So the WxParser Dockerfile installs them:

```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends libgfortran5 libgomp1 \
    && rm -rf /var/lib/apt/lists/*
```

`jasper` / `libpng` / `libaec` are **statically bundled** into the binary, so they are *not* runtime
deps. Decode support built in: `simple, complex, rle, ieee, png, jpeg2000, CCSDS AEC` (covers GFS
packing). Confirm the current deps any time with `ldd wgrib2`.

## Verifying a (re)build

After `./build.sh`, confirm it actually runs on the runtime base — not just wherever it was built:

```bash
# --platform=linux/amd64 so this exercises the x86-64 binary even on an ARM host
# (without it, debian:bookworm-slim pulls arm64 and the binary fails "Exec format error").
docker run --rm --platform=linux/amd64 -v "$PWD":/t:ro debian:bookworm-slim \
  bash -c 'apt-get update -qq && apt-get install -y libgfortran5 libgomp1 >/dev/null && /t/wgrib2 -version'
```

For a full functional check, run `-small_grib` then `-csv` on a real GFS GRIB2 file and confirm the
CSV parses — this is part of the WX-66 §13 test procedure.

## Notes

- The binary is ~8 MB and **not stripped** (keeps `debug_info`). `strip wgrib2` shrinks it to ~1.5 MB
  if image size matters; kept unstripped here for debuggability.
- A **lean** build (`USE_IPOLATES=0 USE_OPENMP=0` → `libc`+`libm` only, matching the old binary) was
  attempted but fights the makefile (`USE_SPECTRAL=1 requires USE_IPOLATES`); the two extra runtime
  deps are trivial, so we ship the full build.
- Longer term, building `wgrib2` **in the Dockerfile** (no committed binary) is the fully
  reproducible option — tracked with the image-pinning work in **WX-296**.
