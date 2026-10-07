// One analysis cycle: pick a batch, ask the model, repair the reply, commit.
//
// The analysis policy:
//   * an unparseable reply is retried once with the corrective prompt;
//   * a second failure records nothing and leaves the batch queued for the next
//     tick (the caller counts attempts and retires after three);
//   * a successful run writes ai_history and applies the result to every log.

namespace LogAI.Core.Ai;

using LogAI.Core.Store;

public sealed class AnalysisRunner(RedisStore store, AiClient client, AiHistoryWriter history,
                                       int fallbackBatchSize = 500, int fallbackSampleLimit = 0)
{
    public sealed record Outcome(string Status, int Count, string? HistoryId, string? Error);

    /// <summary>单批分析量的硬上限，与 AppHost.MaxAnalysisBatch 保持一致。</summary>
    public const int MaxBatch = 5000;

    /// <summary>
    /// 每轮开始时重新读取批次上限与样本上限。
    ///
    /// 之前这两个值只在启动时读一次并固定下来，于是设置页上明明写着
    /// "Applied immediately"，改完却必须重启容器——用户看到的告警冷却等
    /// 是即时生效的，很容易据此以为这里也是。分析任务本身几分钟才跑一次，
    /// 每轮多一次 HGET 完全可以忽略，换来的是文案与行为一致。
    /// 读取失败时退回启动时的取值，不让一次抖动影响分析。
    /// </summary>
    private (int BatchSize, int SampleLimit, bool CacheOptimized, bool ThinkingEnabled) ReadLimits()
    {
        try
        {
            var settings = store.GetSettingsAsync().GetAwaiter().GetResult();

            int batch = 0;
            foreach (string key in new[] { "max_logs_per_analysis", "batch_size" })
            {
                if (settings.TryGetValue(key, out object? raw) &&
                    int.TryParse(RedisStore.ToText(raw).Trim().Trim('"'), out int parsed) && parsed > 0)
                { batch = parsed; break; }
            }
            if (batch <= 0) batch = fallbackBatchSize;
            batch = Math.Min(batch, MaxBatch);

            int sample = 0;
            if (settings.TryGetValue("batch_sample_limit", out object? sampleRaw))
                int.TryParse(RedisStore.ToText(sampleRaw).Trim().Trim('"'), out sample);
            if (sample <= 0) sample = fallbackSampleLimit;

            bool cacheOptimized = AiClient.AiCacheOptimizedEnabledIn(settings);
            bool thinkingEnabled = AiClient.AiThinkingEnabledIn(settings);
            return (batch, sample, cacheOptimized, thinkingEnabled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return (fallbackBatchSize, fallbackSampleLimit, false, true);
        }
    }

    public async Task<Outcome> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var (batchSize, sampleLimit, cacheOptimized, thinkingEnabled) = ReadLimits();
        var batch = await UnanalyzedBatch.FetchAsync(store, batchSize, cancellationToken);
        if (batch.Count == 0) return new Outcome("empty", 0, null, null);

        var ids = batch.Select(item => item.Id).ToList();
        var logs = batch.Select(item => (IReadOnlyDictionary<string, string>)item.Fields).ToList();

        // 取批上限与送给模型的样本数是两件事：批次决定"这一轮处理多少条"，
        // 样本上限决定"其中多少条真正进入提示词"（按级别优先）。
        string summary = PromptBuilder.LogSummary(logs, sampleLimit);
        string prompt = cacheOptimized
            ? PromptBuilder.BatchPromptCached(summary)
            : PromptBuilder.BatchPrompt(summary);
        string reply = await client.CompleteAsync(prompt, cancellationToken: cancellationToken,
                                                 enableThinking: thinkingEnabled);
        var analysis = JsonExtractor.Extract(reply);

        // 解析失败，或模型漏了必需字段（overall_status/issues/recommendations/
        // critical_count）时，纠正性重试一次。
        if (analysis is null || !JsonExtractor.HasRequiredFields(analysis))
        {
            reply = await client.CompleteAsync(PromptBuilder.CorrectivePrompt(prompt), cancellationToken: cancellationToken,
                                               enableThinking: thinkingEnabled);
            analysis = JsonExtractor.Extract(reply);
        }

        if (analysis is null)
            return new Outcome("failed", batch.Count, null, "reply was not a valid JSON object after a corrective retry");

        // 给 issues/处理建议补 "[HOST] " 前缀（python-legacy 的 _ensure_host_prefix）。
        var hosts = logs
            .Select(l => l.GetValueOrDefault("hostname") ?? l.GetValueOrDefault("source") ?? "")
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (analysis is System.Text.Json.Nodes.JsonObject obj && hosts.Count > 0)
            JsonExtractor.EnsureHostPrefix(obj, hosts);

        string historyId = await history.WriteAsync(ids, analysis);
        await AnalysisCommit.ApplyAsync(store, ids, analysis, cancellationToken);
        return new Outcome("analyzed", batch.Count, historyId, null);
    }
}
