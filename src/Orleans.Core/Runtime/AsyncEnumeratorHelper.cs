using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// Shared helpers for the grain-side and observer-side async enumerable extensions.
/// </summary>
internal static class AsyncEnumeratorHelper
{
    public readonly struct BatchFillResult
    {
        public BatchFillResult(EnumerationResult status, object? value, Task<bool>? pendingTask)
        {
            Status = status;
            Value = value;
            PendingTask = pendingTask;
        }

        public EnumerationResult Status { get; }
        public object? Value { get; }
        public Task<bool>? PendingTask { get; }
    }

    /// <summary>
    /// Attempts to synchronously produce up to <paramref name="maxBatchSize"/> elements.
    /// </summary>
    public static BatchFillResult FillBatchSync<T>(IAsyncEnumerator<T> enumerator, int maxBatchSize)
    {
        Debug.Assert(maxBatchSize > 0, "Max batch size must be positive.");

        var count = 0;
        object? result = null;

        try
        {
            while (count < maxBatchSize)
            {
                var moveNext = enumerator.MoveNextAsync();

                if (!moveNext.IsCompletedSuccessfully)
                {
                    return new BatchFillResult(count switch
                    {
                        0 => EnumerationResult.Heartbeat,
                        1 => EnumerationResult.Element,
                        _ => EnumerationResult.Batch,
                    }, result, moveNext.AsTask());
                }

                if (!moveNext.Result)
                {
                    return new BatchFillResult(count switch
                    {
                        0 => EnumerationResult.Completed,
                        1 => EnumerationResult.CompletedWithElement,
                        _ => EnumerationResult.CompletedWithBatch,
                    }, result, pendingTask: null);
                }

                ++count;

                var value = enumerator.Current;

                switch (count)
                {
                    case 1: result = value; break;
                    case 2: result = new List<T> { (T)result!, value }; break;
                    default: ((List<T>)result!).Add(value); break;
                }
            }

            return new BatchFillResult(count == 1 ? EnumerationResult.Element : EnumerationResult.Batch, result, pendingTask: null);
        }
        catch (Exception exception)
        {
            return new BatchFillResult(EnumerationResult.Error, exception, pendingTask: null);
        }
    }

    /// <summary>
    /// Awaits a pending MoveNext with a long-poll timeout, then classifies the outcome.
    /// </summary>
    public static async ValueTask<(EnumerationResult Status, object? Value)> AwaitPendingMoveNextAsync<T>(
        IAsyncEnumerator<T> enumerator, Task<bool> moveNextTask, TimeSpan longPollTimeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(longPollTimeout);

        try
        {
            await moveNextTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (EnumerationResult.Canceled, null);
        }
        catch (OperationCanceledException)
        {
            // Long-poll elapsed, or the task itself was cancelled.
            if (moveNextTask.IsCompleted)
            {
                return ClassifyPending(enumerator, moveNextTask);
            }
            return (EnumerationResult.Heartbeat, null);
        }
        catch
        {
            // Task faulted; classify below.
        }

        return ClassifyPending(enumerator, moveNextTask);
    }

    /// <summary>Classifies a completed MoveNext task.</summary>
    public static (EnumerationResult Status, object? Value) ClassifyPending<T>(IAsyncEnumerator<T> enumerator, Task<bool> moveNextTask)
    {
        Debug.Assert(moveNextTask.IsCompleted);

        if (moveNextTask.IsCompletedSuccessfully)
        {
            return moveNextTask.Result
                ? (EnumerationResult.Element, enumerator.Current)
                : (EnumerationResult.Completed, null);
        }

        if (moveNextTask.IsCanceled)
        {
            return (EnumerationResult.Canceled, null);
        }

        var exception = moveNextTask.Exception!;
        var inner = exception.InnerExceptions.Count == 1 ? exception.InnerException! : exception;
        return (EnumerationResult.Error, inner);
    }
}
