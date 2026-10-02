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

### 3.1 补两个静默删除入口的日志（各 1 行，建议先做）
```csharp
// LogMaintenance.ClearAllAsync    → 清空全部日志
// LogMaintenance.DeleteBySourceAsync → 按来源删除
```
背景：手动清空告警原本不留痕迹，导致一次"37 条告警去哪了"无法自查（后确认是用户手动删除）。
`/api/alerts/clear` 已补日志，这两个入口同样静默。
**验收**：调用后日志出现删除条数。

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
- `style.css` 里 141 处 `.theme-terminal` 规则：**已不可达**（主题只提供默认/晚上）→ 可删但收益低。

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
# 构建并部署（本仓库 → 主机构建树 → 镜像 → 容器）
tar czf - --exclude=bin --exclude=obj src templates wwwroot Dockerfile README.md DEPLOY.md \
  docker-compose.yml .env.example | ssh root@192.168.50.6 \
  'rm -rf /root/logai-cs/src /root/logai-cs/templates /root/logai-cs/wwwroot && mkdir -p /root/logai-cs && tar xzf - -C /root/logai-cs'
ssh root@192.168.50.6 'cd /root/logai-cs && docker build -t logaimonitor-cs:latest .'

# 健康与心跳
curl -s -o /dev/null -w '%{http_code}\n' http://192.168.50.6:5059/api/health
ssh root@192.168.50.6 "docker logs logaimonitor 2>&1 | grep '\[Health\]' | tail -3"

# 回退到 Python 版（保留数据）
ssh root@192.168.50.6 'docker rm -f logaimonitor && docker rename logaimonitor-py-backup2 logaimonitor && docker start logaimonitor'
```
