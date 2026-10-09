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
using static System.Runtime.InteropServices.JavaScript.JSType;

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
                            if (!state.TryEnter())
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
                                state.Exit();
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
        await state.WaitToEnterAsync(cancellationToken).ConfigureAwait(false);

        (EnumerationResult Status, object? Value) result;

        try
        {
            result = await DoMoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = (EnumerationResult.Error, exception);
        }
        finally
        {
            state.Exit();
        }

        // We dispose the enumerator *outside* the gate, since DisposeEnumeratorBodyAsync
        // acquires the gate itself, so we must have released it by now.
        if (result.Status.IsTerminated())
        {
            lock (_lock)
            {
                _enumerators.Remove(requestId);
            }

            await DisposeEnumeratorAsync(state).ConfigureAwait(false);
        }

        return result;

        async ValueTask<(EnumerationResult Status, object? Value)> DoMoveNextAsync()
        {
            lock (_lock)
            {
                if (!_enumerators.TryGetValue(requestId, out var current) || !ReferenceEquals(current, state))
                {
                    // Cleanup or an explicit dispose removed us while we waited on the
                    // gate. Report it as terminated so the caller disposes the state
                    // (idempotent via state.Disposed).
                    return (EnumerationResult.MissingEnumeratorError, default);
                }
            }

            state.Seen = true;

            var currentTask = state.MoveNextTask;

            if (currentTask is null)
            {
                var (fillStatus, fillValue, pendingTask) = AsyncEnumeratorHelper.FillBatchSync(typedEnumerator, state.MaxBatchSize);

                if (pendingTask is { })
                {
                    state.MoveNextTask = pendingTask;
                }

                if (fillStatus is EnumerationResult.Element or EnumerationResult.Batch)
                {
                    return (fillStatus, fillValue);
                }

                if (!fillStatus.IsActive())
                {
                    // Error, Completed, CompletedWithElement, CompletedWithBatch.
                    return (fillStatus, fillValue);
                }

                Debug.Assert(fillStatus is EnumerationResult.Heartbeat);
                currentTask = state.MoveNextTask!;
            }

            var (status, value) = await AsyncEnumeratorHelper
                .AwaitPendingMoveNextAsync(typedEnumerator, currentTask, _longPollTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (currentTask.IsCompleted)
            {
                state.MoveNextTask = null;
            }

            return (status, value);
        }
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

    public void Dispose() => DisposeAsync().AsTask().Ignore();

    public ValueTask DisposeAsync()
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

            _disposeTask = DoDisposeAsync(enumerators);

            return new(_disposeTask);
        }

        async Task DoDisposeAsync(List<EnumeratorState> enumerators)
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            await _cleanupTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            try { _shutdownCts.Dispose(); } catch { }

            foreach (var state in enumerators)
            {
                await DisposeEnumeratorAsync(state).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask DisposeEnumeratorAsync(EnumeratorState state)
    {
        // In the Terminate path the state has already been removed
        // from _enumerators and the caller has released the gate, so this is uncontended.
        // But in the external DisposeAsync path we may briefly contend with an in-flight
        // MoveNext (bounded by _disposeTimeout), so we acquire the gate!
        using var timeoutCts = new CancellationTokenSource(_disposeTimeout);

        try
        {
            await state.WaitToEnterAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The in-flight MoveNext will observe that the state was removed from _enumerators and dispose.
            LogWarningErrorDisposingEnumerator(new TimeoutException("Timed out waiting to dispose the enumerator."));
            return;
        }

        try
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
            }
        }
        finally
        {
            state.Exit();
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

        public bool TryEnter() => Gate.Wait(0);
        public Task WaitToEnterAsync(CancellationToken cancellationToken) => Gate.WaitAsync(cancellationToken);
        public void Exit() => Gate.Release();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error canceling observer async enumerator.")]
    private partial void LogWarningErrorCancelingEnumerator(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error disposing observer async enumerator.")]
    private partial void LogWarningErrorDisposingEnumerator(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error cleaning up observer async enumerators.")]
    private partial void LogWarningErrorCleaningUpEnumerators(Exception exception);
}
