using GHelper.Daemon.Contract;
using GHelper.Daemon.Core;

namespace GHelper.Daemon.DBus;

/// <summary>
/// Bounds protocol calls whose underlying Tmds task cannot accept a
/// CancellationToken. A cancelled waiter returns promptly, but its slot is
/// retained until the protocol task completes and its result is observed.
/// </summary>
public sealed class BoundedCallRunner
{
    private readonly SemaphoreSlim _slots;

    public BoundedCallRunner(int maximumInFlight)
    {
        if (maximumInFlight is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(maximumInFlight));
        _slots = new SemaphoreSlim(maximumInFlight, maximumInFlight);
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> startCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startCall);
        bool entered = await _slots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (!entered)
            throw new DaemonRequestException(
                DaemonContract.ErrorBusy,
                "The bounded D-Bus transport is saturated.");

        Task<T> call;
        try
        {
            call = startCall() ?? throw new InvalidOperationException("Transport returned no task.");
        }
        catch
        {
            _slots.Release();
            throw;
        }

        try
        {
            T result = await call.WaitAsync(cancellationToken).ConfigureAwait(false);
            _slots.Release();
            return result;
        }
        catch (OperationCanceledException)
        {
            _ = ObserveAndReleaseAsync(call);
            throw;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public async Task RunAsync(Func<Task> startCall, CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            await startCall().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveAndReleaseAsync<T>(Task<T> call)
    {
        try
        {
            await call.ConfigureAwait(false);
        }
        catch
        {
            // Observation is deliberate; public callers receive stable errors.
        }
        finally
        {
            _slots.Release();
        }
    }
}
