# 交接文档（归档快照）

> 本文档用于无损接续工作：记录**生产现状**、**已验证的事实**、**未完成项与确切下一步**，
> 以及本项目里行之有效的验证约定。新会话请从「未完成项」直接开始。

---

## 1. 生产拓扑

| 项 | 值 |
|---|---|
| 主机 | `192.168.50.6`（root） |
| 应用容器 | `logaimonitor`（镜像 `logaimonitor-cs:latest`，.NET 8 版，带 HEALTHCHECK） |
| 数据容器 | `logaimonitor-redis`（`redis:7-alpine`，网络 `logradarai_logaimonitor-net`） |
| 回退容器 | `logaimonitor-py-backup2`（已停止，镜像 `logradarai:hardened`＝加固后的 Python 版） |
| 端口 | 5059/tcp（Web）· 514/udp · 515/tcp（syslog） |
| Redis | DB 0；`maxmemory 9G` + `volatile-lru`；卷 `logradarai_redis-data` |
| 源码（构建树） | `/root/logai-cs/`（由本仓库同步过去后 `docker build`） |
| 工作副本 | 本仓库（`CSharpExport/`） |

**默认管理员**：`admin / admin`（仅当库中不存在 role=admin 时自动创建）。

### 环境变量要点
- `REDIS_HOST` **必须用容器名**（同网络内可解析），漏传 `--network` 会导致 `/api/health` 503。
- `ALLOW_DB0_WRITES=1` **必须显式设置**，否则采集/分析/清理等后台写入组件拒绝启动。
- `SECRET_KEY` 固定，否则每次重启会话失效。

---

## 2. 已完成的验证（可信事实）

| 领域 | 结论 |
|---|---|
| 接口 | 读 20 + 写 21 全部实现；与 Python 版并行对照，14/18 逐字节一致，差异均已定性 |
| 采集 | UDP 与 TCP 实测各 +1 条；`dropped=0` |
| 实时推送 | 长轮询握手/连接/鉴权/事件（`new_log`、`new_alert`、`stats`、`analysis_complete`）实测通过 |
| AI 三条路径 | 批次 / 单条 / 对话，均对真实模型端到端验证 |
| 定时分析 | 每约 2 分钟一次，`age` 锯齿 32–128 秒，远低于上限 600 秒 |
| 首次运行引导 | 空库启动自动创建 `admin/admin`，字段与生产一致，幂等 |
| 部署套件 | `DEPLOY.md` + `docker-compose.yml`（已实测一键启动，含健康检查） |
| 性能 | `/api/stats` 冷启动 4.05s → **0.003s**；筛选 0.20–0.36s → **0.014s**（60 秒交集缓存） |
| 时间筛选 | `/api/logs`、`/api/ai-history` 的 `start/end` 逐项验证（含颠倒交换、ISO 格式、空结果） |
| 无效参数 | `limit=abc`、`start=abc`、`limit=-5`、超大/颠倒时间窗 → 全部 200，**无 500** |

---

## 3. 未完成项与确切下一步

> 现状：3.1 已完成并部署；接下来按 3.2 → 3.3 的顺序做，3.4 与代码无关但优先级最高。
### 3.1 补两个静默删除入口的日志 —— ✅ 已完成（2026-10-03）
```csharp
// LogMaintenance.ClearAllAsync       → [Logs] cleared all N log(s)
// LogMaintenance.DeleteBySourceAsync → [Logs] deleted N log(s) from source <source>
```
背景：手动清空告警原本不留痕迹，导致一次"37 条告警去哪了"无法自查（后确认是用户手动删除）。
`/api/alerts/clear` 已补日志，这两个入口同样静默。

**已部署**：镜像 `sha256:51f800680e6f…`（旧镜像 `50008ecd1734`），容器已用同一份
环境变量重建（见 3.6）。日志行与 `AlertMaintenance` 的 `[Alerts]` 风格一致，无条件输出
（被删 0 条时也输出，因为"删了 0 条"同样是审计信息）。

**验证（三层，均为正面判据）**
1. 隔离库自测 `--maintenance-selftest`（DB 9）：
   `[Logs] deleted 2 log(s) from source 10.0.0.1` / `[Logs] cleared all 2 log(s)`，
   与断言期望的条数一致，`ALL PASSED (0 failures)`。
2. 端到端走 `POST /api/logs/delete-source`：注入 1 条合成日志 →
   响应 `{"client_deleted":true,"deleted":5,"status":"ok"}` →
   容器日志出现 `[Logs] deleted 5 log(s) from source 127.0.0.1`；
   删除后 `logs:source:127.0.0.1` 不存在，告警数不变。
3. 不存在的来源：`{"deleted":0,...}` → `[Logs] deleted 0 log(s) from source zzz-nonexistent`。

**验收边界**：`ClearAllAsync` 的那一行没有在 DB 0 上做过真实调用——它会清空 269 万条
生产日志。它由第 1 层（同一镜像、同一条代码路径、命中 DB 9）覆盖。

### 3.2 过滤器告警的 Telegram 推送（卡在"通知链未进入"）
- 现象：过滤器命中并**生成告警** ✓，但**没有任何通知日志** ✗（连"被门限拦截"都没打）。
- 已做：给通知链每一步加了诊断日志（门限/冷却/未配置/已发送/失败，含上下文），**已部署但未触发**。
- 下一步：**回读 `AppHost.cs` 确认那段代码的实际位置**（是否真在 `foreach (var rule in LoadFiltersAsync(...))` 内）。
  本项目已多次发生"整块替换配平但**放错位置**"，编译通过不代表位置正确。
  随后在**循环入口**加一行"匹配到 N 条规则"，即可区分"没进循环"与"进了但没匹配"。
- 相关配置（已改为正确值）：`filter:1790028200664`（网络抖动）`notify_telegram=true`、`notify_any_severity=true`。

### 3.3 参数驱动行为集中排查
对"带参数会改变行为"的端点各测一次，断言**返回集合 ⊆ 参数值**且 `total` 与索引基数一致：
```
/api/alerts?severity=   ?acknowledged=   （库中现有 12+ 条告警，正是好时机）
/api/syslog/clients?…   /api/docker/containers?…   /api/ai-history/stats?…
```
**方法必须正确**（上次因此误判）：
1. 先单独确认会话有效（打印登录返回码，不假设成功）；
2. 把每个响应**存成文件**；
3. 在**文件上**提取（不要把带引号的复杂 grep 塞进 ssh 命令）。

### 3.4 安全事项（优先级最高）
- **撤销 Telegram 令牌**：它曾明文出现在对话中 → BotFather → `/mybots` → API Token → Revoke，
  然后到设置页填入新令牌。
- 同理，两个 GitHub 令牌也已出现在对话中，应一并撤销。

### 3.5 可选清理
- `logradarai:local`（499MB）：已无容器使用（回退容器用的是 `hardened`）→ 可删。
- `style.css` 里 141 处 `.theme-terminal` 规则：**已不可达** → 可删（本次已删掉
  base.html 里那份 212 行的内联终端样式，style.css 里的仍在，见 3.8）。

### 3.8 晚上主题重做（2026-10-03 完成，已部署）
背景：界面上"晚上"几乎不可用——页面底色仍是白的，卡片是深灰，正文是浅灰，
等于"白底浅灰字"；筛选条、表头、分页、输入框也还是白天配色。

**根因（两个，都修了）**
1. `app.js` 的 `applyTheme()` 把 `theme-night` 挂在 `.main-content` 上，而
   `style.css` 把浅色写死在 `body`、`.main-header` 等**该元素之外**的地方。
   于是**带刷新的加载**（base.html 会设 `data-theme`）看着还行，
   **点按钮切换**（只改 `.main-content`）就只换一半。
   现在主题标记只挂在 `<html>` 上，加载与切换走同一条路径。
2. `refined.css` 只覆盖了 `.card/table/input` 等少数选择器，样式表里还有
   27 处写死的浅底和 20 多处写死的深色文字不在覆盖范围内。
   现在收敛成一套语义令牌（`--lm-canvas/-subtle/-hover/-line/-ink*`＋状态色），
   `:root` 是白天、`html.theme-night` 是晚上，两边都保证对比度。

**顺带修掉的问题**
- 设置页主题下拉里是 `terminal`（深绿终端风），而 JS 只认 `default/night`
  → 选"Terminal"实际落到默认。现改为 `默认（白天）/ 晚上（深色）`。
- **登录页完全无视主题设置**（独立页面，不加载 app.js，也没有主题标记）：
  现补上同样的内联标记并加载主题层；同时修掉"卡片永远白底 + 晚上深色文字
  => 1.19:1 看不见"的问题。
- 模板与 `app.js` 里 110+ 处内联 `#666/#333/浅底` 改为令牌；`#e74c3c/#3498db`
  角色徽标、`#512BD4/#DC382D/#2496ED` 品牌色保持字面量（白字在深色上本就够）。
- 删除 base.html 里 212 行**不可达**的 `.theme-terminal` 内联样式与其初始化脚本。

**验证方式（可复现，见 `.preview/README.md`）**
浏览器跑在部署主机上（沙箱缺 Chromium 的系统库），对**线上真实页面**截图 +
逐元素计算对比度（正文阈值 4.5:1）。结果：
- 晚上缺陷元素从 **48 → 1**；
- 剩下的 1 处是 about 页色块内文字 4.26:1（阈值 4.5，接近，可接受）；
- 另有 16 处是"深色底 + 白字"的实心按钮/徽标，**白天同样不达标**
  （如白字在 `#28a745` 上 3.13:1），属既有问题，不算主题回归。
- 白天主题逐页核对无变化（body 令牌值就是原来的 `#f4f5f7`）。

**仍可做（收益低，先不做）**：`style.css` 里 141 处 `.theme-terminal` 规则、
实心按钮的白字对比度（`--lm-btn-primary-bg` 已把主按钮修到 4.75:1，其余变体未动）。

### 3.6 部署方式的一个隐患（本次踩到，已缓解）
- **`logaimonitor` 容器不是 compose 建的**：它挂在 `logradarai_logaimonitor-net` 上，
  宿主机上已经没有对应的 compose 工程，所以 `docker compose up -d` 管不了它；
  它的全部参数只存在于容器自身。`REDIS_HOST=redis`（不是 `logaimonitor-redis`）——
  该网络上 `redis` 是个能解析的别名。
- **`docker build -t logaimonitor-cs:latest` 会把旧镜像的 tag 摘掉**。本次构建后
  运行中容器的镜像 `50008ecd1734` 在宿主机上已经不存在（只剩容器引用），
  此时容器一旦重启就起不来。**因此构建与重建容器必须一次做完**。
- 为此新增 `CSharpExport/scripts/deploy-cs.sh`：同步 → 构建 → 用 `docker inspect`
  从旧容器读回 4 个机密（`SECRET_KEY`/`AI_API_KEY`/`TELEGRAM_BOT_TOKEN`/`TELEGRAM_CHAT_ID`，
  不写进脚本文件）→ 以完全相同的参数重建容器 → 健康检查。
  抽取/拼装逻辑已在宿主机上空跑验证过（`sh -n` 通过、生成的片段语法正确）。
- 重建后已核对：容器内 32 个应用环境变量与旧容器**逐一相同**（差异仅为基础镜像自带的
  `PATH`/`LANG`/`GPG_KEY`/`PYTHON_VERSION`/`PYTHON_SHA256`——见 3.7）；数据不受影响
  （Redis 卷未动，`logs:timeline` 仍在增长，告警 13 条保留）。

### 3.7 待确认：旧容器为何带着 Python 基础镜像的环境变量
- `docker inspect` 显示**正在跑的 .NET 容器**里带着 `PYTHON_VERSION=3.11.16`、
  `PYTHON_SHA256=…`、`GPG_KEY=…`、`LANG=C.UTF-8` 以及 Python 镜像的 `PATH`。
  当前 `Dockerfile` 的 `FROM mcr.microsoft.com/dotnet/aspnet:8.0` 不可能带这些变量，
  所以旧容器应当是被人为带上（或从 Python 镜像继承）后启动的，来源尚不明确。
- 重建后的容器只保留 .NET 基础镜像自带的变量，**应用自身 32 个变量一项没少**，
  启动日志确认是 .NET 版（`Now listening on: http://0.0.0.0:5059`、`[Health]` 心跳、
  `[Receiver] udp=… dropped=0`）。这个差异对功能无影响，仅作记录。
- 顺带修正一处过时说明：**`admin/admin` 已经登不上了**（返回"无效用户名或密码"），
  说明管理员密码早已改过。本次验证改用应用自带的 `dotnet LogAI.Web.dll --mint-session`
  （需传入生产 `SECRET_KEY`）签发的临时管理员会话，没有改库里的任何账号。

---

## 4. 验证约定（本项目行之有效的做法，请沿用）

1. **用正面判据**：`grep -q "Build succeeded"`，而不是"没有出现某类错误"。
  （曾因只查 `error CS` 而在**源码目录被删空**时误判构建成功。）
2. **改动产物要有变化**：镜像 ID 必须变化；文件必须回读确认。
3. **整块替换后查结构**：花括号配平 + `<div>` 配平 + 章节清单。
  （曾整块替换时**静默删掉整个 Technology Stack 章节**。）
4. **编辑用文件承载，不用内联引号**：把内容写进文件再插入，避免引号/分隔符问题。
  （已多次踩坑：`perl s|...|...|` 而内容含 `|`；`sed` 替换串里双引号套双引号；heredoc 吞掉后续命令。）
5. **传数据不要加 `< /dev/null`**：它会覆盖前面的管道（症状是 gzip 流不完整，极易误判）。
6. **远端命令只用双引号或不含引号的模式**：内层单引号会提前结束外层字符串。
7. **测试脚本也要被验证**：本次两次"功能异常"实为**测试脚本错误**（sid 解析多截一个字符；
  cookie 文件被后台输出覆盖）。产品无恙，但浪费了大量排查。
8. **破坏性操作只在隔离库（DB 9）验证**；DB 0 默认只读护栏。

---

## 5. 常用命令

```bash
# 构建并部署 —— 用脚本，不要手敲下面那两行（会漏掉"重建容器"这一步，见 3.6）
#   KEY=~/.ssh/logai_deploy sh scripts/deploy-cs.sh
# 脚本做四件事：同步 → 构建 → 从旧容器读回 4 个机密 → 同参数重建容器 → 健康检查。
# 前置：ssh-keygen -t ed25519 -N "" -f ~/.ssh/logai_deploy && ssh-copy-id -i ~/.ssh/logai_deploy.pub root@192.168.50.6
# （脚本内部要二次 ssh 读旧容器环境变量，交互式密码在那里答不了。）

# 健康与心跳
curl -s -o /dev/null -w '%{http_code}\n' http://192.168.50.6:5059/api/health
ssh root@192.168.50.6 "docker logs logaimonitor 2>&1 | grep '\[Health\]' | tail -3"

# 回退到 Python 版（保留数据）
ssh root@192.168.50.6 'docker rm -f logaimonitor && docker rename logaimonitor-py-backup2 logaimonitor && docker start logaimonitor'

# 主题/界面改动的视觉验证（截线上页面 + 逐元素对比度审计）
#   见 ../.preview/README.md
```
