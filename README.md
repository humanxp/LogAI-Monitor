# LogAI Monitor — .NET 8 重写版

syslog 采集 + AI 批量分析 + 告警推送 + Web 界面的日志监控系统。**本仓库是 .NET 8 重写实现**，与原有 Python 版**共用同一份 Redis 数据布局**，可在同一条数据上并行运行、逐项对照、随时切换。

---

## 功能

| 模块 | 说明 |
|---|---|
| **syslog 采集** | UDP + TCP；RFC3164 / RFC5424 解析（PRI、facility/severity、程序名、消息），发送方省略主机名时**不把程序名误当主机名**；编码三级回退（UTF-8 → GB18030 → Latin-1，替换非法序列） |
| **Docker 容器日志** | 通过 Docker Engine API（只读 unix socket）读取运行中容器日志；**排除列表**避免采集自身；8 字节帧解复用；按"最后一行"标记**跨轮询去重** |
| **存储** | 与 Python 版**完全相同的键布局**：`logs:timeline`、`logs:unanalyzed`、三个注册表集合 `logs:index:{sources,hosts,severities}`、三个维度 ZSET `logs:{source,host,severity}:<值>`、日志哈希带保留期 TTL |
| **AI 分析** | 三条路径：**批次**（定时，`type=auto`/`batch`）、**单条**（`type=single`）、**对话**；OpenAI 兼容端点（vLLM / SGLang / Ollama `/v1`）与原生 Ollama；**JSON 提取 + 截断修复 + 数组对修复 + 纠正性重试**；解析失败视为真失败（不伪造成功），**死信退役**避免毒批次永久重试 |
| **过滤器与告警** | 四条件 AND（级别列表 / 来源子串 / 消息子串 / 消息正则），正则编译复用、非法正则不匹配不抛异常；告警落库 10 字段；**级别门限**与单规则"任意级别"旁路；每 (主机 × 规则) **冷却**用 `SET NX EX` 原子实现 |
| **Telegram 推送** | 消息模板与 Python 版**逐字节一致**（emoji 映射、级别大写、`hostname or source`、message 500 / analysis 300 截断、`html.escape` 语义转义）；发送失败不影响采集 |
| **REST API** | 读 20 个 + 写 21 个端点，响应与 Python 版**逐字节对齐**（键序、非 ASCII 转义、换行） |
| **实时推送** | Engine.IO v4 长轮询（握手 / 命名空间连接 / 鉴权 / `\x1e` 多包 / 包序）；事件 `connected`、`new_log`、`new_alert`、`analysis_complete`、`stats`（2 秒节流） |
| **调度** | 分析（设置间隔）、清理（保留期 + **死索引清理**）、健康巡检（`ok` 判据与阈值同 Python）、Docker 轮询；任务抛异常**可见**且不影响其它任务 |
| **认证与权限** | Werkzeug 兼容口令校验**与生成**（scrypt / pbkdf2 / legacy sha256，参数从哈希中读取）；HMAC 签名会话 cookie；三级权限；`X-Ingest-Token` 摄取令牌 |
| **界面** | 9 个页面，**HTML 与原版逐字节相同**，视觉改进集中在单个 `wwwroot/css/refined.css`（约 10.6 KB）——信息架构零改动 |

---

## 技术栈

- **.NET 8 / ASP.NET Core**（minimal API，无第三方 Web 框架）
- **StackExchange.Redis 2.8** — 管道化写入、批量读取
- **System.Text.Json** — 自定义序列化选项以匹配 Flask 的 `json.dumps`（键排序、非 ASCII 转义）
- **Docker Engine API** — 经 unix socket 只读访问，无 SDK 依赖
- **自实现**：Jinja2 模板子集引擎、Engine.IO v4 长轮询、scrypt 口令哈希生成
- 部署：多阶段 `Dockerfile`（`dotnet publish` → `aspnet:8.0`）

---

## 快速开始

```bash
docker build -t logaimonitor-cs .
docker run -d --name logaimonitor \
  -p 5059:5059 -p 514:514/udp -p 515:515/tcp \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -e REDIS_HOST=<redis 主机> -e REDIS_DB=0 \
  -e SECRET_KEY=<与 Python 版相同，可让会话无缝延续> \
  -e ALLOW_DB0_WRITES=1 \
  -e OLLAMA_MODEL=<模型> -e AI_API_KEY=<密钥> \
  logaimonitor-cs
```

### 环境变量

| 变量 | 说明 |
|---|---|
| `REDIS_HOST` / `REDIS_PORT` / `REDIS_DB` | Redis 连接；**`REDIS_DB=0` 默认禁止后台写入**（见下） |
| `ALLOW_DB0_WRITES` | 设为 `1` 才允许在 DB 0 上运行采集/分析/清理等写入任务 |
| `SECRET_KEY` | 会话签名密钥；沿用 Python 版的值可让已登录用户不掉线 |
| `SYSLOG_UDP_PORT` / `SYSLOG_TCP_PORT` | 默认 514 / 515 |
| `OLLAMA_MODEL` / `AI_API_KEY` | 模型名与（可选）API 密钥 |
| `DOCKER_COLLECTION` | 设为 `off` 可关闭容器日志采集 |

### 安全护栏：DB 0 默认只读

> 生产库（DB 0）上，后台写入组件（采集、分析、清理、健康、Docker 轮询）**默认拒绝启动**，并打印 `background writers DISABLED`。

这样"连生产库做只读对照"就不会意外写入数据；正式切换时必须**显式**加 `ALLOW_DB0_WRITES=1`。

---

## 自测

内置 17 套自测，可用命令行运行（`--<name>-selftest`），覆盖解析、过滤器、告警、会话、Telegram 模板、提示词、提交、调度、健康、清理、客户端跟踪、过滤器加载、AI 历史、批次挑选、Docker 采集等。多数自测**要求非 0 号库**，对 DB 0 直接拒绝执行。

```bash
docker run --rm --network host -e REDIS_HOST=<host> -e REDIS_DB=9 \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  <image> dotnet LogAI.Web.dll --parse-selftest
```

维护类工具：

```bash
# 批量删除已不存在的容器所留下的日志来源（先干跑，确认后加 --confirm）
dotnet LogAI.Web.dll --purge-sources "docker:cs-" --confirm
```

---

## 与 Python 版的关系

两份实现**共用一处 Redis**：键布局、值编码、索引结构、告警与历史字段完全一致，并已双向验证：

- 本实现写入 → Python 用自己的解析逻辑读取并可用 ✓
- Python 写入 → 本实现正确读取（含 `json.dumps` 的空格分隔差异）✓
- 同一时刻读取同一份生产数据：多数读接口**逐字节一致**，其余差异均为实时数据漂移或安全护栏产物 ✓

因此可以并行运行、逐项对照，切换与回退都是一条命令。

## 验证方法

本项目坚持"**断言要带量纲与类型**"和"**端到端才算数**"：

- 解析对照 3000 条真实记录，0 差异
- 键布局/类型/编码逐项对照生产真实数据（含类型断言，曾借此发现"把 ZSET 当 SET"与"纪元基准错误"两个真实缺陷）
- 告警产出交 Python 读取逻辑消费验证
- AI 三条路径均用**真实模型**端到端验证
- 自测套件含类型断言与量纲断言（如"索引分数应为纪元秒"）
