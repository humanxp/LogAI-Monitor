# LogAI Monitor

syslog 采集 + AI 批量分析 + 告警推送 + Web 界面的日志监控系统。**.NET 8 单一实现**，自持一套 Redis 数据布局，不依赖任何其它语言版本。

---

## 功能

| 模块 | 说明 |
|---|---|
| **syslog 采集** | UDP + TCP；RFC3164 / RFC5424 解析（PRI、facility/severity、程序名、消息），发送方省略主机名时**不把程序名误当主机名**；编码三级回退（UTF-8 → GB18030 → Latin-1，替换非法序列） |
| **Docker 容器日志** | 通过 Docker Engine API（只读 unix socket）读取运行中容器日志；**排除列表**避免采集自身；8 字节帧解复用；按"最后一行"标记**跨轮询去重** |
| **存储** | 稳定的键布局：`logs:timeline`、`logs:unanalyzed`、三个注册表集合 `logs:index:{sources,hosts,severities}`、三个维度 ZSET `logs:{source,host,severity}:<值>`、日志哈希带保留期 TTL；**冷热分层**——超过 `archive_after_hours`（默认 168h）的日志/分析/告警哈希归档到 SQLite，Redis 只留 ZSET 索引，读取时按需回填 |
| **AI 分析** | 三条路径：**批次**（定时，`type=auto`/`batch`）、**单条**（`type=single`）、**对话**；OpenAI 兼容端点（vLLM / SGLang / Ollama `/v1`）与原生 Ollama；**JSON 提取 + 截断修复 + 数组对修复 + 纠正性重试**；解析失败视为真失败（不伪造成功），**死信退役**避免毒批次永久重试。整体状态分 **critical / warning / healthy / other** 四档，**纯模型判定**（与 python-legacy 一致）——模型判什么就是什么，程序只做同义词归一；默认模型 **Qwen3.5-9B**（MoE，快且准），日志按级别排序、带 `[SEVERITY]` 前缀原样交给模型；「发现的问题/处理建议」由模型自己带 `[主机名/IP]` 前缀，返回结果原样保留（不去重、不封顶 critical_count）。提示词可选：Llama3.2-3B 默认简单版，Qwen3.5-9B / Qwen3.6-35B-A3B 用 few-shot 版 |
| **过滤器与告警** | 四条件 AND（级别列表 / 来源子串 / 消息子串 / 消息正则），正则编译复用、非法正则不匹配不抛异常；告警落库 10 字段；**最低推送级别可配**（critical/error/warning/notice/info/debug，低于它的仍入库但不推），另有单规则"任意级别"旁路；每 (主机 × 规则) **冷却**用 `SET NX EX` 原子实现 |
| **Telegram 推送** | 总开关（设置页不勾则完全不发）+ 两类通知独立控制：**告警**按最低推送级别，**AI 汇总**按 4 档状态勾选并可设冷却；固定模板（emoji 映射、级别大写、`hostname or source`、message 500 / analysis 300 截断、HTML 语义转义）；发送失败不影响采集 |
| **REST API** | 读 20 个 + 写 21 个端点；响应契约固定（键序、非 ASCII 转义、换行），界面与外部脚本依赖它 |
| **实时推送** | Engine.IO v4 长轮询（握手 / 命名空间连接 / 鉴权 / `\x1e` 多包 / 包序）；事件 `connected`、`new_log`、`new_alert`、`analysis_complete`、`stats`（2 秒节流） |
| **调度** | 分析（设置间隔）、清理（保留期 + **死索引清理**）、健康巡检（含看门狗推送与恢复通知）、Docker 轮询；任务抛异常**可见**且不影响其它任务 |
| **认证与权限** | 口令校验**与生成**（scrypt / pbkdf2 / legacy sha256，参数从哈希中读取）；HMAC 签名会话 cookie；三级权限；`X-Ingest-Token` 摄取令牌 |
| **界面与主题** | 11 个主页面 + 登录页，视觉改进集中在叠加层 `wwwroot/css/refined.css`。两套主题：**默认（白天）** 与 **晚上（深色）**，通过语义令牌（`--lm-canvas/-surface/-line/-ink*`＋状态色）实现，按钮切换与整页加载走同一条路径；弹窗、抽屉、快捷键面板等"打开才出现"的界面也在覆盖范围内。**设置页按类目分页**（顶部切换按键），每个类目底部有独立的「保存本类目」，只提交本类目字段 |

---

## 技术栈

- **.NET 8 / ASP.NET Core**（minimal API，无第三方 Web 框架）
- **StackExchange.Redis 2.8** — 管道化写入、批量读取
- **Microsoft.Data.Sqlite 8** — 冷热分层的冷存储（日志/分析/告警哈希归档，Redis 只留 ZSET 索引）
- **System.Text.Json** — 自定义序列化选项以固定响应契约（键排序、非 ASCII 转义）
- **Docker Engine API** — 经 unix socket 只读访问，无 SDK 依赖
- **自实现**：Jinja2 模板子集引擎、Engine.IO v4 长轮询、scrypt 口令哈希生成
- 部署：多阶段 `Dockerfile`（`dotnet publish` → `aspnet:8.0`），静态资源 gzip 压缩 + 5 分钟缓存头

---

## 部署到新机器

完整步骤（镜像搬运、compose 一键启动、日志源配置、常见问题、升级回退）见 **[DEPLOY.md](DEPLOY.md)**。

## 快速开始

```bash
docker build -t logaimonitor-cs .
docker run -d --name logaimonitor \
  -p 5059:5059 -p 514:514/udp -p 515:515/tcp \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -e REDIS_HOST=<redis 主机> -e REDIS_DB=0 \
  -e SECRET_KEY=<固定不变，否则每次重启会话失效> \
  -e ALLOW_DB0_WRITES=1 \
  -e OLLAMA_MODEL=<模型> -e AI_API_KEY=<密钥> \
  logaimonitor-cs
```

### 环境变量

| 变量 | 说明 |
|---|---|
| `REDIS_HOST` / `REDIS_PORT` / `REDIS_DB` | Redis 连接；**`REDIS_DB=0` 默认禁止后台写入**（见下） |
| `ALLOW_DB0_WRITES` | 设为 `1` 才允许在 DB 0 上运行采集/分析/清理等写入任务 |
| `SECRET_KEY` | 会话签名密钥，**必须固定不变**，否则每次重启都要重新登录 |
| `SYSLOG_UDP_PORT` / `SYSLOG_TCP_PORT` | 默认 514 / 515 |
| `AI_BASE_URL` | AI 端点（OpenAI 兼容），如 `http://192.168.50.23:8000/v1`。**设置页的 AI Endpoint 留空即用这个值**；若在设置页手动填了别的地址（尤其 `http://localhost:11434`）就会盖掉它——容器里没有本地 Ollama，会让全部分析报 `Connection refused`（线上踩过） |
| `OLLAMA_MODEL` / `AI_API_KEY` | 模型名与（可选）API 密钥；模型名同样以设置页的 `ollama_model` 优先，为空才回退到这里 |
| `DOCKER_COLLECTION` | 设为 `off` 可关闭容器日志采集 |
| `LOG_ARCHIVE_PATH` | 冷热分层的 SQLite 归档文件路径，默认 `/data/logai-archive.db`（应指向持久卷，否则容器重建归档就丢了） |
| `TELEGRAM_BOT_TOKEN` / `TELEGRAM_CHAT_ID` | Telegram 凭据的**备选来源**：设置页里的值优先（那是用户能改的地方、改完立刻生效），为空时回退到这两个环境变量。只配环境变量、不配设置页也能正常推送 |
| `FILTER_TRACE` | 设为 `1` 时，每条日志都打印 `[Filters] loaded=N matched=M …`。用于区分"没加载到规则""加载了但没匹配""匹配了"，**默认关闭**——生产约 100 条日志/秒，逐条打点会变成噪音而不是可观测性 |

### 巡检看门狗（health watchdog）

定时自检并把异常推给 Telegram，**按条件各自冷却**（不是整体冷却——AI 宕机不会因为刚发过积压告警而被吞掉），条件消失时补一条"已恢复"，另有可选的巡检日报：

| 条件 | 触发 | 恢复通知 |
|---|---|---|
| `backlog` | 未分析积压 > `health_backlog_warn`（允许 0） | 积压回落 ≤ 阈值 |
| `ai` | AI 后端不可达 | 后端恢复 |
| `sched` | 自动分析超过 `max(分析间隔 × 2.5, 300s)` 未运行 | 恢复运行 |

| 设置键 | 默认 | 说明 |
|---|---|---|
| `health_watch_minutes` | 5 | 巡检周期。**每次巡检重读设置，改完无需重启** |
| `health_backlog_warn` | 2000 | 积压阈值，`0` 表示"任何积压都算超标" |
| `health_alert_cooldown_min` | 30 | 每个条件的推送冷却 |
| `health_daily_summary` | true | 每 24 小时一条概览（时间戳存 Redis，重启不会重复发） |

每个推送结果都会留痕（`巡检告警(backlog) 已发送` / `FAILED` / `条件 'ai' 在 30 分钟冷却内，跳过`）——
没有这些行，"发送失败"和"看门狗根本没跑"在日志里长得一样。

### 安全护栏：DB 0 默认只读

> 生产库（DB 0）上，后台写入组件（采集、分析、清理、健康、Docker 轮询）**默认拒绝启动**，并打印 `background writers DISABLED`。

这样"连生产库做只读对照"就不会意外写入数据；正式切换时必须**显式**加 `ALLOW_DB0_WRITES=1`。

这个护栏同时也是**只读比对实例**的正确用法：`REDIS_DB=0` 且**不设** `ALLOW_DB0_WRITES`，
就能对生产数据跑接口比对/参数排查。注意别用隔离库（如 DB 9）去读生产数据——
那样读到的自然是空集合，容易被误判成"接口全挂"。

### 冷热分层（哈希落盘 SQLite）

Redis 里数据占内存的大头是**哈希本体**（日志 ~1.1 KB/条、分析结果 ~5 KB/条），而时间线/维度 ZSET 每条只有 ~70 字节。
冷热分层把"过了热窗口的老哈希"归档到 SQLite，Redis 只保留 ZSET 索引——过滤、翻页、
聚合照常工作，只有真正读取某条老记录时才从 SQLite 回填。三类数据都归档：**日志哈希**、
**分析历史哈希**（含 analysis JSON）、**告警哈希**：

| 设置键 | 默认 | 说明 |
|---|---|---|
| `archive_after_hours` | 168（7 天） | 超过该时长的日志/分析/告警哈希归档到 SQLite；设为 `0` 关闭归档 |
| `log_retention_hours` | 720（30 天） | 总保留期：**日志与 AI 分析历史共用**，到期后从 Redis ZSET 与 SQLite 一并删除 |
| `alert_retention_days` | 30 | 告警保留天数（写入时读取，改完对之后新写入的告警生效）|

- 归档任务**幂等**：分数水位驱动（`logs:archive:watermark` / `ai_history:archive:watermark` / `alerts:archive:watermark` 各自独立），跳过空哈希，重复跑无害
- 读路径自动回填：`/api/logs`、`/api/logs/{id}`、`/api/ai-history`、`/api/alerts` 对 Redis 里缺失的哈希走 SQLite 主键查询（几 ms）
- **分析状态另存一份小哈希** `ai_history:status`（id → 四档状态），它**不随归档搬走**，于是 `/api/ai-history/stats` 不必为已归档记录回读 SQLite 里 5 KB 的分析 JSON（冷调用 444 ms → 48 ms）
- **清理同时覆盖两个库**：设置页的 Clear All Logs / 按来源删除 / 清空 AI 历史会一并清 Redis 与 SQLite（此前只清 Redis，冷库里会留下"界面看不见却占磁盘"的残留）。`--archive-counts` 可打印 SQLite 两表条数用于核对
- 归档后 Redis 内存显著下降（日志实测 300 万条 7.2 GB → 4.3 GB；分析历史 1.9 万条又省 ~70 MB），SQLite 体积约 0.27 KB/条
- 回退：把 `archive_after_hours` 设为 `0` 即关闭归档

---

## 自测

内置 **21 套自测 / 22 个入口**，可用命令行运行（`--<name>-selftest`），覆盖解析、过滤器、告警、会话、Telegram 模板、提示词、提交、调度、健康、清理、客户端跟踪、过滤器加载、AI 历史、批次挑选、Docker 采集、批量删除维护等。多数自测**要求非 0 号库**，对 DB 0 直接拒绝执行。

```bash
docker run --rm --network host -e REDIS_HOST=<host> -e REDIS_DB=9 \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  <image> dotnet LogAI.Web.dll --parse-selftest
```

维护类工具：

```bash
# 批量删除已不存在的容器所留下的日志来源（先干跑，确认后加 --confirm）
dotnet LogAI.Web.dll --purge-sources "docker:cs-" --confirm

# 签发一个临时会话 cookie（验证受保护端点时用，不必知道账号口令）
dotnet LogAI.Web.dll --mint-session <用户名> <角色>

# 打印冷归档（SQLite）两张表的条数——核对"清理是否同时清了两库"
dotnet LogAI.Web.dll --archive-counts

# 重算分析历史的状态分类（分类口径变更或新增闸门后跑一次；幂等）
dotnet LogAI.Web.dll --backfill-ai-status-hash
```

---

## 部署与验证工具

- `scripts/deploy-cs.sh`：同步 → 构建 → 从旧容器读回机密 → **同参数重建容器** → 健康检查。
  必须"构建与重建一起做"：`docker build` 会把 `logaimonitor-cs:latest` 从旧镜像上摘掉，
  而运行中的容器正是用旧镜像创建的，只构建不重建会让容器下次重启起不来。
- 界面改动的视觉验证：对**线上真实页面**截图 + 逐元素对比度审计 + 新旧样式表像素级 A/B。
  工具在仓库上一级的 `.preview/`（含 README），浏览器跑在部署主机的容器里。

---

## 定位

单一实现、单一语言栈：本仓库即全部源码与部署物，不依赖任何其它语言版本，也没有"另一半"需要对照或同步。

Redis 里的数据布局（键名、值编码、索引结构、告警与历史字段）是**本项目的对外契约**：
升级时不会改变它，因此任何按此布局读取数据的脚本都能继续工作。

### 清理（保留期回收）的性能约束

清理任务按**分页 + 管道**实现，不允许出现"一次取回全部到期项"或无界逐条往返：

- 到期日志：每轮按分数取最旧一页（默认 1000 条），读哈希得知索引归属后，
  用一次管道提交删除（哈希 / 时间线 / 待分析队列 / 三个索引）；
- 死索引：按 rank 分页检查存在性，删除后不前进 rank（队列已变短），整页存活才前进；
- 两者都从 rank 0 重新取，因此无需游标，且重复运行天然幂等。

之所以强调这一点：清理的开销**只在跨过保留期那一刻爆发**。早期实现一次性取回
全部到期项（内存无界）并对每条做 6 次顺序往返，在 300 万条规模上表现为"任务像
卡死了"，而不是稳定负载。

实测（隔离库，20,000 条到期日志，每条带哈希 + 时间线 + 队列 + 3 个索引）：

```
POST /api/logs/cleanup → http=200 耗时=0.823s
清理后 时间线=0 队列=0 索引=0
```

基准脚本：仓库上一级的 `.handover-evidence/cleanup-benchmark.sh [条数]`。

## 验证方法

本项目坚持"**断言要带量纲与类型**"和"**端到端才算数**"：

- 解析对照 3000 条真实记录，0 差异（语料随仓库保留在 `src/LogAI.Web/` 的自测入口中）
- 键布局/类型/编码逐项对照生产真实数据（含类型断言，曾借此发现"把 ZSET 当 SET"与"纪元基准错误"两个真实缺陷）
- AI 三条路径均用**真实模型**端到端验证
- 自测套件含类型断言与量纲断言（如"索引分数应为纪元秒"）
- **参数驱动端点**逐个断言"返回集合 ⊆ 参数值"且 `total` 与 Redis 索引基数一致
  （曾借此发现 `/api/ai-history/stats` 完全忽略 `start/end`：空时间窗返回全量）
- **界面改动**用像素级 A/B + 逐元素对比度审计把关，而不是"看着差不多"。
  测对比度必须覆盖"打开才出现"的界面（弹窗等），否则标题这类只在弹窗里的
  元素会漏掉——`html.theme-night .modal-header { color }` 盖不住
  `.modal-header h3 { color: #1f2733 }` 就是这个漏洞造成的。
  比较截图前要冻结动画：CSS `prefers-reduced-motion` **拦不住 SVG SMIL**，
  需要 `document.getAnimations()` 配合 `svg.pauseAnimations()`；
  但也不能无差别冻结，把弹窗的 `opacity` transition 定在 t=0 会让弹窗重新变不可见。
