# Prometheus image with the scrape config baked in (WX-274).
#
# The config is COPYed rather than bind-mounted from the host so the container
# carries its own config and has NO host-filesystem dependency at boot. That is
# what makes it survive a Windows reboot: Docker Desktop auto-starts containers
# (restart: unless-stopped) before the Windows filesystem share is mounted into
# its Linux VM. A single-FILE bind mount whose source isn't resolvable yet gets
# an empty-directory placeholder mounted onto the container's config-FILE path
# -> "not a directory" -> exit 127. With the config in the image, there is no
# host path to race against.
#
# Editing prometheus.yml now requires: docker compose up -d --build
#
# Version pinned (WX-17); bump deliberately and re-check upstream release notes.
FROM prom/prometheus:v3.11.2
COPY prometheus.yml /etc/prometheus/prometheus.yml
