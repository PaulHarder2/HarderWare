# Grafana image with provisioning + dashboards baked in (WX-278).
#
# See prometheus.Dockerfile for the shared boot-race rationale (Docker Desktop
# auto-starts containers on Windows reboot before the filesystem share is ready
# in its Linux VM, so a host bind mount's source is unresolved). WX-274 baked the
# two config-FILE mounts for that reason; this bakes grafana's config DIRECTORIES
# for the same reason -- with one twist worth recording: a directory mount does
# NOT fail loudly like a file mount (exit 127). It comes up as an EMPTY
# placeholder, so grafana starts, the provisioner registers zero dashboards, and
# bookmarked UIDs 404 ("Invalid dashboard UID") -- a silent failure the 30s
# provider re-scan never self-heals (proven on the 2026-07-09 reboot).
#
# Dashboards bake to /etc/grafana/dashboards, NOT /var/lib/grafana/dashboards:
# the grafana-data named volume mounts at /var/lib/grafana and would SHADOW baked
# content there. The provider path in provisioning/dashboards/dashboards.yml
# points at /etc/grafana/dashboards to match.
#
# Editing a dashboard or provisioning file now requires a rebuild:
#   docker compose up -d --build grafana
#
# Version pinned (WX-17); bump deliberately and re-check upstream release notes.
FROM grafana/grafana:11.4.0
COPY grafana/provisioning /etc/grafana/provisioning
COPY grafana/dashboards  /etc/grafana/dashboards
