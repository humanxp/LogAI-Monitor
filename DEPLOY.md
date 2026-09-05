# 部署到新机器（Deploying LogAI Monitor on a fresh Debian machine）

## 1. 环境要求
- Debian 11/12 + root
- Docker Engine + Compose v2:
  ```bash
  apt update && apt install -y docker.io docker-compose-v2
  systemctl enable --now docker
  ```

## 2. 拷贝本目录（源码树）到新机器
```bash
# 方式A：用本目录生成的部署包（推荐，含镜像，见下方第3步的导出说明）
# 方式B：直接 rsync/scp 整个 LogRadarAI 目录
```

## 3. 基础镜像（二选一）
本镜像由「公共基础镜像 ftsiadimos/logradaraiq:latest + 补丁文件」构成。
- **方式A（离线，推荐）**：在现网机器导出镜像，拷到新机器导入：
  ```bash
  docker save logradarai:local | gzip > logradarai-image.tar.gz
  # 新机器上：
  gunzip -c logradarai-image.tar.gz | docker load
  ```
- **方式B（需要网络）**：直接用公共基础镜像构建：
  ```bash
  export BASE_IMAGE=ftsiadimos/logradaraiq:latest
  ```

## 4. 配置并启动
```bash
cd LogRadarAI
cp .env.example .env
nano .env            # 填入 SECRET_KEY / AI_BASE_URL / AI_API_KEY / OLLAMA_MODEL / Telegram 等
docker compose build
docker compose up -d
docker ps            # logaimonitor 与 logaimonitor-redis 应为 healthy
```

## 5. 验证与收尾
- 浏览器访问 http://新机器IP:5059，默认账号 **admin / admin**（登录后立即修改！）
- 放行端口：5059/tcp（Web）、514/udp（syslog，可选 515/tcp）
- 日志源机器配置（每台需要上报的主机）：
  ```bash
  echo *.* @新机器IP:514 > /etc/rsyslog.d/99-logaimonitor.conf
  systemctl restart rsyslog
  ```
- 采集 Docker 容器日志：compose 已挂载 /var/run/docker.sock（只读）
- 本机自监控已默认排除（docker_excluded_containers 含 logaimonitor/logaimonitor-redis，存于 Redis 设置）

## 6. 架构说明
- 运行：gunicorn（gthread，1 worker / 40 线程），监听 0.0.0.0:5059
- AI 分析：每 2 分钟 或 积压满 MAX_LOGS_PER_ANALYSIS 条立即触发（谁先到算谁）；
  每批取最旧未分析日志，按严重级别排序后取前 BATCH_SAMPLE_LIMIT 条发给 AI
- 存储：Redis（日志带 TTL=LOG_RETENTION_HOURS；AI 历史 30 天）
- Telegram：仅 AI 判定 critical（或过滤器告警达 alert_on_critical/alert_on_error 级别）才推送
