# 部署到新机器

LogAI Monitor（.NET 8 版）在一台全新 Linux 机器上的完整部署步骤。按顺序做完即可访问。

---

## 1. 环境要求

| 项 | 要求 |
|---|---|
| 系统 | Linux x86_64，已装 Docker（建议 20.10+）与 Docker Compose v2 |
| 端口 | **5059/tcp**（Web 界面）、**514/udp** 与 **515/tcp**（syslog 接收） |
| 内存 | 建议 ≥ 4GB。其中大部分留给 Redis（见第 3 步的容量估算） |
| 磁盘 | 按保留期估算：默认保留 720 小时（30 天），当前实测约 5–7GB |
| 可选 | 能访问 AI 端点（vLLM / SGLang / Ollama） |

确认端口没被占用：

```bash
ss -lunp | grep -E ':514|:515' ; ss -ltnp | grep ':5059'
```

---

## 2. 把镜像放到新机器上

镜像**不在任何 registry 里**，二选一：

**方式 A：在新机器上从源码构建**（需要能拉基础镜像）

```bash
scp -r CSharpExport/ user@NEWHOST:/opt/logai
ssh user@NEWHOST
cd /opt/logai
docker build -t logaimonitor-cs:latest .
```

**方式 B：导出/导入**（新机器不能联网时）

```bash
# 在已有镜像的机器上
docker save logaimonitor-cs:latest | gzip > logai-cs.tar.gz
scp logai-cs.tar.gz user@NEWHOST:/tmp/

# 在新机器上
gunzip -c /tmp/logai-cs.tar.gz | docker load
docker images | grep logaimonitor-cs      # 应能看到 logaimonitor-cs:latest
```

---

## 3. 一键启动（推荐）

```bash
cd /opt/logai
cp .env.example .env
openssl rand -hex 32                      # 生成一个密钥
vi .env                                   # 把 SECRET_KEY 填进去
docker compose up -d
```

等约 30 秒，然后：

```bash
docker compose ps                         # 两个服务都应为 running / healthy
```

浏览器打开 `http://<新机器IP>:5059`

> **首次启动会自动创建默认管理员**：用户名 `admin`，密码 `admin`。
> **请登录后立即修改密码**（右上角用户菜单）。仅当系统里不存在任何 `role=admin` 的账号时才会创建，重复启动不会重复创建。

### 关于 Redis 参数

compose 里 Redis 的启动参数是刻意配置的，**不建议改**：

```
--maxmemory 9216mb --maxmemory-policy volatile-lru --maxmemory-samples 5
--maxmemory-clients 5% --lazyfree-lazy-eviction yes --save 86400 1
```

`--save 86400 1` 把 RDB 快照从默认的每小时降到每天一次。原因：**开着 AOF 时，重启只从 AOF 恢复**（实测启动日志为 `DB loaded from append only file`，全程不读 `dump.rdb`），所以 RDB 的角色只是"万一 AOF 损坏时的应急回退点"；而 6GB 数据上每次快照要 fork 并写盘约 31 秒，期间打满一个核。降到每天一次既保留兜底，又去掉每小时的 CPU 突发。

> 注意：这个参数写在容器启动命令里。如果 Redis 容器是用 `docker run` 手工起的（而不是 compose），改完必须**重建容器**才生效；`redis-cli CONFIG SET save "86400 1"` 只作用于运行时，容器一重启就回到默认值。

`volatile-lru` 只淘汰**带 TTL 的键**（日志 / 告警 / AI 历史），而 `settings`、`users`、索引注册集合**没有 TTL，永不会被淘汰**。这样即使数据量超预期，也是优雅地丢弃最旧日志，而不是把配置和账号一起清掉。

容量参考：实测约 2KB/条、5,000 条/小时，720 小时约 **7.2GB**。按这个给 `REDIS_MAXMEMORY` 留出余量。

---

## 4. 手工启动（不用 compose 时）

```bash
docker network create logai-net

docker run -d --name logaimonitor-redis --network logai-net --restart unless-stopped \
  -v logai-redis-data:/data redis:7-alpine \
  redis-server --appendonly yes --maxmemory 9216mb --maxmemory-policy volatile-lru \
    --maxmemory-samples 5 --maxmemory-clients 5% --lazyfree-lazy-eviction yes

docker run -d --name logaimonitor --network logai-net --restart unless-stopped \
  -p 5059:5059 -p 514:514/udp -p 515:515/tcp \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -e REDIS_HOST=logaimonitor-redis -e REDIS_PORT=6379 -e REDIS_DB=0 \
  -e ALLOW_DB0_WRITES=1 \
  -e SECRET_KEY=<第 3 步生成的密钥> \
  -e TZ=Asia/Shanghai \
  -e OLLAMA_MODEL=<模型名> -e AI_API_KEY=<密钥> \
  logaimonitor-cs:latest
```

### 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `REDIS_HOST` / `REDIS_PORT` | `127.0.0.1` / `6379` | Redis 地址；**Redis 在容器里时必须用容器名** |
| `REDIS_DB` | `0` | 库号 |
| `ALLOW_DB0_WRITES` | — | **必须设为 `1`**，否则后台写入组件拒绝启动（详见"常见问题"） |
| `SECRET_KEY` | — | 会话签名密钥，**固定不变** |
| `SYSLOG_UDP_PORT` / `SYSLOG_TCP_PORT` | `514` / `515` | 接收端口 |
| `OLLAMA_MODEL` / `AI_API_KEY` | — | AI 后端与密钥（之后也可在设置页改） |
| `DOCKER_COLLECTION` | `on` | 设 `off` 关闭容器日志采集 |
| `TZ` | `UTC` | 建议 `Asia/Shanghai` |

---

## 5. 配置日志源

在每台需要上报的主机上：

```bash
echo '*.* @<新机器IP>:514' > /etc/rsyslog.d/99-logaimonitor.conf
systemctl restart rsyslog
```

TCP 方式（更可靠，适合跨网段）：

```bash
echo '*.* @@<新机器IP>:515' > /etc/rsyslog.d/99-logaimonitor.conf
systemctl restart rsyslog
```

---

## 6. 验证清单

```bash
# 1) 服务存活
curl -s -o /dev/null -w '%{http_code}\n' http://<新机器IP>:5059/api/health      # 期望 200

# 2) 登录页可打开
curl -s -o /dev/null -w '%{http_code}\n' http://<新机器IP>:5059/login           # 期望 200

# 3) 后台组件在跑：每分钟一行心跳，含健康判定与接收计数
docker logs logaimonitor 2>&1 | grep '\[Health\]' | tail -3
#   期望形如：[Health] ok backlog=53 total=2620324 ai=True age=41 warn=2000 | [Receiver] udp=44 ... dropped=0

# 4) 采集链路：登录后在 AI 分析页点 "Analyze Recent Logs"，或直接看日志数是否增长
docker exec logaimonitor-redis redis-cli -n 0 ZCARD logs:timeline
```

界面上确认：仪表盘有数据、Connected Clients 里能看到刚配置的日志源、Logs 页能按主机/来源/级别/关键字筛选。

---

## 7. 常见问题

| 现象 | 原因 | 处理 |
|---|---|---|
| 页面能打开但**没有任何新日志** | 忘记 `ALLOW_DB0_WRITES=1`，后台写入组件被护栏挡住 | 日志里搜 `background writers DISABLED`；加上该变量后重启容器 |
| `/api/health` 返回 **503**，登录也 503 | 应用连不上 Redis | 确认 Redis 与应用在**同一个 Docker 网络**，且 `REDIS_HOST` 用的是**容器名** |
| 登录时提示用户名或密码错误 | 库里没有账号 | 首次启动会自动建 `admin/admin`；若库里已有非 admin 账号，则不会创建，需用已有账号登录 |
| 会话频繁失效、每次重启都要重登 | `SECRET_KEY` 变了或每次随机 | 在 `.env` 里固定一个值 |
| 容器日志采集不到 | 未挂载 docker socket，或目标容器在排除列表里 | 确认 `-v /var/run/docker.sock:/var/run/docker.sock:ro`；排除列表在设置页（默认含 `logaimonitor`、`logaimonitor-redis`） |
| 磁盘持续增长 | 保留期过长 | 设置页调整"日志保留期"（小时），清理任务每小时执行一次 |
| Redis 内存告警 | `REDIS_MAXMEMORY` 小于数据量 | 调大上限或缩短保留期；`volatile-lru` 会先淘汰最旧日志 |

---

## 8. 升级与回退

**升级**（换新镜像）：

```bash
docker compose pull            # 或重新 docker build / docker load
docker compose up -d           # 重建容器，数据在 Redis 卷里不受影响
```

**回退到旧镜像**：

```bash
docker rm -f logaimonitor
docker run -d --name logaimonitor ... <旧镜像标签>     # 参数与第 4 步一致
```

**数据备份**（Redis 卷）：

```bash
docker run --rm -v logai-redis-data:/data -v $(pwd):/backup alpine \
  tar czf /backup/redis-backup-$(date +%F).tar.gz -C /data .
```

---

## 9. 目录与端口一览

| 位置 | 内容 |
|---|---|
| 容器内 `/app` | 应用与模板（代码烘在镜像里，不挂载宿主目录） |
| 卷 `redis-data` → 容器 `/data` | **全部日志数据** |
| 宿主机 `/var/run/docker.sock` | 只读挂载，用于采集容器日志 |
| 5059/tcp · 514/udp · 515/tcp | Web · syslog UDP · syslog TCP |
