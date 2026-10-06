// 磁盘冷存储：把超过归档阈值的日志哈希搬到 SQLite，Redis 只保留有序集合索引。
//
// 为什么这样分层（方案 C，见 HANDOVER 3.16）：
//   日志的内存大头是 `log:<µs>` 哈希（约 1.1KB/条），而有序集合索引（timeline +
//   source/host/severity）每条只有 ~70 字节。把哈希落盘、索引留内存，30 天保留的
//   内存从 ~7GB 降到 ~1GB，且时间/维度筛选（ZSET 求交）与仪表盘/实时推送完全不变。
//
// 只读回退：已归档日志在 Redis 里哈希缺失，读路径（/api/logs、/api/logs/{id}）
// 回落到这里按 id 取字段；清理任务到期时也从这里读 source/hostname/severity 以便
// 从维度 ZSET 摘除，然后一并删除 SQLite 记录（遵守保留期）。
//
// 线程模型：每次操作各开一条连接（SQLite 连接开销极小，归档/回退都是低频操作），
// 避免单连接跨线程复用；WAL 模式让归档写与读回退可并发。

using System.Text.Json;
using Microsoft.Data.Sqlite;
using StackExchange.Redis;

namespace LogAI.Core.Store;

/// <summary>一条待归档的日志记录（字段集与 Redis 哈希完全一致）。</summary>
public sealed record ArchivedLog(
    string Id, string Source, string SourceType, string Hostname,
    string Program, string Facility, string Severity, string Message,
    string Timestamp, string Analyzed, string? Pid, string? ProcId, string? MsgId,
    double Score)
{
    /// <summary>从 Redis 哈希构造，字段缺失按空串处理。</summary>
    public static ArchivedLog FromHash(HashEntry[] hash, double score)
    {
        var f = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
        string G(string k) => f.GetValueOrDefault(k) ?? "";
        // 可选字段缺失时存 NULL（而非空串）：Redis 哈希里没有这些字段，回退读取时
        // 也要原样省略，否则归档日志会多出 "pid":"" 之类，破坏响应逐字节契约。
        string? O(string k) => f.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        return new ArchivedLog(
            G("id"), G("source"), G("source_type"), G("hostname"),
            G("program"), G("facility"), G("severity"), G("message"),
            G("timestamp"), G("analyzed"), O("pid"), O("proc_id"), O("msg_id"),
            score);
    }
}

public sealed class LogArchive
{
    private readonly string _connString;

    public LogArchive(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connString = $"Data Source={path}";
        // WAL + 适度同步：归档写与读回退可并发，且崩溃不会整库损坏。
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS logs (
                    id TEXT PRIMARY KEY,
                    source TEXT NOT NULL,
                    source_type TEXT,
                    hostname TEXT,
                    program TEXT,
                    facility TEXT,
                    severity TEXT,
                    message TEXT,
                    timestamp TEXT,
                    analyzed TEXT,
                    pid TEXT,
                    proc_id TEXT,
                    msg_id TEXT,
                    score REAL
                );
                CREATE INDEX IF NOT EXISTS idx_logs_score ON logs(score);
                CREATE TABLE IF NOT EXISTS hashes (
                    key TEXT PRIMARY KEY,
                    fields TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    /// <summary>批量归档（幂等：主键冲突忽略）。单事务，千条级。 </summary>
    public async Task ArchiveAsync(IReadOnlyList<ArchivedLog> logs, CancellationToken ct = default)
    {
        if (logs.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO logs
                (id, source, source_type, hostname, program, facility, severity, message, timestamp, analyzed, pid, proc_id, msg_id, score)
            VALUES
                ($id, $source, $source_type, $hostname, $program, $facility, $severity, $message, $timestamp, $analyzed, $pid, $proc_id, $msg_id, $score);
            """;
        var p = new SqliteParameter[14];
        for (int i = 0; i < p.Length; i++) { p[i] = cmd.CreateParameter(); cmd.Parameters.Add(p[i]); }
        string[] names = ["$id", "$source", "$source_type", "$hostname", "$program", "$facility", "$severity", "$message", "$timestamp", "$analyzed", "$pid", "$proc_id", "$msg_id", "$score"];
        for (int i = 0; i < names.Length; i++) p[i].ParameterName = names[i];

        foreach (var log in logs)
        {
            p[0].Value = log.Id; p[1].Value = log.Source; p[2].Value = log.SourceType;
            p[3].Value = log.Hostname; p[4].Value = log.Program; p[5].Value = log.Facility;
            p[6].Value = log.Severity; p[7].Value = log.Message; p[8].Value = log.Timestamp;
            p[9].Value = log.Analyzed; p[10].Value = (object?)log.Pid ?? DBNull.Value;
            p[11].Value = (object?)log.ProcId ?? DBNull.Value; p[12].Value = (object?)log.MsgId ?? DBNull.Value;
            p[13].Value = log.Score;
            // 同步执行：SQLite 驱动本身是同步的，逐行 await 只会叠加 Task.Run 调度开销，
            // 5000 行的批量归档会因此慢数秒甚至更久。
            cmd.ExecuteNonQuery();
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// 批量取回已归档日志的字段，形状与 Redis 的 HashGetAll 一致（HashEntry[]，
    /// 缺失返回空数组）。供 /api/logs 与 /api/logs/{id} 的哈希 miss 回退使用。
    /// </summary>
    public async Task<HashEntry[][]> GetFieldsBatchAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var result = new HashEntry[ids.Count][];
        var byId = new Dictionary<string, HashEntry[]>(StringComparer.Ordinal);

        const int Chunk = 500;   // WHERE id IN (...) 每个 id 一个参数，500 < SQLite 上限 999
        for (int offset = 0; offset < ids.Count; offset += Chunk)
        {
            int size = Math.Min(Chunk, ids.Count - offset);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var inClause = new string[size];
            for (int i = 0; i < size; i++)
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = "$p" + i;
                prm.Value = ids[offset + i];
                cmd.Parameters.Add(prm);
                inClause[i] = prm.ParameterName;
            }
            cmd.CommandText = $"""
                SELECT id, source, source_type, hostname, program, facility, severity, message, timestamp, analyzed, pid, proc_id, msg_id
                FROM logs WHERE id IN ({string.Join(", ", inClause)});
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            string[] cols = ["id", "source", "source_type", "hostname", "program", "facility", "severity", "message", "timestamp", "analyzed", "pid", "proc_id", "msg_id"];
            while (await reader.ReadAsync(ct))
            {
                var fields = new List<HashEntry>(13);
                for (int c = 0; c < cols.Length; c++)
                {
                    if (reader.IsDBNull(c)) continue;
                    fields.Add(new HashEntry(cols[c], reader.GetString(c)));
                }
                byId[reader.GetString(0)] = fields.ToArray();
            }
        }

        for (int i = 0; i < ids.Count; i++)
            result[i] = byId.TryGetValue(ids[i], out var f) ? f : [];
        return result;
    }

    /// <summary>保留期到期：删除一条归档记录（与 Redis 维度摘除一并由清理任务调用）。</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM logs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>批量删除归档记录（保留期到期，清理任务每页调用一次）。</summary>
    public async Task DeleteBatchAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM logs WHERE id = $id;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$id";
        cmd.Parameters.Add(p);
        foreach (var id in ids)
        {
            p.Value = id;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>当前归档条数（诊断/心跳用）。</summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM logs;";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ------------------------------------------------------------ 通用哈希归档
    // 分析历史（ai_history:*）与告警（alert:*）的字段集各不相同（且单条分析还带
    // 额外字段），不适合像日志那样一张类型化表。这里用一张通用表存整条哈希的 JSON，
    // 读回退按 key 反序列化回 HashEntry[]，与 Redis HashGetAll 形状一致。

    /// <summary>批量归档任意哈希（幂等：主键冲突忽略）。整条哈希序列化为 JSON。</summary>
    public async Task ArchiveHashesAsync(IReadOnlyList<(string Key, HashEntry[] Fields)> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO hashes (key, fields) VALUES ($key, $fields);";
        var pk = cmd.CreateParameter(); pk.ParameterName = "$key"; cmd.Parameters.Add(pk);
        var pf = cmd.CreateParameter(); pf.ParameterName = "$fields"; cmd.Parameters.Add(pf);
        foreach (var (key, fields) in rows)
        {
            pk.Value = key;
            pf.Value = SerializeHash(fields);
            cmd.ExecuteNonQuery();   // 同步执行（同日志归档，避免逐行 await 的调度开销）
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>批量取回已归档哈希，形状与 Redis HashGetAll 一致（缺失返回空数组）。</summary>
    public async Task<HashEntry[][]> GetHashesBatchAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        var result = new HashEntry[keys.Count][];
        var byKey = new Dictionary<string, HashEntry[]>(StringComparer.Ordinal);

        const int Chunk = 500;
        for (int offset = 0; offset < keys.Count; offset += Chunk)
        {
            int size = Math.Min(Chunk, keys.Count - offset);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var inClause = new string[size];
            for (int i = 0; i < size; i++)
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = "$p" + i;
                prm.Value = keys[offset + i];
                cmd.Parameters.Add(prm);
                inClause[i] = prm.ParameterName;
            }
            cmd.CommandText = $"SELECT key, fields FROM hashes WHERE key IN ({string.Join(", ", inClause)});";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                byKey[reader.GetString(0)] = DeserializeHash(reader.GetString(1));
        }

        for (int i = 0; i < keys.Count; i++)
            result[i] = byKey.TryGetValue(keys[i], out var f) ? f : [];
        return result;
    }

    /// <summary>保留期到期：删除一条已归档哈希。</summary>
    public async Task DeleteHashAsync(string key, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM hashes WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>批量删除已归档哈希（保留期到期，清理任务每页调用一次）。</summary>
    public async Task DeleteHashesBatchAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM hashes WHERE key = $key;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$key";
        cmd.Parameters.Add(p);
        foreach (var key in keys)
        {
            p.Value = key;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>已归档哈希条数（诊断/心跳用）。</summary>
    public async Task<long> CountHashesAsync(CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM hashes;";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static string SerializeHash(HashEntry[] fields)
    {
        var dict = new Dictionary<string, string>(fields.Length, StringComparer.Ordinal);
        foreach (var f in fields) dict[f.Name.ToString()] = f.Value.ToString();
        return JsonSerializer.Serialize(dict);
    }

    private static HashEntry[] DeserializeHash(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (dict is null) return [];
        var fields = new HashEntry[dict.Count];
        int i = 0;
        foreach (var kv in dict) fields[i++] = new HashEntry(kv.Key, kv.Value);
        return fields;
    }
}
