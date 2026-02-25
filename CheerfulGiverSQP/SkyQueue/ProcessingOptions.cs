using Microsoft.Extensions.Configuration;

namespace CheerfulGiverSQP.SkyQueue;

public sealed record ProcessingOptions(
    int PollIntervalSeconds,
    int BatchSize,
    int StaleProcessingMinutes,
    int MaxAttempts)
{
    public static ProcessingOptions FromConfiguration(IConfiguration cfg)
    {
        var poll = cfg.GetValue<int?>("Processing:PollIntervalSeconds") ?? 10;
        var batch = cfg.GetValue<int?>("Processing:BatchSize") ?? 10;
        var stale = cfg.GetValue<int?>("Processing:StaleProcessingMinutes") ?? 30;
        var max = cfg.GetValue<int?>("Processing:MaxAttempts") ?? 5;

        // Guard rails
        poll = poll < 1 ? 1 : poll;
        batch = batch < 1 ? 1 : batch;
        stale = stale < 1 ? 1 : stale;
        max = max < 1 ? 1 : max;

        return new ProcessingOptions(poll, batch, stale, max);
    }
}
