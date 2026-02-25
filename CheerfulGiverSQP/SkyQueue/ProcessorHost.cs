using System;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverSQP.SkyQueue;

public sealed class ProcessorHost : IDisposable
{
    private readonly SkyTransactionProcessor _processor;
    private readonly ProcessingOptions _options;
    private readonly Func<CancellationToken, Task>? _preflight;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ProcessorHost(SkyTransactionProcessor processor, ProcessingOptions options, Func<CancellationToken, Task>? preflight = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options;
        _preflight = preflight;
    }

    public bool IsRunning { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler<string>? LogLine;

    public async Task StartAsync()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();

        // Fail fast before we start mutating queue rows.
        if (_preflight is not null)
            await _preflight(_cts.Token).ConfigureAwait(false);

        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);

        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        try
        {
            _cts?.Cancel();
        }
        catch { /* ignore */ }

        try
        {
            if (_loop is not null)
                await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            Log($"Stop error: {ex.Message}");
        }

        IsRunning = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Log("Worker loop started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await _processor.ProcessOnceAsync(Log, ct).ConfigureAwait(false);

                // If we processed a batch, loop again quickly to drain. Otherwise sleep.
                var delaySeconds = processed > 0 ? 1 : Math.Max(1, _options.PollIntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
                break;
            }
            catch (Exception ex)
            {
                Log("Loop error: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)), ct).ConfigureAwait(false);
            }
        }

        Log("Worker loop stopped.");
    }

    private void Log(string line) => LogLine?.Invoke(this, line);

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}
