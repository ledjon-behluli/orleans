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
            new((EnumerationResult.Error, new InvalidCastException("Attempted to access an enumerator of the wrong type.")));
    }

    private async ValueTask<(EnumerationResult Status, object? Value)> MoveNextCoreAsync<T>(
        Guid requestId, EnumeratorState state, IAsyncEnumerator<T> typedEnumerator, CancellationToken cancellationToken)
    {
        Debug.Assert(state.MaxBatchSize > 0, "Max batch size must be positive.");

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
                    // Periodic cleanup or an explicit dispose removed us while we waited on the gate.
                    // We report the emurator as 'terminated' so the caller disposes it.
                    return (EnumerationResult.MissingEnumeratorError, default);
                }
            }

            state.Seen = true;

            var currentTask = state.MoveNextTask;

            if (currentTask is null)
            {
                var (fillResult, fillValue, pendingTask) = FillBatch(typedEnumerator, state.MaxBatchSize);

                if (pendingTask is { })
                {
                    state.MoveNextTask = pendingTask;
                }

                if (fillResult is EnumerationResult.Element or EnumerationResult.Batch)
                {
                    return (fillResult, fillValue);
                }

                if (fillResult.IsTerminated())
                {
                    return (fillResult, fillValue);
                }

                Debug.Assert(fillResult is EnumerationResult.Heartbeat);

                currentTask = state.MoveNextTask!;
            }

            var (result, value) = await AwaitPendingMoveNextAsync(typedEnumerator, currentTask, _longPollTimeout, cancellationToken).ConfigureAwait(false);

            if (currentTask.IsCompleted)
            {
                state.MoveNextTask = null;
            }

            return (result, value);
        }

        static (EnumerationResult Result, object? Value, Task<bool>? PendingTask) FillBatch(IAsyncEnumerator<T> enumerator, int maxBatchSize)
        {
            var count = 0;
            object? result = null;

            try
            {
                while (count < maxBatchSize)
                {
                    var moveNext = enumerator.MoveNextAsync();

                    // Check if the enumerator has a result ready synchronously.
                    if (!moveNext.IsCompletedSuccessfully)
                    {
                        return new(count switch
                        {
                            0 => EnumerationResult.Heartbeat,
                            1 => EnumerationResult.Element,
                            _ => EnumerationResult.Batch,
                        }, result, moveNext.AsTask());
                    }

                    if (!moveNext.Result)
                    {
                        return new(count switch
                        {
                            0 => EnumerationResult.Completed,
                            1 => EnumerationResult.CompletedWithElement,
                            _ => EnumerationResult.CompletedWithBatch,
                        }, result, null);
                    }

                    ++count; // Account for the just-emitted element.

                    var value = enumerator.Current;

                    switch (count)
                    {
                        case 1: result = value; break;
                        case 2: result = new List<T> { (T)result!, value }; break;
                        default: ((List<T>)result!).Add(value); break;
                    }
                }

                return new(count == 1 ? EnumerationResult.Element : EnumerationResult.Batch, result, null);
            }
            catch (Exception exception)
            {
                return new(EnumerationResult.Error, exception, null);
            }
        }

        static async ValueTask<(EnumerationResult Status, object? Value)> AwaitPendingMoveNextAsync(
            IAsyncEnumerator<T> enumerator, Task<bool> moveNextTask, TimeSpan longPollTimeout, CancellationToken callerToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

            timeoutCts.CancelAfter(longPollTimeout);

            try
            {
                await moveNextTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                // Regardless of the task's state, we surface cancellation as the caller is the one who cancelled it.
                return (EnumerationResult.Canceled, null);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // Long-poll elapsed, or the task itself was cancelled.
                // If the task is still in flight, we report a heartbeat; otherwise fall through and classify its outcome.
                if (!moveNextTask.IsCompleted)
                {
                    return (EnumerationResult.Heartbeat, null);
                }
            }
            catch
            {
                // Ignore, the task faulted! We will classify the error from the task itself.
            }

            Debug.Assert(moveNextTask.IsCompleted);

            if (moveNextTask.IsCompletedSuccessfully)
            {
#pragma warning disable CA1849 // It wont be faulted if we are here!
                return moveNextTask.Result ? (EnumerationResult.Element, enumerator.Current) : (EnumerationResult.Completed, null);
#pragma warning restore CA1849
            }

            if (moveNextTask.IsCanceled)
            {
                return (EnumerationResult.Canceled, null);
            }

            Debug.Assert(moveNextTask.IsFaulted);
            Debug.Assert(moveNextTask.Exception is not null);

            var exception = moveNextTask.Exception;
            return (EnumerationResult.Error, exception.InnerExceptions.Count == 1 ? exception.InnerException! : exception);
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
