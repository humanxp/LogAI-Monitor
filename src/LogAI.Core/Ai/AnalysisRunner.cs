// One analysis cycle: pick a batch, ask the model, repair the reply, commit.
//
// The policy details come from the Python implementation:
//   * an unparseable reply is retried once with the corrective prompt;
//   * a second failure records nothing and leaves the batch queued for the next
//     tick (the caller counts attempts and retires after three);
//   * a successful run writes ai_history and applies the result to every log.

namespace LogAI.Core.Ai;

using LogAI.Core.Store;

public sealed class AnalysisRunner(RedisStore store, AiClient client, AiHistoryWriter history, int batchSize = 500)
{
    public sealed record Outcome(string Status, int Count, string? HistoryId, string? Error);

    public async Task<Outcome> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var batch = await UnanalyzedBatch.FetchAsync(store, batchSize, cancellationToken);
        if (batch.Count == 0) return new Outcome("empty", 0, null, null);

        var ids = batch.Select(item => item.Id).ToList();
        var logs = batch.Select(item => (IReadOnlyDictionary<string, string>)item.Fields).ToList();

        string prompt = PromptBuilder.BatchPrompt(PromptBuilder.LogSummary(logs));
        string reply = await client.CompleteAsync(prompt, cancellationToken: cancellationToken);
        var analysis = JsonExtractor.Extract(reply);

        if (analysis is null)
        {
            reply = await client.CompleteAsync(PromptBuilder.CorrectivePrompt(prompt), cancellationToken: cancellationToken);
            analysis = JsonExtractor.Extract(reply);
        }

        if (analysis is null)
            return new Outcome("failed", batch.Count, null, "reply was not a valid JSON object after a corrective retry");

        string historyId = await history.WriteAsync(ids, analysis);
        await AnalysisCommit.ApplyAsync(store, ids, analysis, cancellationToken);
        return new Outcome("analyzed", batch.Count, historyId, null);
    }
}
