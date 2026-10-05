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
| 历史回退物 | 已于 2026-10-03 解绑：容器 `logaimonitor-py-backup2` 与镜像 `logradarai:hardened` 已删除，镜像归档在宿主机 `/root/python-fallback-archive/`（见 3.9） |
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
| 接口 | 读 20 + 写 21 全部实现；**移植期间**曾与旧实现并行对照，14/18 逐字节一致、差异均已定性（该对照已结束，见 3.9） |
| 采集 | UDP 与 TCP 实测各 +1 条；`dropped=0` |
| 实时推送 | 长轮询握手/连接/鉴权/事件（`new_log`、`new_alert`、`stats`、`analysis_complete`）实测通过 |
| AI 三条路径 | 批次 / 单条 / 对话，均对真实模型端到端验证 |
| 定时分析 | 每约 2 分钟一次，`age` 锯齿 32–128 秒，远低于上限 600 秒 |
| 首次运行引导 | 空库启动自动创建 `admin/admin`，字段与生产一致，幂等 |
| 部署套件 | `DEPLOY.md` + `docker-compose.yml`（已实测一键启动，含健康检查） |
| 性能 | `/api/stats` 冷启动 4.05s → **0.003s**；筛选 0.20–0.36s → **0.014s**（60 秒交集缓存） |
| 时间筛选 | `/api/logs`、`/api/ai-history` 的 `start/end` 逐项验证（含颠倒交换、ISO 格式、空结果） |
| 无效参数 | `limit=abc`、`start=abc`、`limit=-5`、超大/颠倒时间窗 → 全部 200，**无 500** |
| 分批管道（3.10） | 18/18 自测通过；生产数据只读 A/B 全部读端点逐字节一致；500 条突发 274–373ms → **182ms** |
| 界面对比度（3.11） | 无头 Chromium 审计：页面级 28→1、弹窗级 35→0（剩余 1 处为登录按钮渐变误报） |

---

## 3. 工作项与确切下一步

> 现状（2026-10-05）：
> **3.10（读端点/采集/维护路径分批管道化）与 3.11（界面对比度修复 + 打磨）已
> 完成并部署**，都按本项目一贯的三层验证（隔离库自测 → 生产数据只读 A/B 逐字节
> 对比 → 计时/审计）走完。
> **仍只剩 3.4 需要人来做**：撤销曾出现在对话里的 Telegram 令牌与两个 GitHub
> 令牌——这一步与代码无关，但优先级最高。

> 现状（2026-10-03，已过时，仅留档）：
> 3.1 / 3.2 / 3.3 / 3.5 / 3.6 已完成并部署；3.7 是部署方式的隐患（已加脚本缓解），
> 3.8 是一个待确认的历史遗留问题。

### 3.1 补两个静默删除入口的日志 —— ✅ 已完成（2026-10-03）
```csharp
// LogMaintenance.ClearAllAsync       → [Logs] cleared all N log(s)
// LogMaintenance.DeleteBySourceAsync → [Logs] deleted N log(s) from source <source>
```
背景：手动清空告警原本不留痕迹，导致一次"37 条告警去哪了"无法自查（后确认是用户手动删除）。
`/api/alerts/clear` 已补日志，这两个入口同样静默。

**已部署**：镜像 `sha256:51f800680e6f…`（旧镜像 `50008ecd1734`），容器已用同一份
环境变量重建（见 3.7）。日志行与 `AlertMaintenance` 的 `[Alerts]` 风格一致，无条件输出
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

### 3.2 过滤器告警的 Telegram 推送 —— ✅ 已完成（2026-10-03）
**结论：通知链本身是通的，"链路没进去"是误判。** 真正的问题是
**诊断日志当时根本没被部署**——上一轮只改了源码，镜像没重建过，
所以"什么日志都没有"被解读成"没进通知链"。

本次实测（合成来源，`登录爆破` 规则）：
```
[Telegram] alert sent (filter=filter:1790028200663 notify=True any_severity=True severity=info host=172.18.0.1)
```
告警数 +1、`notif_cooldown:…` 键出现、`[Filters] loaded=1 matched=1`，链路端到端成立。

**代码位置已核对**：那段诊断确实在 `foreach (var rule in rules)` 内
（`AppHost.cs` 的 `OnStored`），不是"配平但放错位置"。

**本次新增/修复**
1. 循环入口的区分用日志（HANDOVER 要求的"匹配到 N 条规则"）：
   由 `FILTER_TRACE=1` 打开，输出 `[Filters] loaded=N matched=M severity=… source=…`。
   **默认关闭**——按每条日志打一行在生产是 ~100 行/秒的噪音，
   "加了噪声导致没法看"和"没有日志"一样糟。
   实测：不匹配→`matched=0`；匹配→`matched=1`；未设变量→一行都不打。
2. **冷却顺序 bug（真 bug）**：原来是"先占冷却，再检查 Telegram 是否配置"，
   于是 Telegram 未配置时也会把冷却窗口消耗掉；等凭据补齐后，
   该 `主机+规则` 的告警在冷却期内被判 `suppressed` 而**静默丢弃**。
   现改为"确认能发 → 再占冷却 → 发送"。
   实测（DB 9、无 bot token）：告警写入成功、`[Telegram] … not configured` 有日志、
   **`notif_cooldown:*` 为空**——顺序修复生效。
3. 生产环境未设 `FILTER_TRACE`，确认无 trace 噪音；重排后 Telegram 仍 `alert sent`。

### 3.3 参数驱动行为集中排查 —— ✅ 已完成（2026-10-03）
方法按要求执行：先用 `--mint-session` 单独确认会话有效（打印返回码，不假设），
每个响应存成文件，判定用脚本在**文件**上做。最终 18 项断言全 PASS。

**发现并修掉的真 bug：`/api/ai-history/stats` 完全忽略 `start/end`。**
- 现象：`?start=2001-01-01&end=2001-01-02`（空窗）返回**全量** 15427 条。
- 根因：该端点既不读参数，也不做 `ZRANGEBYSCORE`（本应带时间窗）。
- 修复：与 `/api/ai-history` 共用同一套 `ParseWindow`（epoch 秒或 ISO-8601、
  两端都要能解析、颠倒则交换），并按分数区间取 id；顺带把
  "每个 id 一次 HGET"改成 500 条一批（原来每次页面加载 1.5 万次往返）。
- 实测（新旧同库同时刻对比）：

  | 用例 | 旧（线上） | 新 | 期望 |
  |---|---|---|---|
  | 无参 | 15425 | 15425 | 基数 ✓ |
  | start=min&end=mid | **15425**（错） | **7592** | ZCOUNT 7592 ✓ |
  | start=mid&end=max | **15425**（错） | **7832** | ZCOUNT 7832 ✓ |
  | 空窗 | **15425**（错） | **0** | 0 ✓ |
  | 颠倒两端 | 15425 | 15425 | 交换后全量 ✓ |

**其余端点结论（无需改代码）**
- `/api/alerts`：`acknowledged=true|false` 正确（`true` 当前 0 条因为全部未确认；
  两者之和 == 全量，不重不漏）；非法值退化为无过滤；
  `limit/offset` 正确且不重叠；每条 10 个字段。
- `/api/alerts?severity=`：**该参数从未存在**，UI 只发 `?limit=100`。
  属交接文档里的臆测，保持"无害忽略"，不新增参数。
- `/api/syslog/clients`：**不是 GET 端点，只是文档写错了**。C# 侧只有
  `DELETE /api/syslog/clients/{ip}`，客户端列表在 `/api/syslog/diagnostics`
  （无参数，11 个顶层字段 + 13 个每客户端字段）；只有 DELETE。
- `/api/docker/containers`：无参数端点，多余参数被忽略；每容器 6 个字段。
- `/api/logs`：不存在的 severity / 空时间窗都返回 `{"count":0,"logs":[],"total":0}`，无 500。

**方法学教训（本轮又踩一次）**：验证脚本把 `1.7884096750999029e+9` 直接拼进 URL，
HTTP 把 `+` 解成空格 ⇒ 时间戳解析失败 ⇒ 两轮都退化成全量，**差点误判成"修复无效"**。
凡是用 Redis 取来的分数/时间进 URL，先用 `awk` 转定点小数再拼。

**另一处踩坑**：核对时把一次性实例的 `REDIS_DB` 设成 9（隔离库）却去读生产数据，
所有端点都返回空集合，看似"全挂"。正确姿势是把 `REDIS_DB=0` 且**不设**
`ALLOW_DB0_WRITES`——那就是应用自带的只读模式，正好用于这种比对。

### 3.4 安全事项（优先级最高）
- **撤销 Telegram 令牌**：它曾明文出现在对话中 → BotFather → `/mybots` → API Token → Revoke，
  然后到设置页填入新令牌。
- 同理，两个 GitHub 令牌也已出现在对话中，应一并撤销。

### 3.5 晚上主题重做（2026-10-03 完成，已部署）
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

**仍可做（收益低，先不做）**：实心按钮的白字对比度（`--lm-btn-primary-bg`
已把主按钮修到 4.75:1，其余变体未动）。
`.theme-terminal` 死代码已在 3.5 清理完毕。

### 3.6 可选清理 —— ✅ 已完成（2026-10-03）
**a) `style.css` 里 130 条 `.theme-terminal` 规则（死代码）**
主题只提供 默认/晚上，`applyTheme` 把非 `night` 的值一律回落到默认，
所以 `.theme-terminal` 永远不会被挂上。做法是**按大括号配平解析成 375 个顶层规则块**，
只删除"选择器列表里每一个选择器都含 `theme-terminal`"的块——
解析结果里**混合块为 0**，因此不存在误伤共用规则的可能。
`49554 → 32353` 字节（-17.2KB / -699 行）。

验证（两条独立判据）：
1. 选择器集合 diff：删除 130 个，逐个确认为 terminal-only；新增 0 个；
2. **像素级 A/B**：同一批静态页、同视口、**冻结动画后**逐像素比较新旧样式表，
   26 个「页面 x 主题」组合里 24~25 个逐像素完全相同，剩余 1~2 处的差值是
   2 个像素、最大通道差 19/255，且**在"同一样式表自己比自己"时同样出现**，
   属渲染/字体光栅噪声，与本次改动无关。

> 做这个 A/B 时踩到一个坑，值得记下：about 页有一张 **SVG 流程图**，既用 CSS
> 动画（`lmFlow`/`lmPulse`）也用 **SMIL `<animate>`**（移动的小圆点）。
> 只设 `prefers-reduced-motion: reduce` 拦不住 SMIL，第一轮 A/B 因此测到
> "610 像素差异"，其实是动画相位不同。要 `document.getAnimations()` **加上
> `svg.pauseAnimations()`** 才能真正冻结；冻结后噪声为 0。

**b) `logradarai:local`（旧镜像）**
- 先确认它不是 `hardened` 的别名：两者 image id 不同（`6aa104bf5abb` 33 小时前
  36 层、`python:3.11-slim` + app，属**未打加固补丁**的那版；`8287acc325fb`
  19 小时前），回退容器 `logaimonitor-py-backup2` 引用的是 `hardened`，
  `docker inspect` 确认其 `Config.Image=logradarai:hardened` 且状态 `exited`。
- 删除前把完整 `docker image inspect` 存到 `.handover-evidence/logradarai-local-inspect.json`；
  加固补丁本身在仓库里（`python-hardening.patch`），需要时可复现。
- **实际只回收了约 1MB**：`logradarai:local` 与 `hardened` 共享同一套基础层，
  `docker images` 里显示的 499MB 是含共享层的"名义大小"，
  `docker system df` 显示删除前后 5.238GB → 5.237GB。**"删了 499MB 镜像"
  ≠ "省了 499MB 磁盘"**，以 `docker system df` 为准。

**c) 顺带清理真正占空间的东西（本轮验证产生的）**
宿主机上最占地的其实是验证工具（各 3.38GB：`logai-preview:latest`、
`logai-ab:latest`）与构建缓存（8.19GB）。本次清理的实际回收：

| 动作 | 释放 |
|---|---|
| `docker rmi logradarai:local` | ~1MB（与 `hardened` 共享层，见上） |
| `docker rmi logai-preview logai-ab` | 约 6.8GB |
| `docker buildx prune -af` | 7.82GB |
| 合计 | 磁盘占用 11GB → 9.7GB，构建缓存 8.19GB → 0.37GB |

保留的镜像（都还有用，别删）：`logaimonitor-cs:latest`（生产）、
`mcr.microsoft.com/dotnet/sdk:8.0`（1.23GB，
**Dockerfile 构建阶段必须用它**，删了就没法再构建 .NET 镜像）、`redis:7-alpine`、
`alpine:3.19`。
验证工具需要时按 `.preview/README.md` 重建（约 1 分钟）。

### 3.7 部署方式的一个隐患（本次踩到，已缓解）
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
  `PATH`/`LANG`/`GPG_KEY`/`PYTHON_VERSION`/`PYTHON_SHA256`——见 3.8）；数据不受影响
  （Redis 卷未动，`logs:timeline` 仍在增长，告警 13 条保留）。

### 3.8 已归档：旧容器曾带着另一套基础镜像的环境变量（现已无关）
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

### 3.9 与旧实现解绑（2026-10-03 完成）
**决策**：今后只用 C#/.NET 8 一套实现开发，不再维护"另一个语言版本"的对照关系。

**已删除（宿主机）**

| 项 | 说明 |
|---|---|
| 容器 `logaimonitor-py-backup2` | 旧回退容器，删除 |
| 镜像 `logradarai:hardened`（499MB） | 旧实现加固后的**唯一副本**，删除前已归档 |

归档在宿主机 `/root/python-fallback-archive/`：
- `logradarai-hardened-20261003.tar.gz`（**126MB**，已校验可 `docker load`）；
- `container-config.json`（原容器完整 inspect 配置，便于按原参数重建）；
- `RESTORE.md`（恢复步骤与注意事项）。

**已删除（工作区）**：`GitHubExport/`（旧实现源码 425 文件 / 7.8M）、
`GitHubExport-python-backup-*.bundle`、`python-hardening.patch`、`csyslog/`（C 版采集器草稿）。

**已改写**：README、HANDOVER 与源文件里的 **149 处**旧实现引用，全部是注释/文档行
（已核查：**没有一处在字符串或逻辑里**，因此零行为风险）。改写原则是保留"为什么这样
设计/这个坑"的信息，只去掉"与另一半对照"的叙述，例如
`DELIBERATE DIVERGENCE: Python …` → `DELIBERATE SECURITY CHOICE: …`。

**刻意保留不变的三样**
1. **接口契约与字段集**（`is_admin`、10 字段告警、键序、非 ASCII 转义等）——
   界面与外部脚本都依赖它，改这些是破坏性变更，与"解绑"无关；
2. **Redis 键布局**——生产数据在其上，是稳定的对外契约；
3. **`--parse-compare <corpus.jsonl>`** 及其语料生成脚本——它比对的是
   "解析器 vs 已捕获语料"，语料是回归资产，不依赖旧实现存活。

**旁注**：网络名 `logradarai_logaimonitor-net`、卷名 `logradarai_redis-data`
是历史部署留下的名字。改名要重建网络/卷，而卷名一改就必须迁移全部生产数据，
**收益纯 cosmetic、风险不小**，因此保留原名。

### 3.10 读端点/采集/维护路径的分批管道化 —— ✅ 已完成（2026-10-05）
**主题**：把项目里"循环内逐条 `await` Redis"的串行往返统一收敛为**分批管道**，
每批一次往返。这批改动不改任何接口契约 / 字段集 / 键布局，只消除往返等待。

**改动清单（15 个源码文件）**
- 新增共享助手 `RedisStore.HashGetAllBatchAsync(ids, chunkSize=500)`。
- 读端点：`/api/logs`、`/api/ai-history`、`/api/alerts`（页与 acknowledged 扫描
  两个分支）、`/api/filters`、`/api/users` 的逐条 `HGETALL` → 一批取回；
  `/api/hosts` 的逐键 `ZCARD`、`/api/syslog/diagnostics` 的每客户端三读
  （hash+protocols+per-minute）、`/api/stats` 的七个计数/索引读取 → 单批。
- 采集热路径：`ClientTracker.TrackAsync` 约 8 次串行往返 → 单批（首见初始化用
  字段级 `HSETNX`，顺带消除 EXISTS↔HSET 之间的并发竞态）；`LogWriter.StoreAsync`
  的 `HSET` 并入索引批。
- `LoadFiltersAsync`：每条日志重读规则 → 进程内 2 秒 TTL 缓存 + 过滤写接口
  （FilterWriteApi）每次写后 `InvalidateFilterCache()`。同进程改动即时生效，
  外部直改 Redis 最多等 2 秒——与 60 秒筛选交集缓存同一哲学。
- 维护/写：`LogMaintenance.DeleteBySourceAsync`（含"来源索引缺失时扫整条时间线"
  的回退路径，原来 300 万条逐条 HGET 能占住端点几分钟）、`ClearAllAsync`、
  `AlertMaintenance.AcknowledgeAllAsync` / `ClearAcknowledgedAsync`、
  `DELETE /api/ai-history` 清空，全部批量管道化。

**验证（三层，均为正面判据）**
1. 隔离库（DB 9）自测 18/18 `ALL PASSED`（`client-tracker`、`ingest`、
   `maintenance`、`alert`、`filter`、`filter-load`、`parse`、`json`、`session`、
   `health`、`cleanup`、`scheduler`、`commit`、`prompt`、`ai-history`、`telegram`、
   `docker`、`docker-poll`）。`runner`/`ai` 两项需要真实 AI 端点凭据，在测试
   容器里失败，但**新旧镜像失败方式逐字一致**，属环境性而非回归。
2. 生产数据只读 A/B：新旧镜像各起一个 `REDIS_DB=0` 且不设 `ALLOW_DB0_WRITES`
   的实例，同一 `SECRET_KEY`，并行抓取后逐字节比较——**全部读端点一致**，含
   3MB 冻结窗口、1.58MB `logs1000`、diagnostics、users、settings。
3. DB 9 合成数据 A/B：新旧实例共用 DB 9（各开一个 syslog 端口），同源流量下
   diagnostics 逐字节一致、过滤器缓存失效端到端生效（新建规则→下一条匹配日志
   立即告警）、告警链路两端都触发。

**计时**：500 条突发 OLD 274–373ms → NEW 182ms（应用层零丢弃；两端对称的少量
丢包是内核级 UDP 突发损耗，非应用行为）。`/api/logs?limit=1000` 0.10s → 0.04s、
`/api/ai-history?limit=1000` 0.13s → 0.06s——剩余耗时主要是 JSON 序列化 +
`EscapeNonAscii`（逐字符）+ 1.5MB 传输，Redis 往返已基本消除。

**踩坑记录**：本仓库 Dockerfile 的 `ENTRYPOINT ["dotnet","LogAI.Web.dll"]` 意味着
`docker run <image> dotnet LogAI.Web.dll --x-selftest` 会把 `dotnet` 当作 `args[0]`，
自测分支匹配不上，整包以 Web 服务形态空跑。正确姿势是 `docker run <image> --x-selftest`
（只传 flag）。README 里那句 `dotnet LogAI.Web.dll --parse-selftest` 对 `docker exec`
成立、对 `docker run` 不成立，别照抄。

### 3.11 界面对比度修复 + 细节打磨 —— ✅ 已完成（2026-10-05）
**方法**：`.preview` 工具（无头 Chromium 截图 + 逐元素对比度审计）跑线上真实页面，
以审计结果（`contrast.json` / `modal-contrast.json`）为唯一改动依据。

**改动（仅 `wwwroot/css/refined.css`，叠加层不改原版 style.css）**
- 夜间侧栏/分页当前项：`--lm-accent`（#58a6ff）+白字 2.53:1 → 专用
  `--lm-nav-active-bg`（#2b68c4）5.41:1。
- 彩色实心按钮 Ack/Clear/Close：`success` #28a745（3.13:1）、`danger` #dc3545、
  `secondary` #6c757d 收敛为令牌 `--lm-btn-success/danger/secondary-bg`
  （#18752f/#b02a37/#5a6268），对白字与夜间 off-white #e8ecf2 双重 ≥4.5:1；
  夜间两条通用按钮文字规则都排除这些变体（否则 background:transparent / ink 色
  会把白字压成 3.8–4:1 甚至透明底白字）。
- 灰色标签文字 `--lm-ink-3` #8a8f98（白底 3.25:1）→ #6b7280（白/次级面均 ≥4.55:1）。
- `.log-host` 链接蓝 #2196F3（白底 3.12:1）→ 仅白天覆盖 #0b6eb8；侧栏 logo 渐变
  亮端 #00a3cc（2.95:1）→ #0077b6。
- 打磨：顶栏毛玻璃（`--lm-header-bg` 改半透明 + `.main-header` backdrop-filter）、
  侧栏当前项左侧内嵌指示条、统计卡悬停抬升、按钮按下反馈、键盘 `:focus-visible`
  焦点环、`prefers-reduced-motion` 收窄动效。

**验证**：审计前后 **页面级 28→1、弹窗级 35→0**；剩余 1 处是登录按钮
`linear-gradient` 背景（审计跳过 `background-image`，误报 1:1），实际两端
#0066cc/#004c99 对白字均 ≥5.3:1，无需处理。夜间两套 26 个「页面×主题」与 8 个
弹窗全部渲染无错误。

**部署说明**：3.10 与 3.11 分两个 commit（`perf(redis)`、`fix(ui)`），一起
`docker build -t logaimonitor-cs:latest` 后按 3.7 的脚本参数重建容器（机密仍从
旧容器 `docker inspect` 读回，不落盘）。部署后 health 200、采集恢复 dropped=0、
环境变量 26/26 对齐。

### 3.12 序列化快速路径 + 密码式部署脚本 —— ✅ 已完成（2026-10-05）
**a) `EscapeNonAscii` 纯 ASCII 快速路径（`ReadApi.cs`）**
响应契约要求非 ASCII 转义为 `\uXXXX`、HTML 字符保持原样，所以序列化分两步：
`JsonSerializer`（`UnsafeRelaxedJsonEscaping`）→ 逐字符 `EscapeNonAscii`。第二步
对纯 ASCII payload 是纯浪费——`/api/logs` 整页 1.5MB 逐字符 `Append` 一遍。

现改为先用 .NET 8 的 SIMD 向量化 `IndexOfAnyExceptInRange((char)0, (char)0x7F)`
扫描：纯 ASCII 直接原样返回（与旧实现逐字符复制的输出**逐字节相同**）；命中
非 ASCII 才走转义循环。扫描对 1.5MB 量级几乎免费，因此命中非 ASCII 时也没有
可感知的回退。

验证：生产只读 A/B 逐字节一致，含 84 处中文转义的 1.49MB 冻结窗口、以及
alerts/filters/ai-history 等 ASCII 端点。（计时上收益取决于 payload 是否纯 ASCII：
大页若混有中文日志则快路径不触发，此时退化为"一次向量化扫描 + 原循环"，无回归。）

**b) 密码式部署脚本 `scripts/deploy-cs-password.sh`**
面向"无法配置 SSH 密钥"的环境（只读 `$HOME`、只有密码）。与 `deploy-cs.sh`
同样的四步，但：(1) 密码经 throwaway `SSH_ASKPASS` helper 交给 ssh，不落盘、
不进命令行；(2) build + 读机密 + 重建容器在**远程单会话**内完成，四个机密
从不离开主机、也不写到本地文件系统。用法见第 5 节，已端到端实测两次真实部署。

**踩坑记录（脚本）**：`$SSH` 变量若含空格路径必须加引号——本脚本的语义是
"单个可执行包装器路径"（auth 与 ssh 选项都封装在 wrapper 内），不是多词命令。
另：容器重建后 ASP.NET 需数秒绑定端口，verify 步骤必须轮询（单次 curl 会误报
`health=000`，看起来像部署失败、实际已成功）。

### 3.13 告警详情 Message 内容夜间看不清 —— ✅ 已完成（2026-10-05）
**现象**：Alert Detail 弹窗里 Message 字段的内容文字看不清（夜间主题）。
**根因**：`showAlertDetail`（app.js）给 Message 块写了硬编码内联样式
`background:#f8f9fa`（浅灰底）却未设文字色；夜间主题下文字继承 `--lm-ink`
（#e8ecf2 近白）→ **浅字压浅底**。这类"JS 生成的内联样式写死颜色、不走主题
令牌"是 3.11 之后漏掉的一类，弹窗内容是运行时填充、静态 CSS 覆盖不到。

**修复**：Message 块改用令牌（`background: var(--lm-subtle)` + `color: var(--lm-ink)`
+ `border: var(--lm-line)`），并顺手把同一弹窗流程里的其余硬编码色收敛：
AI 结果分隔线 `#e9ecef`→`var(--lm-line)`、注释 `#888`→`var(--lm-ink-3)`、
失败红字 `#c62828`→`var(--lm-danger-ink)`、受影响主机徽标
`var(--lm-accent)`→`var(--lm-nav-active-bg)`（夜间 #58a6ff 白字仅 2.53:1）。

**验证**：扩展 `.preview/modal.js` 覆盖 `alertDetailModal`（此前只测
logDetailModal），审计 `alerts.alertDetailModal` 的 default/night 均为
**0 低对比度**。

**教训**：弹窗/抽屉等"打开才现填"的界面，颜色必须走令牌；写死十六进制色在
夜里会悄悄坏掉，而静态 CSS 审计看不见运行时填的内容——所以 modal 审计的
harness 也要跟着真实填充逻辑走。

### 3.14 内存诊断 + 保留期死配置清理 —— ✅ 已完成（2026-10-05）
**现状**：应用容器 276 MiB（正常）；Redis 7.04 GiB / maxmemory 9 GiB（占宿主
内存 61%）。日志 328 万条 × ~2.1KB/条 ≈ 6.9GB，ai_history 18764 条 ≈ 130MB。

**发现（保留期配置打架）**：主采集路径的保留期只认 settings 里的
`log_retention_hours`（现为 720h=30 天）；部署脚本却一直带着
`-e LOG_RETENTION_HOURS=12`，而这个环境变量只在 `Program.cs` 的 HTTP ingest
旁路 LogWriter 里被读——于是"部署写着 12 小时、实际留 30 天"，且 HTTP 旁路的
TTL(12h) 与主路径(720h) 还不一致。

**本次清理**：从 `deploy-cs.sh` 与 `deploy-cs-password.sh` 删掉死配置
`LOG_RETENTION_HOURS=12`（旁路自动回退到 720，与主路径一致）；在 `AppHost.cs`
保留期读取处加注释说明唯一来源是 settings。**保留期仍为 720h，未改**（用户
明确要求保持 30 天）。

**遗留隐患（待决策）**：按当前 ~3.8 条/秒 ≈ 690MB/天 增长，Redis 会在约 3 天
内撞上 9G maxmemory，届时 `volatile-lru` 会静默淘汰最旧日志（日志写入后不再被
读，LRU≈最旧），实际保留期被压到 ~13 天——「保持 30 天」会被内存上限架空。
选项：A) 继续观察；B) 上调 Redis maxmemory 到 ~11G（宿主尚有 ~4GB 余量，才能真
留满 30 天）；C) 接受更短保留期（内存降到 0.5–2GB，可控可预期）。当前按 A 观察。

### 3.15 幻影主机/来源清理 —— ✅ 已完成（2026-10-05）
**现象**：Logs 页「所有主机」出现大量不存在的来源/主机。
**根因**：生产 `logaimonitor` 的 DockerCollector 会采集宿主机上**所有运行中容器**
的日志（排除列表 `docker_excluded_containers` 只有 `["logaimonitor","logaimonitor-redis"]`）。
历次验证/自测会话建的临时容器（`logai-*`、`ab-new`、`busy_davinci`、`pedantic_turing`
等）没被排除，它们的日志以 `source=docker:<容器名>` 落入生产 Redis；容器删除后
这些来源就变成「幻影」。另有验证脚本直接发的合成主机名（alertDiagHost、
envCheckHost、netTestHost、rtFinal、tcpCheck…）。

**本次清理**：
- `--purge-sources "docker:" --confirm` 删 63 个来源、2558 条日志；
- 按主机名精准删 10 个合成主机名（~10 条日志）+ 死索引 `LogRadarAI-test`、
  `docker-host`、`10.99.99.9`。
- 结果：来源 95→31、主机 53→42，均为真实设备。`ShellCrash`（7490 条，来源
  192.168.50.11，跑代理工具）确认是真实设备，保留。

**教训（重要）**：今后在宿主机上起任何临时容器做验证，要么把它加进生产的
`docker_excluded_containers`，要么验证完立刻 `--purge-sources "docker:<名>" --confirm`
清掉；否则每建一个测试容器，生产日志库就多一个幻影 `docker:*` 来源。宿主机的
docker socket 是只读挂进 logaimonitor 的，它看得见所有容器。

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
# 构建并部署 —— 用脚本，不要手敲下面那两行（会漏掉"重建容器"这一步，见 3.7）
#   KEY=~/.ssh/logai_deploy sh scripts/deploy-cs.sh
# 脚本做四件事：同步 → 构建 → 从旧容器读回 4 个机密 → 同参数重建容器 → 健康检查。
# 前置：ssh-keygen -t ed25519 -N "" -f ~/.ssh/logai_deploy && ssh-copy-id -i ~/.ssh/logai_deploy.pub root@192.168.50.6
# （脚本内部要二次 ssh 读旧容器环境变量，交互式密码在那里答不了。）

# 无法配密钥的环境（只读 $HOME、只有密码）用密码版（见 3.12）：
#   SSHPASS='<root 密码>' sh scripts/deploy-cs-password.sh
# 同一四步，但密码经 throwaway SSH_ASKPASS、build+读机密+重建在远程单会话完成。

# 健康与心跳
curl -s -o /dev/null -w '%{http_code}\n' http://192.168.50.6:5059/api/health
ssh root@192.168.50.6 "docker logs logaimonitor 2>&1 | grep '\[Health\]' | tail -3"

# 回退到上一版 .NET 镜像（保留数据）
#   本项目已与 Python 解绑，不再有跨语言回退。回退请用上一版镜像：
#   docker rm -f logaimonitor && docker run -d --name logaimonitor ... <上一版镜像>
#   （参数见 scripts/deploy-cs.sh 里的 docker run，或 HANDOVER 3.7）
#   Python 旧版仅作考古归档：宿主机 /root/python-fallback-archive/（见 3.9）

# 主题/界面改动的视觉验证（截线上页面 + 逐元素对比度审计）
#   见 ../.preview/README.md
```
