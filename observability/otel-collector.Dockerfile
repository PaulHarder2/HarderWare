# OpenTelemetry Collector image with its config baked in (WX-274).
#
# See prometheus.Dockerfile for the full rationale: the config is COPYed, not
# bind-mounted, to remove the host-filesystem dependency that makes a single-file
# bind mount fail (exit 127) when Docker Desktop auto-starts the container on
# reboot before the Windows FS share is ready inside its Linux VM.
#
# /etc/otelcol/config.yaml is the collector's default config path, so the stock
# entrypoint picks it up with no --config flag needed.
#
# Editing otel-collector-config.yml now requires: docker compose up -d --build
#
# Version pinned (WX-17); bump deliberately and re-check upstream release notes.
FROM otel/opentelemetry-collector:0.150.1
COPY otel-collector-config.yml /etc/otelcol/config.yaml
