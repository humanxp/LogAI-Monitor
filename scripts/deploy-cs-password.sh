#!/bin/sh
# Password-auth deploy for LogAI Monitor — the same outcome as deploy-cs.sh, but
# for environments where an SSH key cannot be set up (e.g. read-only $HOME) and
# only password auth is available.
#
# Why it is shaped differently from deploy-cs.sh: the key-based script runs
# several local `ssh` hops and stages secrets in local /tmp files. This script
# instead (a) hands the password to ssh via a throwaway SSH_ASKPASS helper and
# (b) does the build + credential read + container recreate entirely on the
# remote host in ONE session, so the four credentials never leave the host and
# nothing is written to the local filesystem.
#
# Usage:
#   # A) password via env (recommended; it never touches a file on disk):
#   SSHPASS='<root password>' sh scripts/deploy-cs-password.sh
#
#   # B) you already have an askpass ssh wrapper (e.g. the workspace helper):
#   SSH='/path/to/.sshhelper/run.sh' sh scripts/deploy-cs-password.sh
#   (SSH must be a single executable path — a wrapper that supplies auth and
#   forwards its arguments to ssh. It is invoked quoted, so keep extra flags
#   inside the wrapper rather than appending them here.)
#
#   # C) override host / build tree:
#   HOST=root@10.0.0.9 TREE=/root/logai-cs SSHPASS='...' sh scripts/deploy-cs-password.sh
#
# NOTE: the fixed `docker run` flags below must stay in sync with
# scripts/deploy-cs.sh (they describe the same live container, see HANDOVER 3.7).

set -e

HOST=${HOST:-root@192.168.50.6}
TREE=${TREE:-/root/logai-cs}
SSHPASS=${SSHPASS:-}
SSH=${SSH:-}

# --- pick the ssh command --------------------------------------------------
if [ -z "$SSH" ]; then
  if [ -z "$SSHPASS" ]; then
    echo "FATAL: set SSH (an askpass ssh wrapper) or SSHPASS (the root password)" >&2
    exit 1
  fi
  TMP=$(mktemp -d "${TMPDIR:-/tmp}/logai-deploy.XXXXXX")
  trap 'rm -rf "$TMP"' EXIT
  chmod 700 "$TMP"
  printf '%s\n' "$SSHPASS" > "$TMP/pw"
  chmod 600 "$TMP/pw"
  # ssh obtains the password by running this helper (no TTY -> askpass).
  printf '#!/bin/sh\ncat "%s/pw"\n' "$TMP" > "$TMP/askpass"
  chmod 700 "$TMP/askpass"
  printf '#!/bin/sh\nSSH_ASKPASS="%s/askpass" SSH_ASKPASS_REQUIRE=force setsid ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null "$@"\n' "$TMP" > "$TMP/sshwrap"
  chmod 700 "$TMP/sshwrap"
  SSH="$TMP/sshwrap"
fi

echo "== 1/3 sync working copy to $HOST:$TREE =="
tar czf - --exclude=bin --exclude=obj \
  src templates wwwroot Dockerfile README.md DEPLOY.md docker-compose.yml .env.example scripts \
  | "$SSH" "$HOST" "rm -rf $TREE/src $TREE/templates $TREE/wwwroot $TREE/scripts && mkdir -p $TREE && tar xzf - -C $TREE"

echo "== 2/3 build + recreate (remote, credentials read from the live container) =="
"$SSH" "$HOST" "TREE='${TREE}' sh -s" <<'REMOTE'
set -eo pipefail
cd "$TREE"
docker build -t logaimonitor-cs:latest . 2>&1 | tail -1

ENV=$(docker inspect logaimonitor --format '{{range .Config.Env}}{{println .}}{{end}}')
SK=$(printf '%s\n' "$ENV" | grep '^SECRET_KEY=' | head -1)
AA=$(printf '%s\n' "$ENV" | grep '^AI_API_KEY=' | head -1)
TB=$(printf '%s\n' "$ENV" | grep '^TELEGRAM_BOT_TOKEN=' | head -1)
TC=$(printf '%s\n' "$ENV" | grep '^TELEGRAM_CHAT_ID=' | head -1)
for v in "$SK" "$AA" "$TB" "$TC"; do
  [ -n "$v" ] || { echo "FATAL: could not read one of the 4 credentials"; exit 1; }
done
echo "got 4 credentials (values not shown)"

run_cmd() {
  docker run -d --name logaimonitor \
    --restart unless-stopped \
    --network logradarai_logaimonitor-net \
    --network-alias redis \
    -p 5059:5059 -p 514:514/udp -p 515:515/tcp \
    -v /var/run/docker.sock:/var/run/docker.sock:ro \
    -v /root/logai-archive:/data \
    -e LOG_ARCHIVE_PATH=/data/logai-archive.db \
    -e AI_BASE_URL=http://192.168.50.23:8000/v1 \
    -e BATCH_SAMPLE_LIMIT=100 \
    -e REDIS_HOST=redis -e REDIS_PORT=6379 \
    -e OLLAMA_MODEL=Llama-3.2-3B-Instruct-4bit \
    -e OLLAMA_HOST=http://host.docker.internal:11434 \
    -e HEALTH_DAILY_SUMMARY=1 -e AI_READ_TIMEOUT=600 -e MAX_LOGS_PER_ANALYSIS=500 \
    -e SYSLOG_PORT=514 -e HEALTH_WATCH_MINUTES=5 -e HEALTH_BACKLOG_WARN=2000 \
    -e AI_PROVIDER=openai -e ANALYSIS_INTERVAL_MINUTES=2 \
    -e LOG_INGEST_TOKEN= -e HEALTH_ALERT_COOLDOWN_MIN=30 -e DEBUG=false \
    -e ALLOW_DB0_WRITES=1 -e DOCKER_COLLECTION=on -e TZ=Asia/Shanghai \
    -e "$SK" -e "$AA" -e "$TB" -e "$TC" \
    logaimonitor-cs:latest
}

# Build replaces the `:latest` tag, which untags the image the running container
# was created from — so the recreate must happen in the same run (see HANDOVER 3.7).
docker rm -f logaimonitor
run_cmd >/dev/null
docker ps --filter name=logaimonitor --format '{{.Names}}|{{.Image}}|{{.Status}}'
REMOTE

echo "== 3/3 verify =="
# The container was recreated a moment ago; ASP.NET needs a few seconds to bind
# port 5059, so poll instead of a single curl (a single curl right after
# recreate returns 000 and looks like a failure when the deploy actually worked).
ok=0
i=0
while [ "$i" -lt 30 ]; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "http://${HOST#*@}:5059/api/health" || echo 000)
  [ "$code" = "200" ] && { ok=1; break; }
  i=$((i + 1))
  sleep 1
done
echo "health=$([ "$ok" = 1 ] && echo 200 || echo "unreachable after ${i}s")"
"$SSH" "$HOST" "docker logs logaimonitor 2>&1 | grep '\[Health\]' | tail -1"
