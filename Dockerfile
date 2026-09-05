# Patched LogRadarAI / LogAI Monitor image.
#
# Base image is overridable so the same tree builds on any machine.
# Default is the OFFICIAL public base (cached locally after first pull). We
# deliberately do NOT stack on the custom image (FROM logradarai:local) - every
# such rebuild appended its layers and the image grew to ~500 layers, which
# broke the overlay filesystem (build error: mount ... invalid argument).
ARG BASE_IMAGE=ftsiadimos/logradaraiq:latest
FROM ${BASE_IMAGE}

# Patches (each COPY overwrites the base version):
COPY config.py /app/config.py
COPY services/ollama_analyzer.py /app/services/ollama_analyzer.py
COPY services/redis_client.py /app/services/redis_client.py
COPY services/syslog_receiver.py /app/services/syslog_receiver.py
COPY services/telegram_notifier.py /app/services/telegram_notifier.py
COPY app.py /app/app.py
COPY static/js/app.js /app/static/js/app.js
COPY templates/settings.html /app/templates/settings.html
COPY templates/index.html /app/templates/index.html
COPY templates/ai_history.html /app/templates/ai_history.html
COPY templates/about.html /app/templates/about.html
COPY templates/logs.html /app/templates/logs.html
COPY templates/users.html /app/templates/users.html
COPY templates/docker.html /app/templates/docker.html
COPY templates/alerts.html /app/templates/alerts.html
COPY templates/clients.html /app/templates/clients.html
COPY templates/base.html /app/templates/base.html
