using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime;

/// <summary>
/// Observer-side support for returning <see cref="IAsyncEnumerable{T}"/> from observer methods.
/// </summary>
internal sealed partial class AsyncEnumerableGrainObserverExtension : IAsyncEnumerableGrainExtension, IAsyncDisposable, IDisposable
{
#if NET10_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    private readonly Dictionary<Guid, EnumeratorState> _enumerators = [];
    private readonly IGrainContext _grainContext;
    private readonly MessagingOptions _messagingOptions;
    private readonly TimeSpan _cleanupPeriod;
    private readonly TimeSpan _longPollTimeout;
    private readonly TimeSpan _disposeTimeout;

    private readonly Task _cleanupTask;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ILogger<AsyncEnumerableGrainObserverExtension> _logger;

    private bool _disposed;
    private Task? _disposeTask;

    public AsyncEnumerableGrainObserverExtension(
        IGrainContext grainContext,
        IOptions<MessagingOptions> messagingOptions,
        ILogger<AsyncEnumerableGrainObserverExtension> logger)
    {
        _logger = logger;
        _grainContext = grainContext;
        _messagingOptions = messagingOptions.Value;

        _cleanupPeriod = _messagingOptions.ResponseTimeout;
        _disposeTimeout = _messagingOptions.ResponseTimeout;
        _longPollTimeout = _messagingOptions.ConfiguredResponseTimeout / 2;

        _cleanupTask = RunCleanupLoopAsync(_shutdownCts.Token);
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_cleanupPeriod);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    List<EnumeratorState>? expired = null;

                    lock (_lock)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        foreach (var (requestId, state) in _enumerators)
                        {
                            if (!state.Gate.Wait(0, CancellationToken.None))
                            {
                                // We skip entries that currently have a MoveNext in-flight.
                                continue;
                            }

                            try
                            {
                                if (state.Seen)
                                {
                                    state.Seen = false;
                                }
                                else if (_enumerators.Remove(requestId))
                                {
                                    (expired ??= []).Add(state);
                                }
                            }
                            finally
                            {
                                state.Gate.Release();
                            }
                        }
                    }

                    if (expired is not null)
                    {
                        foreach (var state in expired)
                        {
                            await DisposeEnumeratorAsync(state).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception exception)
                {
                    LogWarningErrorCleaningUpEnumerators(exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore we are shutting down
        }
        catch (Exception exception)
        {
            LogWarningErrorCleaningUpEnumerators(exception);
        }
    }

    public ValueTask<(EnumerationResult Status, object? Value)> StartEnumeration<T>(
        Guid requestId, IAsyncEnumerableRequest<T> request, CancellationToken cancellationToken)
    {
        if (request.MaxBatchSize <= 0)
        {
            return ValueTask.FromResult<(EnumerationResult, object?)>((EnumerationResult.Error, new ArgumentOutOfRangeException(nameof(request), "MaxBatchSize must be positive.")));
        }

        request.SetTarget(_grainContext);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(request.GetCancellationToken());
        var enumerable = request.InvokeImplementation();
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);

        var state = new EnumeratorState
        {
            Seen = true,
            Enumerator = enumerator,
            CancellationTokenSource = cts,
            MaxBatchSize = request.MaxBatchSize
        };

        lock (_lock)
        {
            if (_disposed)
            {
                CleanUp(cts, enumerator);
                return ValueTask.FromResult<(EnumerationResult, object?)>((EnumerationResult.Error, new ObjectDisposedException(nameof(AsyncEnumerableGrainObserverExtension))));
            }

            if (!_enumerators.TryAdd(requestId, state))
            {
                CleanUp(cts, enumerator);
                return ValueTask.FromResult<(EnumerationResult, object?)>((EnumerationResult.Error, new InvalidOperationException("An enumerator with the same id already exists.")));
            }
        }

        return MoveNextCoreAsync(requestId, state, enumerator, cancellationToken);

        static void CleanUp(CancellationTokenSource cts, IAsyncEnumerator<T> enumerator)
        {
            cts.Cancel();
            cts.Dispose();
            enumerator.DisposeAsync().AsTask().Ignore();
        }
    }

    public ValueTask<(EnumerationResult Status, object? Value)> MoveNext<T>(Guid requestId, CancellationToken cancellationToken)
    {
        EnumeratorState? state;

        lock (_lock)
        {
            if (!_enumerators.TryGetValue(requestId, out state))
            {
                return ValueTask.FromResult<(EnumerationResult, object?)>((EnumerationResult.MissingEnumeratorError, default));
            }
        }

        return state.Enumerator is IAsyncEnumerator<T> typedEnumerator ?
            MoveNextCoreAsync(requestId, state, typedEnumerator, cancellationToken) :
            ValueTask.FromResult<(EnumerationResult, object?)>((EnumerationResult.Error, new InvalidCastException("Attempted to access an enumerator of the wrong type.")));
    }

    private async ValueTask<(EnumerationResult Status, object? Value)> MoveNextCoreAsync<T>(
        Guid requestId, EnumeratorState state, IAsyncEnumerator<T> typedEnumerator, CancellationToken cancellationToken)
    {
        // We gate via the semaphore so there can only be one MoveNext at a time per enumerator.
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_lock)
            {
                // Cleanup or an explicit dispose may have removed us while we waited on the gate.
                // So we re-check under the lock before touching the state, otherwise we could run
                // MoveNext on an enumerator that's already being torn down.

                if (!_enumerators.TryGetValue(requestId, out var current) || !ReferenceEquals(current, state))
                {
                    return (EnumerationResult.MissingEnumeratorError, default);
                }
            }

            state.Seen = true;

            var pendingTask = state.MoveNextTask;

            if (pendingTask is null)
            {
                var result = AsyncEnumeratorHelper.FillBatchSync(typedEnumerator, state.MaxBatchSize);

                if (result.Status is EnumerationResult.Error)
                {
                    return await TerminateAsync(requestId, state, EnumerationResult.Error, result.Value).ConfigureAwait(false);
                }

                if (result.PendingTask is { } pending)
                {
                    state.MoveNextTask = pending;
                }

                if (result.Status is EnumerationResult.Element or EnumerationResult.Batch)
                {
                    // It means we producd elements *synchronously*, so we return them now.
                    return (result.Status, result.Value);
                }

                if (result.Status.IsTerminated())
                {
                    return await TerminateAsync(requestId, state, result.Status, result.Value).ConfigureAwait(false);
                }

                Debug.Assert(result.Status is EnumerationResult.Heartbeat);

                pendingTask = state.MoveNextTask!;
            }

            var (status, value) = await AsyncEnumeratorHelper
                .AwaitPendingMoveNextAsync(typedEnumerator, pendingTask, _longPollTimeout, cancellationToken)
                .ConfigureAwait(false);

            // If the pending task finished, we drop it so the next call starts a fresh batch.
            if (pendingTask.IsCompleted)
            {
                state.MoveNextTask = null;
            }

            if (status.IsTerminated())
            {
                return await TerminateAsync(requestId, state, status, value).ConfigureAwait(false);
            }

            return (status, value);
        }
        catch (Exception exception)
        {
            return await TerminateAsync(requestId, state, EnumerationResult.Error, exception).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async ValueTask<(EnumerationResult Status, object? Value)> TerminateAsync(
        Guid requestId, EnumeratorState state, EnumerationResult status, object? value)
    {
        lock (_lock)
        {
            _enumerators.Remove(requestId);
        }

        await DisposeEnumeratorBodyAsync(state).ConfigureAwait(false);

        return (status, value);
    }

    ValueTask IAsyncEnumerableGrainExtension.DisposeAsync(Guid requestId) =>
        ((IAsyncEnumerableGrainExtension)this).DisposeAsync(requestId, CancellationToken.None);

    async ValueTask IAsyncEnumerableGrainExtension.DisposeAsync(Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EnumeratorState? state;

        lock (_lock)
        {
            if (!_enumerators.Remove(requestId, out state))
            {
                return;
            }
        }

        await DisposeEnumeratorAsync(state).ConfigureAwait(false);
    }

    void IDisposable.Dispose() => ((IAsyncDisposable)this).DisposeAsync().AsTask().Ignore();

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposeTask is not null)
            {
                return new(_disposeTask);
            }

            _disposed = true;

            List<EnumeratorState> enumerators;

            if (_enumerators.Count == 0)
            {
                enumerators = [];
            }
            else
            {
                enumerators = [.. _enumerators.Values];
                _enumerators.Clear();
            }

            _disposeTask = DisposeCoreAsync(enumerators);

            return new(_disposeTask);
        }

        async Task DisposeCoreAsync(List<EnumeratorState> enumerators)
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            await _cleanupTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            _shutdownCts.Dispose();

            foreach (var state in enumerators)
            {
                await DisposeEnumeratorAsync(state).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask DisposeEnumeratorAsync(EnumeratorState state)
    {
        // We cancel first so any in-flight MoveNext unblocks promptly. Safe without the gate.
        try
        {
            await state.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningErrorCancelingEnumerator(exception);
        }

        var gateAcquired = false;

        try
        {
            using var timeoutCts = new CancellationTokenSource(_disposeTimeout);

            try
            {
                await state.Gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                gateAcquired = true;
            }
            catch (OperationCanceledException)
            {
                return; // In-flight MoveNext handles disposal itself.
            }
            catch (Exception exception)
            {
                LogWarningErrorDisposingEnumerator(exception);
                return;
            }

            // We got the gate acquired so we can dispose the enumerator ourselves.
            await DisposeEnumeratorBodyAsync(state).ConfigureAwait(false);
        }
        finally
        {
            if (gateAcquired)
            {
                state.Gate.Release();
            }
        }
    }

    private async ValueTask DisposeEnumeratorBodyAsync(EnumeratorState state)
    {
        if (state.Disposed)
        {
            return;
        }

        state.Disposed = true;

        try
        {
            await state.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningErrorCancelingEnumerator(exception);
        }

        if (state.MoveNextTask is { IsCompleted: false } pending)
        {
            try
            {
                await pending.WaitAsync(_disposeTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogWarningErrorDisposingEnumerator(exception);
            }
        }

        try
        {
            await state.Enumerator.DisposeAsync().AsTask().WaitAsync(_disposeTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningErrorDisposingEnumerator(exception);
        }
        finally
        {
            state.CancellationTokenSource.Dispose();
            // NOTE: We do not release the semaphore as we may still be holding it (check the TerminateAsync path).
        }
    }

    private sealed class EnumeratorState
    {
        public required int MaxBatchSize { get; init; }
        public required IAsyncDisposable Enumerator { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; init; }

        public bool Seen { get; set; }
        public bool Disposed { get; set; }
        public Task<bool>? MoveNextTask { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error canceling observer async enumerator.")]
    private partial void LogWarningErrorCancelingEnumerator(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error disposing observer async enumerator.")]
    private partial void LogWarningErrorDisposingEnumerator(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error cleaning up observer async enumerators.")]
    private partial void LogWarningErrorCleaningUpEnumerators(Exception exception);
}
