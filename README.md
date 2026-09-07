# LogAI Monitor（LogRadarAI 定制版）

智能日志监控与 AI 分析平台。采集 Linux 主机 syslog（UDP/TCP）与 Docker 容器日志，实时存入 Redis，由 AI 按批自动分析（OpenAI 兼容端点 / Ollama），并通过 Telegram 推送关键告警。

本项目基于开源项目 [LogRadarAI](https://github.com/ftsiadimos/LogRadarAI)（GPL-3.0），由 **MinG** 在本机完成深度定制与生产化改造。

> 原项目由 Fotios Tsiadimos 开发；本仓库在其基础上进行了大量功能增强与修复，详见 [与上游的差异](#与上游的差异)。

---

## 功能特性

- 📡 **Syslog 采集**：UDP / TCP 双通道（默认 514/udp、515/tcp），支持 RFC3164 / RFC5424
- 🐳 **Docker 容器日志采集**：自动发现并采集本机容器（默认已排除 logaimonitor 自身，避免回环噪音）
- 🇨🇳 **中文日志支持**：自动识别 UTF-8 / GBK / GB2312 编码，中文不再乱码
- 🤖 **AI 批量分析**：
  - 每批取**最旧**的未分析日志（防高流量下饿死），按**严重级别优先排序**后采样 **100 条**发给 AI
  - 触发机制：**每 2 分钟 或 积压满 500 条**，谁先到谁触发
  - 返回：状态 / 问题清单 / 解决方法 / 受影响主机
- 📝 **AI 历史与重新分析**：分析（含失败）全部入历史，AI 无响应时可一键重新分析并原地更新
- 🔔 **Telegram 通知**：仅 AI 判定 **critical**（或过滤器告警达设定级别）才推送，摘要附带 AI 给出的解决方法
- 🔍 **过滤器与告警**：自定义规则命中后生成页面告警，可选推送到 Telegram
- 💬 **AI 聊天助手**：基于日志上下文问答
- ⚡ **实时推送**：Socket.IO，页面实时更新
- 🖥 **Web 管理**：仪表盘 / 日志 / 过滤器 / 告警 / AI 分析 / AI 历史 / 设置 / 用户

## 技术栈

| 层 | 技术 |
|---|---|
| 语言/框架 | Python 3 / Flask（Flask-SocketIO · Flask-Login） |
| 存储 | Redis |
| AI | OpenAI 兼容 API（vLLM/SGLang/omlx 等），原生 Ollama 亦可用 |
| 调度 | APScheduler（定时分析 / 清理 / 健康检查） |
| Web 服务 | Gunicorn（gthread，1 worker / 40 线程） |
| 容器日志 | Docker SDK（挂载 `/var/run/docker.sock` 只读） |
| 实时 | Socket.IO（threading 模式，轮询传输） |
| 通知 | Telegram Bot API（python-telegram-bot） |
| 采集 | 原生 socket（UDP/TCP syslog） |
| 前端 | 原生 JavaScript + FontAwesome（无框架） |

## 架构

```
┌──────────────┐   UDP 514 / TCP 515
│ Linux 主机    │───────────┐
│ (rsyslog)     │           ▼
└──────────────┘      ┌────────────────────┐    ┌─────────┐
                      │   LogAI Monitor    │───▶│  Redis  │
┌──────────────┐      │  (Gunicorn:5059)   │    │ (存储)   │
│ Docker 容器   │─────▶│  Syslog 接收器      │    └─────────┘
└──────────────┘      │  Docker 采集器      │
                      └─────────┬──────────┘
                                │ AI 分析（2 分钟或 500 条触发，
                                │ 采样 100 条 → OpenAI兼容/Ollama）
                                ▼
                     ┌────────────────────┐
                     │  AI 分析结果 / 历史   │──▶ Telegram（仅 critical）
                     └────────────────────┘
```

## 与上游的差异

本项目相对原版 LogRadarAI 的全部改动：

**🤖 AI 分析**
- 新增 OpenAI 兼容端点支持（原生 Ollama 仍可用）
- 批量触发机制：每 2 分钟 **或** 积压满 500 条即触发（原仅定时）
- 最旧日志优先分析，修复高日志量下的饥饿问题
- AI 无响应/失败写入历史记录，可一键重新分析并原地更新（不产生重复条目）
- 修复 AI 输出截断导致的 JSON 解析失败（严格输出约束 + 提高 token 上限）
- 每批按严重级别排序采样 **100 条**（原 200 条）
- AI 可用性探测超时 20s → 8s，点击不再长时间假死
- 模型输出结构容错：小模型（如 Qwen3-0.6B）返回对象而非字符串时分析不再崩溃
- 设置里改模型/主机即时生效（每次分析前自动同步，无需重启）
- AI 服务器不可达时快速失败：连接/读取超时分离（8s/180s），不再占用分析锁导致后续批次跳过；自动分析直接发请求、去掉多余可用性探测

**🔔 Telegram**
- 批量摘要仅 AI 判定 critical 时发送
- 过滤器告警按 `alert_on_critical` / `alert_on_error` 开关限级（原为死设置）
- 摘要附带 AI 生成的解决方法
- 调度器提醒仅任务真正出错时发送（错过/跳过不再打扰）

**🛠 性能与稳定性**
- 生产部署：Gunicorn（gthread，40 线程）替代 Flask 开发服务器
- 清库/清理改为批量删除（8.6 万条约 0.4 秒）；清空日志库时同步清空 Connected Clients 记录
- 排除自监控：logaimonitor 容器不再采集自己的输出
- AI 历史接口返回真实总数
- 日志列表接口优化：有界窗口 + Redis pipeline（2.5 万条时单页加载 2.3s → ~40ms）
- 主机/级别/来源筛选走索引集合查询，修复“选择某台主机查不到日志”（不受最新窗口限制）
- 清理任务自动清除过期日志残留的死 ID（时间线与主机索引不再虚增）
- 前端实时渲染批量化（约 150ms 合批）并按页面限制 DOM 数量（Dashboard 30 / Logs 200），实时滚动流畅
- 镜像构建修复：改用官方公共基础镜像（层数 497 → 30），解决反复重建导致的层数爆炸（overlay 上限报错）

**🇨🇳 中文支持**
- syslog 智能解码 UTF-8 → GB18030/GBK → latin-1，中文日志不再乱码

**🖥 界面**
- AI History：auto/batch 记录正确显示为 Batch（原误显示 Single）；单条记录显示 category/is_critical 而非 unknown
- AI History 列表上限 100 → 500 条，统计卡显示 30 天真实总数
- 各页面 Refresh 按钮（Logs / AI History / Users / Docker / Alerts）及日志页 Clear 均带加载状态（转圈）与结果提示
- About 页：真实技术栈 + 开发者署名
- “Connected Clients”从设置页抽为独立页面（Main 菜单下），客户端列表每 5 秒自动刷新
- Connected Clients 记录持久化到 Redis（重启不丢、不自动删除），表格带序号列
- Connected Clients 每行一个删除键：一键删除该主机记录及其发来的全部日志（管理员，红色确认）

## 更新记录（2026-09-04 ~ 2026-09-07）

> 与 About 页"开发者定制说明"同步；完整逐条清单见 About → Developers。

- **分页与网页化配置**：Log Entries / AI History 均 100 条/页 + 翻页；AI 历史保留期与 Log Retention 同步并自动清扫过期记录；General Settings 可直接改 保留期 / 分析间隔 / 每批条数 / AI 采样上限（保存即生效、调度实时重排，无需重建容器）
- **AI 解析与稳定性**：JSON 提取重写（字符串感知 + schema 评分 + 纠正性重试）；prompt 输入消毒（引号/换行/反斜杠）；输出预算与 temperature/frequency_penalty 调优，防小模型重复循环；失败批次自动重试自愈并自动清理残留失败记录
- **AI 端点可运维**：Settings 可手动选择 OpenAI-compatible /v1 或 Ollama 原生协议；端点修改即时生效、自动补全 `/v1`；读超时 AI_READ_TIMEOUT 可配；4xx 错误透出服务端原因；AI 引擎可由 systemd 托管自启
- **健康巡检与告警**：内置看门狗（积压 / AI 不可达 / 调度卡死 → Telegram 告警 + 恢复通知 + 每日日报），参数可在 General Settings 网页调整；`GET /api/health` 供外部轮询
- **实时性修复**：API 全量 `Cache-Control: no-store` + 前端 cache-buster + Socket 断开 15s 兜底轮询 + 45s 心跳刷新，解决 Chromium 激进缓存导致的"页面不更新"
- **存储优化（2026-09-07）**：移除冗余 `raw_message` 字段（零读取、重复存储）；每条日志分析结果改为紧凑 JSON（完整结果在 AI 历史），长期内存占用降 ~30-40%

## 文件结构

```
LogRadarAI/
├── app.py                      # 主程序（路由 / 调度 / 触发逻辑）
├── config.py                   # 配置（env 读取）
├── Dockerfile                  # 可移植镜像构建（ARG BASE_IMAGE）
├── docker-compose.yml          # 生产编排（gunicorn 启动）
├── docker-compose.example.yml  # compose 参考副本
├── .env.example                # 环境变量模板（复制为 .env 使用）
├── .env                        # 本机实际配置（含密钥，勿提交/勿打包）
├── DEPLOY.md                   # 新机器部署指南
├── README.md
├── requirements.txt
├── VERSION
├── LICENSE                     # GPL-3.0
├── services/
│   ├── __init__.py
│   ├── syslog_receiver.py      # Syslog UDP/TCP 接收（含中文编码识别）
│   ├── docker_collector.py     # Docker 容器日志采集
│   ├── ollama_analyzer.py      # AI 分析（OpenAI兼容/Ollama，批处理采样）
│   ├── redis_client.py         # Redis 存储/历史/统计
│   └── telegram_notifier.py    # Telegram 通知
├── templates/                  # 14 个 Jinja2 页面模板（含 clients.html 客户端页）
└── static/                     # CSS / JS / FontAwesome / socket.io
```

## 快速开始（本机）

```bash
docker compose build
docker compose up -d
docker ps          # logaimonitor / logaimonitor-redis 均应为 healthy
```

访问 `http://<IP>:5059`，默认账号 **admin / admin**（登录后请立即修改）。

## 部署到新机器

完整步骤见 **[DEPLOY.md](DEPLOY.md)**。要点：

1. 安装 Docker + Compose v2；
2. 拷贝本目录；`cp .env.example .env` 并填写密钥/AI 端点/Telegram；
3. 基础镜像二选一：
   - 离线：`docker load < logradarai-image.tar.gz`（现网机器 `docker save logradarai:local | gzip > ...` 导出）
   - 联网：`export BASE_IMAGE=ftsiadimos/logradaraiq:latest`
4. `docker compose build && docker compose up -d`；
5. 放行 5059/tcp、514/udp；日志源配置见下。

## 配置说明

主要环境变量（完整见 `.env.example`）：

| 变量 | 说明 | 本机默认 |
|---|---|---|
| `SECRET_KEY` | Flask 密钥（务必改） | — |
| `DEBUG` | 调试模式 | `false` |
| `AI_PROVIDER` | `openai` / `ollama` | `openai` |
| `AI_BASE_URL` | OpenAI 兼容端点（含 /v1） | — |
| `AI_API_KEY` | 端点密钥 | — |
| `OLLAMA_MODEL` | 模型名 | `Llama-3.2-3B-Instruct-4bit` |
| `SYSLOG_PORT` | Syslog UDP 端口（TCP=+1） | `514` |
| `LOG_RETENTION_HOURS` | 日志 Redis TTL（小时） | `12` |
| `ANALYSIS_INTERVAL_MINUTES` | AI 分析间隔 | `2` |
| `MAX_LOGS_PER_ANALYSIS` | 每批取数上限（兼作触发阈值） | `500` |
| `BATCH_SAMPLE_LIMIT` | 每批发给 AI 的条数（严重级优先） | `100` |
| `TELEGRAM_BOT_TOKEN` / `TELEGRAM_CHAT_ID` | Telegram 推送 | — |

## 配置日志源（rsyslog）

在各需要上报的主机上：

```bash
echo '*.* @LogAI主机IP:514' > /etc/rsyslog.d/99-logaimonitor.conf
systemctl restart rsyslog
```

测试：`logger -n LogAI主机IP -P 514 -d "test"`。本机自身也可按同样方式转发（本机日志默认未采集）。

## 常见问题

- **中文日志显示 `?`**：部分设备（如 MikroTik）在发送前就把非 ASCII 字符替换为 `?`，属设备端行为，无法恢复；请确认设备以 UTF-8 发送。
- **AI 分析总是 "Unable to parse"**：多为 AI 输出被截断或返回非 JSON。已通过约束输出规模与提高 token 上限缓解；如仍频繁出现请检查 AI 端点负载或更换模型。
- **想清空日志**：设置页 → Log Database → Clear All Logs（秒级完成）。

## 许可证

GPL-3.0。原作者 Fotios Tsiadimos；本机定制与文档：MinG。
