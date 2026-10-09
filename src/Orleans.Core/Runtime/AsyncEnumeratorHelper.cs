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
    /// <summary>
    /// Attempts to synchronously produce up to <paramref name="maxBatchSize"/> elements.
    /// </summary>
    public static (EnumerationResult Result, object? Value, Task<bool>? PendingTask)
        FillBatchSync<T>(IAsyncEnumerator<T> enumerator, int maxBatchSize)
    {
        Debug.Assert(maxBatchSize > 0, "Max batch size must be positive.");

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

    /// <summary>
    /// Awaits a pending MoveNext with a long-poll timeout, then classifies the outcome.
    /// </summary>
    public static async ValueTask<(EnumerationResult Status, object? Value)>
        AwaitPendingMoveNextAsync<T>(IAsyncEnumerator<T> enumerator, Task<bool> moveNextTask, TimeSpan longPollTimeout, CancellationToken callerToken)
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
