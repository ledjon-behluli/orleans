#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal class StatelessWorkerGrainContext : IGrainContext, IAsyncDisposable, IActivationLifecycleObserver
{
    private readonly GrainAddress _address;
    private readonly GrainTypeSharedContext _shared;
    private readonly IGrainContextActivator _innerActivator;
    private readonly int _maxWorkers;
    private readonly List<ActivationData> _workers = new();
    private readonly ConcurrentQueue<(WorkItemType Type, object State)> _workItems = new();
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = false };

#pragma warning disable IDE0052 // Remove unread private members
    /// <summary>
    /// The <see cref="Task"/> representing the <see cref="RunMessageLoop"/> invocation.
    /// This is written once but never otherwise accessed. The purpose of retaining this field is for
    /// debugging, where being able to identify the message loop task corresponding to an activation can
    /// be useful.
    /// </summary>
    private readonly Task _messageLoopTask;
    /// <summary>
    /// The <see cref="Task"/> representing the <see cref="PeriodicallyCollectWorkers"/> invocation.
    /// This is written once but never otherwise accessed. The purpose of retaining this field is for
    /// debugging, where being able to identify the message loop task corresponding to an activation can
    /// be useful.
    /// </summary>
    private readonly Task? _workerCollectionlTask;
#pragma warning restore IDE0052 // Remove unread private members

    private GrainReference? _grainReference;

    public StatelessWorkerGrainContext(
        GrainAddress address,
        GrainTypeSharedContext sharedContext,
        IGrainContextActivator innerActivator)
    {
        _address = address;
        _shared = sharedContext;
        _innerActivator = innerActivator;

        var strategy = (StatelessWorkerPlacement)_shared.PlacementStrategy;

        _maxWorkers = strategy.MaxLocal;
        _messageLoopTask = Task.Run(RunMessageLoop);

        if (strategy.ProactiveWorkerCollection)
        {
            _workerCollectionlTask = PeriodicallyCollectWorkers();
        }
    }

    public GrainReference GrainReference => _grainReference ??= _shared.GrainReferenceActivator.CreateReference(GrainId, default);

    public GrainId GrainId => _address.GrainId;

    public object? GrainInstance => null;

    public ActivationId ActivationId => _address.ActivationId;

    public GrainAddress Address => _address;

    public IServiceProvider ActivationServices => throw new NotImplementedException();

    public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();

    public IWorkItemScheduler Scheduler => throw new NotImplementedException();

    public PlacementStrategy PlacementStrategy => _shared.PlacementStrategy;

    public Task Deactivated
    {
        get
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueWorkItem(WorkItemType.DeactivatedTask, new DeactivatedTaskWorkItemState(completion));
            return completion.Task;
        }
    }

    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
    {
    }

    public void ReceiveMessage(object message)
    {
        EnqueueWorkItem(WorkItemType.Message, message);
    }

    public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken)
    {
        EnqueueWorkItem(WorkItemType.Deactivate, new DeactivateWorkItemState(deactivationReason, cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueWorkItem(WorkItemType.DisposeAsync, new DisposeAsyncWorkItemState(completion));
        await completion.Task;
    }

    private void EnqueueWorkItem(WorkItemType type, object state)
    {
        _workItems.Enqueue(new(type, state));
        _workSignal.Signal();
    }

    public bool Equals([AllowNull] IGrainContext other) => other is not null && ActivationId.Equals(other.ActivationId);

    public TComponent? GetComponent<TComponent>() where TComponent : class => this switch
    {
        TComponent contextResult => contextResult,
        _ => _shared.GetComponent<TComponent>()
    };

    public void SetComponent<TComponent>(TComponent? instance) where TComponent : class
    {
        if (typeof(TComponent) != typeof(GrainCanInterleave))
        {
            throw new ArgumentException($"Cannot set a component of type '{typeof(TComponent)}' on a {nameof(StatelessWorkerGrainContext)}");
        }

        _shared.SetComponent(instance);
    }

    public TTarget GetTarget<TTarget>() where TTarget : class => throw new NotImplementedException();

    private async Task RunMessageLoop()
    {
        while (true)
        {
            try
            {
                while (_workItems.TryDequeue(out var workItem))
                {
                    switch (workItem.Type)
                    {
                        case WorkItemType.Message:
                            ReceiveMessageInternal(workItem.State);
                            break;
                        case WorkItemType.Deactivate:
                            {
                                var state = (DeactivateWorkItemState)workItem.State;
                                DeactivateInternal(state.DeactivationReason, state.CancellationToken);
                                break;
                            }
                        case WorkItemType.DeactivatedTask:
                            {
                                var state = (DeactivatedTaskWorkItemState)workItem.State;
                                _ = DeactivatedTaskInternal(state.Completion);
                                break;
                            }
                        case WorkItemType.DisposeAsync:
                            {
                                var state = (DisposeAsyncWorkItemState)workItem.State;
                                _ = DisposeAsyncInternal(state.Completion);
                                break;
                            }
                        case WorkItemType.OnDestroyActivation:
                            {
                                var grainContext = (ActivationData)workItem.State;
                                _workers.Remove(grainContext);
                                if (_workers.Count == 0)
                                {
                                    // When the last worker is destroyed, we can consider the stateless worker grain
                                    // activation to be destroyed as well
                                    _shared.InternalRuntime.Catalog.UnregisterMessageTarget(this);
                                }
                                break;
                            }
                        default: throw new NotSupportedException($"Work item of type {workItem.Type} is not supported");
                    }
                }

                await _workSignal.WaitAsync();
            }
            catch (Exception exception)
            {
                _shared.Logger.LogError(exception, "Error in stateless worker message loop");
            }
        }
    }

    private void ReceiveMessageInternal(object message)
    {
        try
        {
            ActivationData? worker = null;
            ActivationData? minimumWaitingCountWorker = null;
            var minimumWaitingCount = int.MaxValue;

            // Make sure there is at least one worker
            if (_workers.Count == 0)
            {
                worker = CreateWorker(message);
            }
            else
            {
                // Check to see if we have any inactive workers, prioritizing
                // them in the order they were created to minimize the number
                // of workers spawned
                for (var i = 0; i < _workers.Count; i++)
                {
                    if (_workers[i].IsInactive)
                    {
                        worker = _workers[i];
                        break;
                    }
                    else
                    {
                        // Track the worker with the lowest value for WaitingCount,
                        // this is used if all workers are busy
                        if (_workers[i].WaitingCount < minimumWaitingCount)
                        {
                            minimumWaitingCount = _workers[i].WaitingCount;
                            minimumWaitingCountWorker = _workers[i];
                        }
                    }
                }

                if (worker is null)
                {
                    if (_workers.Count >= _maxWorkers)
                    {
                        // Pick the one with the lowest waiting count
                        worker = minimumWaitingCountWorker;
                    }

                    // If there are no workers, make one.
                    worker ??= CreateWorker(message);
                }
            }

            worker.ReceiveMessage(message);
        }
        catch (Exception exception) when (message is Message msg)
        {
            _shared.InternalRuntime.MessageCenter.RejectMessage(
                msg,
                Message.RejectionTypes.Transient,
                exception,
                "Exception while creating grain context");
        }
    }

    private double _previousError = 0; 
    private double _integralTerm = 0;

    private const double Kp = 0.433;
    private const double Ki = 0.468;
    private const double Kd = 0.480;

    private const int WorkerRemovalBackoffMs = 1000;
    private const int WorkerRemovalPeriodMs = 500;
    private const int ControlSignalNegativeMaxCount = 10;

    private PeriodicTimer? _monitorTimer;
    private int _controlSignalNegativeCount = 0;
    private DateTime _lastWorkerRemovalTime = DateTime.MinValue;

    private void TriggerWorkerRemoval()
    {
        try
        {
            var averageWaitingCount = _workers.Count > 0 ? _workers.Average(w => w.WaitingCount) : 0;
            var error = -averageWaitingCount; // Our target is 0 waiting count: 0 - avgWC = -avgWC

            // Reset integral term if error changes sign, to prevent windup.
            if (_previousError != 0 && Math.Sign(error) != Math.Sign(_previousError))
            {
                //_integralTerm = 0;
            }

            _integralTerm += error;
            var derivativeTerm = error - _previousError;
            _previousError = error;

            Console.WriteLine("IntegralTerm: " + _integralTerm);

            var controlSignal = Kp * error + Ki * _integralTerm + Kd * derivativeTerm;

            _controlSignalNegativeCount = controlSignal < 0 ? ++_controlSignalNegativeCount : 0;

            if (_controlSignalNegativeCount > ControlSignalNegativeMaxCount &&
                (DateTime.Now - _lastWorkerRemovalTime).TotalMilliseconds > WorkerRemovalBackoffMs)
            {
                var inactiveWorkers = _workers.Where(w => w.IsInactive).ToImmutableArray();
                if (inactiveWorkers.Length > 0)
                {
                    var worker = inactiveWorkers[Random.Shared.Next(inactiveWorkers.Length)];
                    var cancellation = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);

                    worker.Deactivate(new(DeactivationReasonCode.RuntimeRequested,
                        "Worker deactivated due to inactivity."), cancellation.Token);

                    _controlSignalNegativeCount = 0;
                    _lastWorkerRemovalTime = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
    }

    private async Task PeriodicallyCollectWorkers()
    {
        var workerRemovalTimespan = TimeSpan.FromMilliseconds(WorkerRemovalPeriodMs);
        _monitorTimer = new(workerRemovalTimespan);

        try
        {
            while (await _monitorTimer.WaitForNextTickAsync())
            {
                var averageWaitingCount = _workers.Count > 0 ? _workers.Average(w => w.WaitingCount) : 0;
                var error = -averageWaitingCount; // Our target is 0 waiting count: 0 - avgWC = -avgWC

                _integralTerm += error;
                var derivativeTerm = error - _previousError;
                _previousError = error;

                var controlSignal = Kp * error + Ki * _integralTerm + Kd * derivativeTerm;

                _controlSignalNegativeCount = controlSignal < 0 ? ++_controlSignalNegativeCount : 0;

                if (_controlSignalNegativeCount > ControlSignalNegativeMaxCount &&
                    (DateTime.Now - _lastWorkerRemovalTime).TotalMilliseconds > WorkerRemovalBackoffMs)
                {
                    var inactiveWorkers = _workers.Where(w => w.IsInactive).ToImmutableArray();
                    if (inactiveWorkers.Length > 0)
                    {
                        var worker = inactiveWorkers[Random.Shared.Next(inactiveWorkers.Length)];
                        var cancellation = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);

                        worker.Deactivate(new(DeactivationReasonCode.RuntimeRequested,
                            "Worker deactivated due to inactivity."), cancellation.Token);

                        var antiWindUpFactor = (double)(inactiveWorkers.Length - 1) / inactiveWorkers.Length;
                        _integralTerm *= antiWindUpFactor;

                        _controlSignalNegativeCount = 0;
                        _lastWorkerRemovalTime = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
    }


    private ActivationData CreateWorker(object? message)
    {
        var address = GrainAddress.GetAddress(_address.SiloAddress, _address.GrainId, ActivationId.NewId());
        var newWorker = (ActivationData)_innerActivator.CreateContext(address);

        // Observe the create/destroy lifecycle of the activation
        newWorker.SetComponent<IActivationLifecycleObserver>(this);

        // If this is a new worker and there is a message in scope, try to get the request context and activate the worker
        var requestContext = (message as Message)?.RequestContextData ?? [];
        var cancellation = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.ActivationTimeout);
        newWorker.Activate(requestContext, cancellation.Token);

        _workers.Add(newWorker);
        return newWorker;
    }

    private void DeactivateInternal(DeactivationReason reason, CancellationToken cancellationToken)
    {
        foreach (var worker in _workers)
        {
            worker.Deactivate(reason, cancellationToken);
        }

        _monitorTimer?.Dispose();
        _monitorTimer = null;
    }

    private async Task DeactivatedTaskInternal(TaskCompletionSource<bool> completion)
    {
        try
        {
            var tasks = new List<Task>(_workers.Count);
            foreach (var worker in _workers)
            {
                tasks.Add(worker.Deactivated);
            }

            await Task.WhenAll(tasks);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }
    }

    private async Task DisposeAsyncInternal(TaskCompletionSource<bool> completion)
    {
        try
        {
            var tasks = new List<Task>(_workers.Count);
            foreach (var worker in _workers)
            {
                try
                {
                    if (worker is IAsyncDisposable disposable)
                    {
                        tasks.Add(disposable.DisposeAsync().AsTask());
                    }
                }
                catch (Exception exception)
                {
                    tasks.Add(Task.FromException(exception));
                }
            }

            await Task.WhenAll(tasks);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }
    }

    public void OnCreateActivation(IGrainContext grainContext)
    {
    }

    public void OnDestroyActivation(IGrainContext grainContext)
    {
        EnqueueWorkItem(WorkItemType.OnDestroyActivation, grainContext);
    }

    public void Rehydrate(IRehydrationContext context)
    {
        // Migration is not supported, but we need to dispose of the context if it's provided
        (context as IDisposable)?.Dispose();
    }

    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
    {
        // Migration is not supported. Do nothing: the contract is that this method attempts migration, but does not guarantee it will occur.
    }

    private enum WorkItemType
    {
        Message,
        Deactivate,
        DeactivatedTask,
        DisposeAsync,
        OnDestroyActivation,
    }

    private record ActivateWorkItemState(Dictionary<string, object>? RequestContext, CancellationToken CancellationToken);
    private record DeactivateWorkItemState(DeactivationReason DeactivationReason, CancellationToken CancellationToken);
    private record DeactivatedTaskWorkItemState(TaskCompletionSource<bool> Completion);
    private record DisposeAsyncWorkItemState(TaskCompletionSource<bool> Completion);
}