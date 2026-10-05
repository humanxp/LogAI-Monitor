#!/bin/sh
# Build the .NET image on the build host and recreate the production container.
#
#   sh scripts/deploy-cs.sh
#
# Why this script exists: the running `logaimonitor` container was NOT created by
#
# NOTE on --network-alias redis: the app reaches Redis as REDIS_HOST=redis, and the
# Redis container's own name is logaimonitor-redis. The short name "redis" only
# resolves because the original compose-created network gave the redis service that
# alias. Recreating the Redis container with a bare `docker run` (as happened once)
# silently drops the alias, the app then cannot resolve "redis" at all, and the
# background subsystems never start. Declaring the alias here makes the app side
# independent of how the Redis container was created.
# docker-compose (the Redis network is named `logradarai_logaimonitor-net`, from a
# compose project that is no longer on the host), so `docker compose up -d` cannot
# recreate it. Its full run configuration is reconstructed here from the live
# container, and the four credential/endpoint variables are carried over with
# `docker inspect` instead of being copied into this file.
#
# The build replaces the `logaimonitor-cs:latest` tag, which UNTAGS the image the
# running container was created from. That is why the container is recreated in the
# same run: leaving it running would break the next `docker start`.

set -e

HOST=${HOST:-root@192.168.50.6}
TREE=${TREE:-/root/logai-cs}
# Key-based ssh is required: the "read the live container's secrets" step runs ssh
# from inside this script, and an interactive password prompt cannot be answered
# there. Generate one with:
#   ssh-keygen -t ed25519 -N "" -f ~/.ssh/logai_deploy
#   ssh-copy-id -i ~/.ssh/logai_deploy.pub root@<host>
KEY=${KEY:-~/.ssh/logai_deploy}
SSH_OPTS=${SSH_OPTS:-}
SSH="ssh -i $KEY -o BatchMode=yes -o PasswordAuthentication=no $SSH_OPTS"

echo "== 1/4 sync working copy to $HOST:$TREE =="
tar czf - --exclude=bin --exclude=obj \
  src templates wwwroot Dockerfile README.md DEPLOY.md docker-compose.yml .env.example scripts \
  | $SSH "$HOST" "rm -rf $TREE/src $TREE/templates $TREE/wwwroot $TREE/scripts && mkdir -p $TREE && tar xzf - -C $TREE"

echo "== 2/4 read the live container's secrets so they survive the swap =="
read_var() { $SSH "$HOST" "docker inspect logaimonitor --format '{{range .Config.Env}}{{println .}}{{end}}' | grep '^$1=' | head -1"; }
read_var SECRET_KEY     > /tmp/_k
read_var AI_API_KEY     > /tmp/_a
read_var TELEGRAM_BOT_TOKEN > /tmp/_t
read_var TELEGRAM_CHAT_ID   > /tmp/_c
for f in /tmp/_k /tmp/_a /tmp/_t /tmp/_c; do
  [ -s "$f" ] || { echo "FATAL: could not read $(basename $f) from the live container"; exit 1; }
done

echo "== 3/4 build =="
$SSH "$HOST" "cd $TREE && docker build -t logaimonitor-cs:latest . 2>&1 | tail -5"

echo "== 4/4 recreate the container =="
{
  cat <<'HEAD'
set -e
docker rm -f logaimonitor
docker run -d --name logaimonitor \
  --restart unless-stopped \
  --network logradarai_logaimonitor-net \
  --network-alias redis \
  -p 5059:5059 -p 514:514/udp -p 515:515/tcp \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
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
HEAD
  printf '  -e %s \\\n' "$(cat /tmp/_k)"
  printf '  -e %s \\\n' "$(cat /tmp/_a)"
  printf '  -e %s \\\n' "$(cat /tmp/_t)"
  printf '  -e %s \\\n' "$(cat /tmp/_c)"
  echo "  logaimonitor-cs:latest"
  echo 'docker ps --filter name=logaimonitor --format "{{.Names}}|{{.Image}}|{{.Status}}"'
} > /tmp/_run

$SSH "$HOST" 'cat > /tmp/recreate.sh && sh /tmp/recreate.sh && rm -f /tmp/recreate.sh' < /tmp/_run
rm -f /tmp/_k /tmp/_a /tmp/_t /tmp/_c /tmp/_run

echo "== verify =="
curl -s -o /dev/null -w 'health=%{http_code}\n' "http://${HOST#*@}:5059/api/health"
$SSH "$HOST" "docker logs logaimonitor 2>&1 | grep '\[Health\]' | tail -1"
