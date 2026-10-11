using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;

internal static class Program
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .UseOrleans(builder => builder.UseLocalhostClustering())
            .ConfigureLogging(builder => builder.AddConsole())
            .Build();

        await host.StartAsync();

        var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
        var observer = new QueueObserver();
        var observerRef = grainFactory.CreateObjectReference<IQueueObserver>(observer);

        try
        {
            await RunSingleImmediateAsync(observerRef);
            await RunSingleWithPendingCompletionAsync(observerRef);
            await RunBatchOnceAsync(observerRef);
            await RunDelayedResultsAsync(observerRef);
            await RunCancellationAsync(observerRef, observer);
            await RunDisposeWhileMoveNextIsPendingAsync(observerRef, observer);

            ClientLog("All tests completed.");
        }
        finally
        {
            grainFactory.DeleteObjectReference<IQueueObserver>(observerRef);
            await host.StopAsync();
        }

        Console.ReadKey();
    }

    private static async Task RunSingleImmediateAsync(IQueueObserver observer)
    {
        ClientLog("TEST 1: Single synchronous element; immediate completion.");

        var count = 0;

        await foreach (var job in observer.GetSingleImmediateAsync().WithBatchSize(10))
        {
            ClientLog($"Received {job.Id}");
            count++;
        }

        Assert(count == 1, $"Expected 1 element, received {count}.");

        ClientLog("TEST 1 PASSED.");
    }

    private static async Task RunSingleWithPendingCompletionAsync(IQueueObserver observer)
    {
        ClientLog("TEST 2: One element followed by asynchronous completion.");

        var count = 0;

        await foreach (var job in observer.GetSingleWithPendingCompletionAsync().WithBatchSize(10))
        {
            ClientLog($"Received {job.Id}");
            count++;
        }

        Assert(count == 1, $"Expected 1 element, received {count}.");

        ClientLog("TEST 2 PASSED.");
    }

    private static async Task RunBatchOnceAsync(IQueueObserver observer)
    {
        ClientLog("TEST 3: One synchronously completed batch.");

        const int batchSize = 100;
        const int expectedCount = 99;

        var count = 0;

        await foreach (var job in observer.GetBatchOnceAsync().WithBatchSize(batchSize))
        {
            count++;

            if (count <= 3 || count == expectedCount)
            {
                ClientLog($"Received {job.Id} ({count}/{expectedCount})");
            }
        }

        Assert(count == expectedCount, $"Expected {expectedCount} elements, received {count}.");

        ClientLog($"TEST 3 PASSED. Received all {count} elements.");
    }

    private static async Task RunDelayedResultsAsync(IQueueObserver observer)
    {
        ClientLog("TEST 4: Delayed elements; pending MoveNext operations.");

        var count = 0;

        await foreach (var job in observer.GetDelayedJobsAsync().WithBatchSize(4))
        {
            ClientLog($"Received {job.Id}");
            count++;
        }

        Assert(count == 3, $"Expected 3 elements, received {count}.");

        ClientLog("TEST 4 PASSED.");
    }

    private static async Task RunCancellationAsync(
        IQueueObserver observerRef,
        QueueObserver observer)
    {
        ClientLog("TEST 5: Cancel while MoveNextAsync is pending.");

        using var cancellation = new CancellationTokenSource();

        var enumerator = observerRef
            .GetCancellationProbeAsync()
            .GetAsyncEnumerator(cancellation.Token);

        Task<bool>? pendingMoveNext = null;

        try
        {
            Assert(
                await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout),
                "Expected the first element.");

            ClientLog($"Received {enumerator.Current.Id}");

            // The observer now waits indefinitely for cancellation.
            pendingMoveNext = enumerator.MoveNextAsync().AsTask();

            await Task.Delay(250);

            ClientLog("Canceling the enumeration.");
            await cancellation.CancelAsync();

            // Confirms that cancellation reached the iterator and ran its finally block.
            await observer.CancellationProbeFinished.WaitAsync(TestTimeout);

            await ObservePendingMoveNextAsync(
                pendingMoveNext,
                "MoveNext after cancellation");

            ClientLog("TEST 5 PASSED. The iterator's finally block ran.");
        }
        finally
        {
            await DisposeClientEnumeratorAsync(enumerator);
        }
    }

    private static async Task RunDisposeWhileMoveNextIsPendingAsync(
        IQueueObserver observerRef,
        QueueObserver observer)
    {
        ClientLog("TEST 6: Explicit DisposeAsync while MoveNextAsync is pending.");

        var enumerator = observerRef
            .GetDisposeProbeAsync()
            .GetAsyncEnumerator();

        Task<bool>? pendingMoveNext = null;
        Task? disposeTask = null;

        try
        {
            Assert(
                await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout),
                "Expected the first element.");

            ClientLog($"Received {enumerator.Current.Id}");

            // Start a second move which will block inside the observer.
            pendingMoveNext = enumerator.MoveNextAsync().AsTask();

            await Task.Delay(250);

            ClientLog("Calling DisposeAsync while the move is still pending.");

            // This is deliberately independent of the pending MoveNext operation.
            disposeTask = enumerator.DisposeAsync().AsTask();

            await disposeTask.WaitAsync(TestTimeout);

            // Confirms that disposing the remote enumerator canceled its producer.
            await observer.DisposeProbeFinished.WaitAsync(TestTimeout);

            await ObservePendingMoveNextAsync(
                pendingMoveNext,
                "MoveNext after explicit disposal");

            ClientLog("TEST 6 PASSED. The iterator's finally block ran.");
        }
        finally
        {
            // Do not issue a second disposal request if one is already in progress.
            if (disposeTask is null)
            {
                await DisposeClientEnumeratorAsync(enumerator);
            }
        }
    }

    private static async Task ObservePendingMoveNextAsync(
        Task<bool> moveNextTask,
        string operation)
    {
        try
        {
            var hasElement = await moveNextTask.WaitAsync(TestTimeout);

            ClientLog($"{operation} completed with result: {hasElement}.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{operation} did not finish within {TestTimeout}.");
        }
        catch (OperationCanceledException exception)
        {
            ClientLog($"{operation} was canceled: {exception.GetType().Name}.");
        }
        catch (Exception exception)
        {
            // Log the terminal result instead of assuming every implementation
            // translates cancellation and disposal into the same exception.
            ClientLog($"{operation} terminated with {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task DisposeClientEnumeratorAsync<T>(
        IAsyncEnumerator<T> enumerator)
    {
        await enumerator.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void ClientLog(string message) =>
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [client]   {message}");

    internal static void ObserverLog(string message) =>
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [observer] {message}");
}

public interface IQueueObserver : IGrainObserver
{
    IAsyncEnumerable<QueueJob> GetSingleImmediateAsync();

    IAsyncEnumerable<QueueJob> GetSingleWithPendingCompletionAsync();

    IAsyncEnumerable<QueueJob> GetBatchOnceAsync();

    IAsyncEnumerable<QueueJob> GetDelayedJobsAsync();

    IAsyncEnumerable<QueueJob> GetCancellationProbeAsync();

    IAsyncEnumerable<QueueJob> GetDisposeProbeAsync();
}

public sealed class QueueObserver : IQueueObserver
{
    private const int BatchCount = 99;

    private readonly TaskCompletionSource<bool> _cancellationProbeFinished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<bool> _disposeProbeFinished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task CancellationProbeFinished => _cancellationProbeFinished.Task;

    public Task DisposeProbeFinished => _disposeProbeFinished.Task;

    public IAsyncEnumerable<QueueJob> GetSingleImmediateAsync() =>
        SingleImmediateCore();

    public IAsyncEnumerable<QueueJob> GetSingleWithPendingCompletionAsync() =>
        SingleWithPendingCompletionCore();

    public IAsyncEnumerable<QueueJob> GetBatchOnceAsync() =>
        BatchOnceCore();

    public IAsyncEnumerable<QueueJob> GetDelayedJobsAsync() =>
        DelayedJobsCore();

    public IAsyncEnumerable<QueueJob> GetCancellationProbeAsync() =>
        CancellationProbeCore();

    public IAsyncEnumerable<QueueJob> GetDisposeProbeAsync() =>
        DisposeProbeCore();

    private static async IAsyncEnumerable<QueueJob> SingleImmediateCore(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            // Completed await keeps the iterator's initial execution synchronous.
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Program.ObserverLog("SingleImmediate: yielding one element.");

            yield return new QueueJob("single-immediate");

            Program.ObserverLog("SingleImmediate: reached natural completion.");
        }
        finally
        {
            Program.ObserverLog("SingleImmediate: finally.");
        }
    }

    private static async IAsyncEnumerable<QueueJob> SingleWithPendingCompletionCore(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Program.ObserverLog("SinglePending: yielding one element.");

            yield return new QueueJob("single-pending");

            // The first result is available synchronously, but the next MoveNext
            // must wait before discovering that the source has completed.
            Program.ObserverLog("SinglePending: delaying completion.");

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

            Program.ObserverLog("SinglePending: reached natural completion.");
        }
        finally
        {
            Program.ObserverLog("SinglePending: finally.");
        }
    }

    private static async IAsyncEnumerable<QueueJob> BatchOnceCore(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.CompletedTask.ConfigureAwait(false);

            Program.ObserverLog($"BatchOnce: starting {BatchCount} synchronous elements.");

            for (var i = 0; i < BatchCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (i == 0 || i == BatchCount - 1)
                {
                    Program.ObserverLog($"BatchOnce: yielding batch-{i:D2}.");
                }

                yield return new QueueJob($"batch-{i:D2}");
            }

            Program.ObserverLog("BatchOnce: reached natural completion.");
        }
        finally
        {
            Program.ObserverLog("BatchOnce: finally.");
        }
    }

    private static async IAsyncEnumerable<QueueJob> DelayedJobsCore(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            for (var i = 0; i < 3; i++)
            {
                Program.ObserverLog($"Delayed: waiting before element {i}.");

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);

                Program.ObserverLog($"Delayed: yielding element {i}.");

                yield return new QueueJob($"delayed-{i}");
            }

            Program.ObserverLog("Delayed: reached natural completion.");
        }
        finally
        {
            Program.ObserverLog("Delayed: finally.");
        }
    }

    private async IAsyncEnumerable<QueueJob> CancellationProbeCore(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Program.ObserverLog("CancellationProbe: yielding first element.");

            yield return new QueueJob("cancel-first");

            Program.ObserverLog("CancellationProbe: waiting indefinitely.");

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);

            // This line should never execute.
            yield return new QueueJob("cancel-unexpected");
        }
        finally
        {
            Program.ObserverLog("CancellationProbe: finally executed.");
            _cancellationProbeFinished.TrySetResult(true);
        }
    }

    private async IAsyncEnumerable<QueueJob> DisposeProbeCore(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Program.ObserverLog("DisposeProbe: yielding first element.");

            yield return new QueueJob("dispose-first");

            Program.ObserverLog("DisposeProbe: waiting indefinitely.");

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);

            // This line should never execute.
            yield return new QueueJob("dispose-unexpected");
        }
        finally
        {
            Program.ObserverLog("DisposeProbe: finally executed.");
            _disposeProbeFinished.TrySetResult(true);
        }
    }
}

[GenerateSerializer]
public sealed record QueueJob([property: Id(0)] string Id);
