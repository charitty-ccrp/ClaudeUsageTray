using ClaudeUsageTray.Models;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

/// <summary>
/// Owns the refresh loop and the UsageState the UI renders. Requests never overlap (single loop),
/// a failed refresh keeps the last good value as stale, and retries follow BackoffPolicy.
/// </summary>
public sealed class UsageService : IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly Func<TimeSpan> _interval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, TimeSpan> _jitter;
    private readonly SynchronizationContext? _ui;
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Transient failures stay quiet (last value shown normally) for this many attempts...</summary>
    public const int QuietFailureLimit = 3;

    /// <summary>...or for this long, whichever comes first.</summary>
    public static readonly TimeSpan QuietFailureWindow = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private UsageState _state = UsageState.Initial;
    private bool _refreshing;
    private volatile bool _paused;
    private int _failures;
    private DateTimeOffset? _failingSince;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private string? _planName;
    private DateTimeOffset _lastPlanAttempt = DateTimeOffset.MinValue;

    /// <param name="jitter">Spreads automatic delays; defaults to BackoffPolicy.Jitter. Tests pass identity.</param>
    public UsageService(IUsageProvider provider, Func<TimeSpan> interval, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, TimeSpan>? jitter = null)
    {
        _provider = provider;
        _interval = interval;
        _jitter = jitter ?? BackoffPolicy.Jitter;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ui = SynchronizationContext.Current; // constructed on the UI thread so events are marshalled back
    }

    public event Action<UsageState>? StateChanged;

    /// <summary>Raised (on the UI thread when constructed there) when a refresh starts or ends.</summary>
    public event Action<bool>? RefreshingChanged;

    public UsageState State => Volatile.Read(ref _state);

    public bool IsRefreshing => Volatile.Read(ref _refreshing);

    /// <summary>Recent samples per window, used for the "could run out by" estimate.</summary>
    public BurnRateTracker BurnRates { get; } = new();

    /// <summary>Delay chosen after the most recent refresh; null means waiting for the user.</summary>
    public TimeSpan? LastDelay { get; private set; }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token));
    }

    /// <summary>Wake the loop now (manual refresh, resume from sleep, credential changed). Thread-safe, coalescing.</summary>
    public void RequestRefresh()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a refresh is already pending */ }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// While paused (workstation locked) the loop makes no automatic requests; a manual refresh still works.
    /// Resuming triggers one refresh so the value is current when the user comes back.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (!value) RequestRefresh();
        }
    }

    /// <summary>Called about once a second by the UI. Triggers one refresh when the reset time passes; never assumes 0%.</summary>
    public void Tick()
    {
        if (_paused) return;
        var state = State;
        if (state.Error is UsageErrorKind.Authentication or UsageErrorKind.NotConfigured) return;

        var reset = state.Snapshot?.FiveHour.ResetsAt;
        var now = _clock();
        if (reset is not null && reset <= now && _lastAttempt < reset)
            RequestRefresh();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
                if (LastDelay is { } delay && !_paused)
                    await _wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                else
                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One fetch + state update. Public so tests can drive the state machine without timers.</summary>
    public async Task<UsageState> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        SetRefreshing(true);
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SetRefreshing(false);
        }
    }

    private async Task<UsageState> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var now = _clock();
        _lastAttempt = now;

        UsageFetchResult result;
        try
        {
            result = await _provider.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Unexpected refresh error: {ex.GetType().Name}: {ex.Message}");
            result = UsageFetchResult.Fail(UsageErrorKind.InvalidResponse, "Unexpected error.");
        }

        UsageState next;
        if (result.IsSuccess)
        {
            _failures = 0;
            _failingSince = null;
            await EnsurePlanNameAsync(now, cancellationToken).ConfigureAwait(false);
            var snapshot = new UsageSnapshot(result.Usage!, now, _planName);
            foreach (var window in snapshot.Windows)
                BurnRates.Add(window.Key, now, window.Entry); // before Publish so listeners see the new sample
            next = new UsageState(snapshot, false, UsageErrorKind.None, null, now, null);
            Logger.Info($"Usage received: {UsageMath.DisplayUtilization(snapshot.FiveHour.Utilization)}%");
        }
        else
        {
            _failures++;
            _failingSince ??= now;
            var previous = State;
            var quiet = previous.Snapshot is not null && BackoffPolicy.IsTransient(result.Error)
                && _failures < QuietFailureLimit && now - _failingSince.Value < QuietFailureWindow;
            next = new UsageState(previous.Snapshot, previous.Snapshot is not null, result.Error,
                result.Message, previous.LastSuccessfulUpdate, null) { IsQuietFailure = quiet };
            Logger.Warn($"Refresh failed ({_failures} in a row): {result.Error} - {result.Message}");
        }

        LastDelay = BackoffPolicy.NextDelay(next.Error, _failures, _interval(), result.RetryAfter) is { } d0 ? _jitter(d0) : null;
        next = next with { NextAttemptAt = LastDelay is { } d ? now + d : null };
        Publish(next);
        return next;
    }

    private async Task EnsurePlanNameAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_planName is not null || now - _lastPlanAttempt < TimeSpan.FromMinutes(10)) return;
        _lastPlanAttempt = now;
        try
        {
            _planName = await _provider.GetPlanNameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Plan name lookup failed: {ex.GetType().Name}");
        }
    }

    private void Publish(UsageState state)
    {
        Volatile.Write(ref _state, state);
        var handler = StateChanged;
        if (handler is null) return;
        Raise(() => handler(state));
    }

    private void SetRefreshing(bool value)
    {
        Volatile.Write(ref _refreshing, value);
        var handler = RefreshingChanged;
        if (handler is null) return;
        Raise(() => handler(value));
    }

    private void Raise(Action action)
    {
        if (_ui is not null) _ui.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts?.Dispose();
        _wake.Dispose();
    }
}
