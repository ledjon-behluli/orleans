using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Timers;

namespace Orleans.Runtime;

/// <summary>
/// Grain-side support for returning <see cref="IAsyncEnumerable{T}"/> from grain methods.
/// </summary>
internal sealed partial class AsyncEnumerableGrainExtension : IAsyncEnumerableGrainExtension, IAsyncDisposable, IDisposable
{
    private static readonly DiagnosticListener DiagnosticListener = new("Orleans.Runtime.AsyncEnumerableGrainExtension");
    private readonly Dictionary<Guid, EnumeratorState> _enumerators = [];
    private readonly ILogger<AsyncEnumerableGrainExtension> _logger;
    private readonly MessagingOptions _messagingOptions;

    // Internal for testing
    internal IGrainTimer Timer { get; }
    internal IGrainContext GrainContext { get; }

    /// <summary>
    /// Initializes a new <see cref="AsyncEnumerableGrainExtension"/> instance.
    /// </summary>
    /// <param name="grainContext">The grain which this extension is attached to.</param>
    /// <param name="messagingOptions">The messaging options.</param>
    /// <param name="logger">The logger.</param>
    public AsyncEnumerableGrainExtension(
        IGrainContext grainContext,
        IOptions<MessagingOptions> messagingOptions,
        ILogger<AsyncEnumerableGrainExtension> logger)
    {
        _logger = logger;
        GrainContext = grainContext;

        _messagingOptions = messagingOptions.Value;
        var registry = GrainContext.GetComponent<ITimerRegistry>();
        var cleanupPeriod = messagingOptions.Value.ResponseTimeout;
        Timer = registry!.RegisterGrainTimer(
            GrainContext,
            static async (state, cancellationToken) => await state.RemoveExpiredAsync(cancellationToken),
            this,
            new()
            {
                DueTime = cleanupPeriod,
                Period = cleanupPeriod,
                Interleave = true,
                KeepAlive = false
            });
        OnAsyncEnumeratorGrainExtensionCreated(this);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync(Guid requestId) => DisposeAsync(requestId, CancellationToken.None);

    /// <inheritdoc/>
    public ValueTask DisposeAsync(Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RemoveEnumeratorAsync(requestId);
    }

    private async ValueTask RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        List<Guid>? toRemove = default;
        foreach (var (requestId, state) in _enumerators)
        {
            if (MarkAndCheck(requestId))
            {
                toRemove ??= [];
                toRemove.Add(requestId);
            }

            bool MarkAndCheck(Guid requestId)
            {
                ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
                if (Unsafe.IsNullRef(ref state))
                {
                    return false;
                }

                // Returns true if no flags were set.
                return state.ClearSeen();
            }
        }

        List<Task>? tasks = default;
        if (toRemove is not null)
        {
            foreach (var requestId in toRemove)
            {
                var removeTask = RemoveEnumeratorAsync(requestId);
                if (!removeTask.IsCompletedSuccessfully)
                {
                    tasks ??= [];
                    tasks.Add(removeTask.AsTask());
                }
            }
        }

        if (tasks is { Count: > 0 })
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }

        OnEnumeratorCleanupCompleted(this);
    }

    /// <inheritdoc/>
    public ValueTask<(EnumerationResult Status, object? Value)> StartEnumeration<T>(Guid requestId, [Immutable] IAsyncEnumerableRequest<T> request, CancellationToken cancellationToken)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_enumerators, requestId, out bool exists);
        if (exists)
        {
            return ThrowAlreadyExists();
        }

        request.SetTarget(GrainContext);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(request.GetCancellationToken());
        var enumerable = request.InvokeImplementation();
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        entry.Enumerator = enumerator;
        entry.MaxBatchSize = request.MaxBatchSize;
        entry.CancellationTokenSource = cts;
        Debug.Assert(entry.MaxBatchSize > 0, "Max batch size must be positive.");
        return MoveNextCore(ref entry, requestId, enumerator, cancellationToken);

        static ValueTask<(EnumerationResult Status, object? Value)> ThrowAlreadyExists() => ValueTask.FromException<(EnumerationResult Status, object? Value)>(new InvalidOperationException("An enumerator with the same id already exists."));
    }

    /// <inheritdoc/>
    public ValueTask<(EnumerationResult Status, object? Value)> MoveNext<T>(Guid requestId, CancellationToken cancellationToken)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
        if (Unsafe.IsNullRef(ref entry))
        {
            return new((EnumerationResult.MissingEnumeratorError, default!));
        }

        if (entry.Enumerator is not IAsyncEnumerator<T> typedEnumerator)
        {
            throw new InvalidCastException("Attempted to access an enumerator of the wrong type.");
        }

        return MoveNextCore(ref entry, requestId, typedEnumerator, cancellationToken);
    }

    private ValueTask<(EnumerationResult Status, object? Value)> MoveNextCore<T>(
        ref EnumeratorState entry,
        Guid requestId,
        IAsyncEnumerator<T> typedEnumerator,
        CancellationToken cancellationToken)
    {
        entry.SetSeen();

        try
        {
            if (entry.MoveNextTask is null)
            {
                var (status, value, pendingTask) = AsyncEnumeratorHelper.FillBatchSync(typedEnumerator, entry.MaxBatchSize);

                if (status is EnumerationResult.Error)
                {
                    return OnTerminateAsync(requestId, EnumerationResult.Error, value);
                }

                if (pendingTask is { } pending)
                {
                    entry.MoveNextTask = pending;
                }

                if (status is EnumerationResult.Element or EnumerationResult.Batch)
                {
                    return new((status, value));
                }

                if (status.IsTerminated())
                {
                    return OnTerminateAsync(requestId, status, value);
                }

                Debug.Assert(status is EnumerationResult.Heartbeat);
                // Fall through to await the pending task.
            }

            // Prevent the enumerator from being collected while we are enumerating it.
            entry.SetBusy();
            return AwaitMoveNextAsync(requestId, typedEnumerator, entry.MoveNextTask!, cancellationToken);
        }
        catch (Exception exception)
        {
            return OnTerminateAsync(requestId, EnumerationResult.Error, exception);
        }
    }

    private async ValueTask<(EnumerationResult Status, object? Value)> AwaitMoveNextAsync<T>(
        Guid requestId,
        IAsyncEnumerator<T> typedEnumerator,
        Task<bool> moveNextTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var longPollingTimeout = _messagingOptions.ConfiguredResponseTimeout / 2;

            var (result, value) = await AsyncEnumeratorHelper
                .AwaitPendingMoveNextAsync(typedEnumerator, moveNextTask, longPollingTimeout, cancellationToken)
                .ConfigureAwait(false);

            // Update the enumerator state to indicate that we are no longer waiting for MoveNextAsync to complete.
            // If the MoveNextAsync task completed then clear that now, too.
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
            if (!Unsafe.IsNullRef(ref state))
            {
                state.ClearBusy();
                if (moveNextTask.IsCompleted)
                {
                    state.MoveNextTask = null;
                }
            }

            if (result.IsTerminated())
            {
                await RemoveEnumeratorAsync(requestId);
            }

            return (result, value);
        }
        catch (Exception exception)
        {
            await RemoveEnumeratorAsync(requestId);
            return (EnumerationResult.Error, exception);
        }
    }

    private async ValueTask RemoveEnumeratorAsync(Guid requestId)
    {
        if (_enumerators.Remove(requestId, out var state))
        {
            await DisposeEnumeratorAsync(state);
        }
    }

    private async ValueTask<(EnumerationResult Status, object? Value)> OnTerminateAsync(Guid requestId, EnumerationResult status, object? value)
    {
        await RemoveEnumeratorAsync(requestId);
        return (status, value);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_enumerators.Count > 0)
        {
            var enumerators = new List<EnumeratorState>(_enumerators.Values);
            _enumerators.Clear();

            foreach (var enumerator in enumerators)
            {
                await DisposeEnumeratorAsync(enumerator);
            }
        }

        Timer.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1849:Call async methods when in an async method",
        Justification = "Cancellation callbacks must run synchronously on the current grain scheduler before awaiting MoveNext completion.")]
    private async ValueTask DisposeEnumeratorAsync(EnumeratorState enumerator)
    {
        try
        {
            enumerator.CancellationTokenSource!.Cancel();
        }
        catch (Exception exception)
        {
            LogWarningErrorCancellingEnumerator(exception);
        }

        try
        {
            using var cts = new CancellationTokenSource(_messagingOptions.ResponseTimeout);
            if (enumerator.MoveNextTask is { } task)
            {
                await task.WaitAsync(cts.Token).SuppressThrowing();
            }

            if (enumerator.MoveNextTask is null or { IsCompleted: true } && enumerator.Enumerator is { } value)
            {
                await value.DisposeAsync().AsTask().WaitAsync(cts.Token).SuppressThrowing();
            }
        }
        catch (Exception exception)
        {
            LogWarningErrorDisposingEnumerator(exception);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Timer.Dispose();
    }

    private static void OnAsyncEnumeratorGrainExtensionCreated(AsyncEnumerableGrainExtension extension)
    {
        if (DiagnosticListener.IsEnabled())
        {
            DiagnosticListener.Write(nameof(OnAsyncEnumeratorGrainExtensionCreated), extension);
        }
    }

    private static void OnEnumeratorCleanupCompleted(AsyncEnumerableGrainExtension extension)
    {
        if (DiagnosticListener.IsEnabled())
        {
            DiagnosticListener.Write(nameof(OnEnumeratorCleanupCompleted), extension);
        }
    }

    private struct EnumeratorState
    {
        private const int SeenFlag = 0x01;
        private const int BusyFlag = 0x10;
        private int _flags;
        public IAsyncDisposable? Enumerator;
        public Task<bool>? MoveNextTask;
        public int MaxBatchSize;
        internal CancellationTokenSource? CancellationTokenSource;
        public void SetSeen() => _flags |= SeenFlag;
        public void SetBusy() => _flags |= BusyFlag | SeenFlag;
        public void ClearBusy() => _flags = SeenFlag; // Clear the 'Busy' flag, but set the 'Seen' flag.
        public bool ClearSeen()
        {
            // Clear the 'Seen' flag and check if any flags were set previously.
            var isExpired = _flags == 0;
            _flags &= ~SeenFlag;
            return isExpired;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error cancelling enumerator."
    )]
    private partial void LogWarningErrorCancellingEnumerator(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error disposing enumerator."
    )]
    private partial void LogWarningErrorDisposingEnumerator(Exception exception);
}
