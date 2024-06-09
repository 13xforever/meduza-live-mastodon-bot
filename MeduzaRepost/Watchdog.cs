namespace MeduzaRepost;

public class Watchdog: IObserver<TgEvent>, IDisposable
{
    private readonly Action onTimeout;
    private Task trigger;
    private CancellationTokenSource cts = new();
    private readonly object syncObj = new();

    public Watchdog(Action onTimeout)
    {
        this.onTimeout = onTimeout;
        var token = cts.Token;
        trigger = Task.Delay(Config.WatchdogThreshold, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                this.onTimeout();
        }, token);
    }

    public void Reset()
    {
        lock (syncObj)
        {
            cts.Cancel(false);
            if (!cts.TryReset())
            {
                cts.Dispose();
                cts = new();
            }
            trigger.Dispose();
            var token = cts.Token;
            trigger = Task.Delay(Config.WatchdogThreshold, token).ContinueWith(_ =>
            {
                if (!token.IsCancellationRequested)
                    onTimeout();
            }, token);
        }
    }

    public void Dispose()
    {
        cts.Cancel(false);
        trigger.Dispose();
        cts.Dispose();
    }

    public void OnCompleted() => Dispose();
    public void OnError(Exception error) {}
    public void OnNext(TgEvent value)
    {
        if (value.Type is TgEventType.Post or TgEventType.Edit or TgEventType.Delete or TgEventType.Pin)
        {
            Reset();
            Config.Log.Debug("Watchdog reset");
        }
    }
}