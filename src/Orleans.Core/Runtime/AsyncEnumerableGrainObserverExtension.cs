using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime;

/// <summary>
/// Observer-side support for returning <see cref="IAsyncEnumerable{T}"/> from observer methods.
/// </summary>
internal sealed partial class AsyncEnumerableGrainObserverExtension : IAsyncEnumerableGrainExtension, IAsyncDisposable
{
#if NET10_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    private readonly Dictionary<Guid, EnumeratorState> _enumerators = [];
    private readonly HashSet<Task> _activeDisposals = [];
    private readonly IGrainContext _grainContext;
    private readonly MessagingOptions _messagingOptions;

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
        _cleanupTask = RunCleanupLoopAsync(_shutdownCts.Token);
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_messagingOptions.ResponseTimeout);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    List<(Guid RequestId, EnumeratorState State)>? expired = null;
                    List<DisposalWork>? workItems = null;
                    List<Task>? disposalTasks = null;

                    lock (_lock)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        foreach (var (requestId, state) in _enumerators)
                        {
                            if (!state.TryEnterGate())
                            {
                                continue; // We skip those that currently have an in-flight MoveNext call.
                            }

                            try
                            {
                                if (state.Seen)
                                {
                                    state.Seen = false;
                                }
                                else
                                {
                                    expired ??= [];
                                    expired.Add((requestId, state));
                                }
                            }
                            finally
                            {
                                state.ExitGate();
                            }
                        }

                        if (expired is not null)
                        {
                            foreach (var (requestId, state) in expired)
                            {
                                if (_enumerators.TryGetValue(requestId, out var current) && ReferenceEquals(current, state))
                                {
                                    _enumerators.Remove(requestId);
                                }

                                var task = GetOrCreateDisposalTask(state, cancel: true, out var work);

                                (disposalTasks ??= []).Add(task);

                                if (work is { } item)
                                {
                                    (workItems ??= []).Add(item);
                                }
                            }
                        }
                    }

                    if (workItems is not null)
                    {
                        foreach (var work in workItems)
                        {
                            StartDisposal(work);
                        }
                    }

                    if (disposalTasks is { Count: > 0 })
                    {
                        await Task.WhenAll(disposalTasks).ConfigureAwait(false);
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
            // Ignore; we are shutting down.
        }
        catch (Exception exception)
        {
            LogWarningErrorCleaningUpEnumerators(exception);
        }
    }

    public async ValueTask<(EnumerationResult Status, object? Value)> StartEnumeration<T>(
        Guid requestId, IAsyncEnumerableRequest<T> request, CancellationToken cancellationToken)
    {
        if (request.MaxBatchSize <= 0)
        {
            return (EnumerationResult.Error, new ArgumentOutOfRangeException(nameof(request), "MaxBatchSize must be positive."));
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(request.GetCancellationToken());

        var state = new EnumeratorState
        {
            Seen = true,
            CancellationTokenSource = cts,
            MaxBatchSize = request.MaxBatchSize
        };

        // We enter the gate to prevent a concurrent Dispose/MoveNext from using a partially inited enumerator.
        await state.WaitToEnterGateAsync(CancellationToken.None).ConfigureAwait(false);

        Exception? exception = null;

        lock (_lock)
        {
            if (_disposed)
            {
                exception = new ObjectDisposedException(nameof(AsyncEnumerableGrainObserverExtension));
            }
            else if (!_enumerators.TryAdd(requestId, state))
            {
                exception = new InvalidOperationException("An enumerator with the same id already exists.");
            }
        }

        if (exception is not null)
        {
            state.ExitGate();
            cts.Dispose();

            return (EnumerationResult.Error, exception);
        }

        try
        {
            request.SetTarget(_grainContext);

            var enumerable = request.InvokeImplementation();

            state.Enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        }
        catch (Exception ex)
        {
            // Remove and claim cleanup while still holding the gate. The cleanup worker will
            // cancel the token and wait for this initialization path to release the gate.

            var disposalTask = RequestDisposal(requestId, state, cancel: true);

            state.ExitGate();

            await AwaitDisposalWithTimeoutAsync(disposalTask).ConfigureAwait(false);

            return (EnumerationResult.Error, ex);
        }

        state.ExitGate();

        return await MoveNextCoreAsync<T>(requestId, state, cancellationToken).ConfigureAwait(false);
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

        return MoveNextCoreAsync<T>(requestId, state, cancellationToken);
    }

    private async ValueTask<(EnumerationResult Status, object? Value)> MoveNextCoreAsync<T>(
        Guid requestId, EnumeratorState state, CancellationToken cancellationToken)
    {
        Debug.Assert(state.MaxBatchSize > 0, "Max batch size must be positive.");

        try
        {
            await state.WaitToEnterGateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var disposalTask = RequestDisposal(requestId, state, cancel: true);

            await AwaitDisposalWithTimeoutAsync(disposalTask).ConfigureAwait(false);

            return (EnumerationResult.Canceled, default);
        }

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
            state.ExitGate();
        }

        // We dispose the enumerator *outside* the gate, since DisposeEnumeratorAsync
        // acquires the gate itself, so we must have released it by now.
        if (result.Status.IsTerminated())
        {
            var cancel = result.Status is EnumerationResult.Error or EnumerationResult.Canceled or EnumerationResult.MissingEnumeratorError;
            var disposalTask = RequestDisposal(requestId, state, cancel);

            await AwaitDisposalWithTimeoutAsync(disposalTask).ConfigureAwait(false);
        }

        return result;

        async ValueTask<(EnumerationResult Status, object? Value)> DoMoveNextAsync()
        {
            lock (_lock)
            {
                if (!_enumerators.TryGetValue(requestId, out var current) || !ReferenceEquals(current, state))
                {
                    // Periodic cleanup or an explicit dispose removed us while we waited on the gate.
                    // We report the enumerator as terminated; the owner of the disposal task cleans it up.
                    return (EnumerationResult.MissingEnumeratorError, default);
                }
            }

            if (state.Enumerator is not IAsyncEnumerator<T> typedEnumerator)
            {
                return (EnumerationResult.Error, new InvalidCastException("Attempted to access an enumerator of the wrong type."));
            }

            state.Seen = true;

            var currentTask = state.MoveNextTask;

            if (currentTask is null)
            {
                var currentBatchSize = 0;
                object? batchResult = null;

                do
                {
                    // Check if the enumerator has a result ready synchronously.
                    var moveNext = typedEnumerator.MoveNextAsync();
                    if (moveNext.IsCompletedSuccessfully)
                    {
                        var hasValue = moveNext.Result;
                        if (hasValue)
                        {
                            // Account for the just-emitted element.
                            ++currentBatchSize;
                            var value = typedEnumerator.Current;
                            if (currentBatchSize == 1)
                            {
                                batchResult = value;
                            }
                            else if (currentBatchSize == 2)
                            {
                                // Grow from a single element to a list.
                                batchResult = new List<T> { (T)batchResult!, value };
                            }
                            else
                            {
                                ((List<T>)batchResult!).Add(value);
                            }
                        }
                        else
                        {
                            // Completed successfully, possibly with some final elements.
                            if (currentBatchSize == 0)
                            {
                                return (EnumerationResult.Completed, default);
                            }
                            else if (currentBatchSize == 1)
                            {
                                return (EnumerationResult.CompletedWithElement, batchResult);
                            }

                            return (EnumerationResult.CompletedWithBatch, batchResult);
                        }
                    }
                    else
                    {
                        // The enumerator did not complete synchronously, so we need to await the result for subsequent elements.
                        state.MoveNextTask = moveNext.AsTask();
                        break;
                    }
                } while (currentBatchSize < state.MaxBatchSize);

                // If there are elements, return them now instead of waiting for the pending operation to complete.
                if (currentBatchSize == 1)
                {
                    return (EnumerationResult.Element, batchResult!);
                }
                else if (currentBatchSize > 1)
                {
                    return (EnumerationResult.Batch, batchResult!);
                }

                // There are no elements, so wait for the pending operation to complete.
                currentTask = state.MoveNextTask!;
            }

            var longPollTimeout = _messagingOptions.ConfiguredResponseTimeout / 2;
            var (result, moveNextValue) = await AwaitMoveNextAsync(typedEnumerator, currentTask, longPollTimeout, cancellationToken).ConfigureAwait(false);

            if (currentTask.IsCompleted)
            {
                state.MoveNextTask = null;
            }

            return (result, moveNextValue);
        }

        static async ValueTask<(EnumerationResult Status, object? Value)> AwaitMoveNextAsync(
            IAsyncEnumerator<T> enumerator, Task<bool> moveNextTask, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            cts.CancelAfter(timeout);

            try
            {
                await moveNextTask.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Regardless of the task's state, we surface cancellation as the caller is the one who cancelled it.
                return (EnumerationResult.Canceled, null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
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
                // Ignore; the task faulted! We will classify the error from the task itself.
            }

            Debug.Assert(moveNextTask.IsCompleted);

            if (moveNextTask.IsCompletedSuccessfully)
            {
#pragma warning disable CA1849 // It won't be faulted if we are here!
                return moveNextTask.Result ? (EnumerationResult.Element, enumerator.Current) : (EnumerationResult.Completed, null);
#pragma warning restore CA1849
            }

            if (moveNextTask.IsCanceled)
            {
                return (EnumerationResult.Canceled, null);
            }

            Debug.Assert(moveNextTask.IsFaulted);
            Debug.Assert(moveNextTask.Exception is not null);

            var exception = moveNextTask.Exception!;

            return (EnumerationResult.Error, exception.InnerExceptions.Count == 1 ? exception.InnerException! : exception);
        }
    }

    ValueTask IAsyncEnumerableGrainExtension.DisposeAsync(Guid requestId) =>
        ((IAsyncEnumerableGrainExtension)this).DisposeAsync(requestId, CancellationToken.None);

    async ValueTask IAsyncEnumerableGrainExtension.DisposeAsync(Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var disposalTask = RequestDisposal(requestId, expectedState: null, cancel: true);

        await AwaitDisposalWithTimeoutAsync(disposalTask).ConfigureAwait(false);
    }

    private Task RequestDisposal(Guid requestId, EnumeratorState? expectedState, bool cancel)
    {
        DisposalWork? disposalWork;
        var disposalTask = Task.CompletedTask;

        lock (_lock)
        {
            EnumeratorState state;

            if (expectedState is null)
            {
                if (!_enumerators.Remove(requestId, out var current))
                {
                    return disposalTask;
                }

                state = current;
            }
            else
            {
                state = expectedState;

                if (_enumerators.TryGetValue(requestId, out var current) && ReferenceEquals(current, state))
                {
                    _enumerators.Remove(requestId);
                }
            }

            disposalTask = GetOrCreateDisposalTask(state, cancel, out disposalWork);
        }

        if (disposalWork is { } work)
        {
            StartDisposal(work);
        }

        return disposalTask;
    }

    private Task GetOrCreateDisposalTask(EnumeratorState state, bool cancel, out DisposalWork? work)
    {
        if (state.DisposalTask is { } existingTask)
        {
            work = null;
            return existingTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        state.DisposalTask = completion.Task;

        _activeDisposals.Add(completion.Task);

        work = new DisposalWork(state, cancel, completion);

        return completion.Task;
    }

    private void StartDisposal(DisposalWork work)
    {
        RunDisposalAsync().Ignore();

        async Task RunDisposalAsync()
        {
            var state = work.State;
            var gateEntered = false;
            var cancellationTask = Task.CompletedTask;

            try
            {
                // We signal cancellation *before* waiting for the gate. An in-flight MoveNext may be
                // waiting for the producer, and cancellation is what gives it a chance to finish, because
                // it will notice the cancellation and fault thereby releasing the gate itself.
                if (work.Cancel)
                {
                    try
                    {
                        cancellationTask = state.CancellationTokenSource.CancelAsync();
                    }
                    catch (Exception exception)
                    {
                        LogWarningErrorCancelingEnumerator(exception);
                    }
                }

                // The above ensures that we wont get stuck waiting here for the full ResponseTimeout duration.
                await state.WaitToEnterGateAsync(CancellationToken.None).ConfigureAwait(false);

                gateEntered = true;

                if (!state.Disposed) // We guard because multiple StartDisposal() calls can be enqued.
                {
                    state.Disposed = true;

                    // DisposeAsync must not overlap a running MoveNextAsync, so we wait until a running MoveNextAsync completes fully.
                    if (state.MoveNextTask is { } pendingMoveNext)
                    {
                        await WaitUntilCompletedAsync(pendingMoveNext).ConfigureAwait(false);
                    }

                    // Then we finalize cancellation
                    await WaitUntilCompletedAsync(cancellationTask).ConfigureAwait(false);

                    if (state.Enumerator is { } enumerator)
                    {
                        try
                        {
                            var enumeratorDisposeTask = enumerator.DisposeAsync().AsTask();
                            await WaitUntilCompletedAsync(enumeratorDisposeTask).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            LogWarningErrorDisposingEnumerator(exception);
                        }
                    }

                    state.CancellationTokenSource.Dispose();
                }
            }
            catch (Exception exception)
            {
                // Teardown is best-effort since the per-enumerator gate is never disposed,
                // since a stale request may already be waiting to acquire it.
                LogWarningErrorDisposingEnumerator(exception);
            }
            finally
            {
                if (gateEntered)
                {
                    state.ExitGate();
                }

                lock (_lock)
                {
                    _activeDisposals.Remove(work.Completion.Task);
                }

                work.Completion.TrySetResult(true);
            }
        }
    }

    private async Task WaitUntilCompletedAsync(Task task)
    {
        while (!task.IsCompleted)
        {
            try
            {
                await task.WaitAsync(_messagingOptions.ResponseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogWarningErrorDisposingEnumerator(new TimeoutException($"Timed out waiting for disposal."));
            }
            catch
            {
                // The task faulted or was canceled, we observe its final
                // outcome below but break out of the loop!
                break;
            }
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWarningErrorDisposingEnumerator(ex);
        }
    }

    private async Task AwaitDisposalWithTimeoutAsync(Task disposalTask)
    {
        try
        {
            await disposalTask.WaitAsync(_messagingOptions.ResponseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The actual disposal task remains alive and retains responsibility for cleanup.
            LogWarningErrorDisposingEnumerator(new TimeoutException("Timed out waiting for observer async enumerator disposal."));
        }
        catch (Exception exception)
        {
            LogWarningErrorDisposingEnumerator(exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task[] disposalTasks;
        List<DisposalWork> workItems = [];
        TaskCompletionSource<bool> completion;

        lock (_lock)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;

            foreach (var state in _enumerators.Values)
            {
                GetOrCreateDisposalTask(state, cancel: true, out var work);

                if (work is { } item)
                {
                    workItems.Add(item);
                }
            }

            _enumerators.Clear();

            disposalTasks = [.. _activeDisposals];
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _disposeTask = completion.Task;
        }

        CompleteDisposeAsync().Ignore();

        return new ValueTask(completion.Task);

        async Task CompleteDisposeAsync()
        {
            foreach (var work in workItems)
            {
                StartDisposal(work);
            }

            try
            {
                await _shutdownCts.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogWarningErrorCleaningUpEnumerators(exception);
            }

            try
            {
                await _cleanupTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogWarningErrorCleaningUpEnumerators(exception);
            }

            try
            {
                await Task.WhenAll(disposalTasks).WaitAsync(_messagingOptions.ResponseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Individual disposal workers remain alive and continue waiting for in-flight producer operations.
                LogWarningErrorDisposingEnumerator(new TimeoutException("Timed out waiting for all observer async enumerators to dispose."));
            }
            catch (Exception exception)
            {
                LogWarningErrorDisposingEnumerator(exception);
            }
            finally
            {
                try { _shutdownCts.Dispose(); } catch { }
                completion.TrySetResult(true);
            }
        }
    }

    private readonly record struct DisposalWork(EnumeratorState State, bool Cancel, TaskCompletionSource<bool> Completion);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "Its for the SempahoreSlim, but we arent using a WaitHandle where disposal would be required," +
                        "and the disposal can race with in-flight operations using the semaphore. We let the GC take care of it.")]
    private sealed class EnumeratorState
    {
        public required int MaxBatchSize { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; init; }

        // These fields are accessed while Gate is held, except DisposalTask which is protected by _lock.
        public IAsyncDisposable? Enumerator { get; set; }
        public bool Seen { get; set; }
        public bool Disposed { get; set; }
        public Task<bool>? MoveNextTask { get; set; }
        public Task? DisposalTask { get; set; }

        private readonly SemaphoreSlim _gate = new(1, 1);

        public bool TryEnterGate() => _gate.Wait(0);
        public Task WaitToEnterGateAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);
        public void ExitGate() => _gate.Release();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error canceling observer async enumerator.")]
    private partial void LogWarningErrorCancelingEnumerator(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error disposing observer async enumerator.")]
    private partial void LogWarningErrorDisposingEnumerator(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error cleaning up observer async enumerators.")]
    private partial void LogWarningErrorCleaningUpEnumerators(Exception exception);
}
