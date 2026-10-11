using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans;

internal sealed partial class InvokableObjectManager
{
    private readonly ConcurrentDictionary<ObserverGrainId, LocalObjectData> localObjects = new ConcurrentDictionary<ObserverGrainId, LocalObjectData>();

    private readonly InterfaceToImplementationMappingCache _interfaceToImplementationMapping;
    private readonly IGrainContext rootGrainContext;
    private readonly IRuntimeClient runtimeClient;
    private readonly ILogger logger;
    private readonly DeepCopier deepCopier;
    private readonly DeepCopier<Response> _responseCopier;
    private readonly MessagingTrace messagingTrace;
    private readonly AdmissionGate _invocations = new();
    private readonly AdmissionGate _cancellations = new();
    private List<IIncomingGrainCallFilter>? _grainCallFilters;

    private List<IIncomingGrainCallFilter> GrainCallFilters
        => _grainCallFilters ??= [.. runtimeClient.ServiceProvider.GetServices<IIncomingGrainCallFilter>()];

    public InvokableObjectManager(
        IGrainContext rootGrainContext,
        IRuntimeClient runtimeClient,
        DeepCopier deepCopier,
        MessagingTrace messagingTrace,
        DeepCopier<Response> responseCopier,
        InterfaceToImplementationMappingCache interfaceToImplementationMapping,
        ILogger logger)
    {
        this.rootGrainContext = rootGrainContext;
        this.runtimeClient = runtimeClient;
        this.deepCopier = deepCopier;
        this.messagingTrace = messagingTrace;
        _responseCopier = responseCopier;
        _interfaceToImplementationMapping = interfaceToImplementationMapping;
        this.logger = logger;
    }

    public bool TryRegister(IAddressable obj, ObserverGrainId objectId)
    {
        return this.localObjects.TryAdd(objectId, new LocalObjectData(obj, objectId, this));
    }

    public bool TryDeregister(ObserverGrainId objectId)
    {
        if (!this.localObjects.TryRemove(objectId, out var objectData))
        {
            return false;
        }

        objectData?.DisposeAsync().Ignore();

        return true;
    }

    /// <summary>
    /// Closes observer admission and drains invocation processing, including cancellation controls and response handling.
    /// </summary>
    public async Task StopAsync()
    {
        // Cancellation controls can release admitted work while application admission is closed.
        await _invocations.CloseAsync().ConfigureAwait(false);
        await _cancellations.CloseAsync().ConfigureAwait(false);

        // Dispose all registered observers, also cancelling any outstanding enumerators.
        await Task.WhenAll(localObjects.Select(kvp =>
        {
            localObjects.TryRemove(kvp.Key, out var objectData);
            return objectData?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        })).ConfigureAwait(false);
    }

    internal static bool IsCancellationRequest(Message message) =>
        message.BodyObject is IInvokable request && request.GetInterfaceType() == typeof(IGrainCallCancellationExtension);

    private AdmissionGate GetAdmissionGate(Message message) =>
        IsCancellationRequest(message) ? _cancellations : _invocations;

    internal void RejectMessage(Message message)
    {
        LogRejectingMessageDuringShutdown(logger, message);
        if (message.Direction == Message.Directions.Request)
        {
            runtimeClient.SendResponse(message, Response.FromException(
                new SiloUnavailableException("The local Orleans host is shutting down and can no longer accept observer invocations.")));
        }
    }

    public void Dispatch(Message message)
    {
        if (!ObserverGrainId.TryParse(message.TargetGrain, out var observerId))
        {
            LogNotAddressedToAnObserver(logger, message);
            return;
        }

        if (this.localObjects.TryGetValue(observerId, out var objectData))
        {
            objectData.ReceiveMessage(message);
        }
        else
        {
            LogUnexpectedTargetInRequest(logger, message.TargetGrain, message);
        }
    }

    private readonly struct ObserverGrainIdLogValue(ObserverGrainId observerId)
    {
        public override string ToString() => observerId.ToString();
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error disposing an observer during shutdown.")]
    private static partial void LogErrorDisposingObservers(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rejecting observer invocation during shutdown: '{Message}'."
    )]
    private static partial void LogRejectingMessageDuringShutdown(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.ProxyClient_OGC_TargetNotFound_2,
        Level = LogLevel.Error,
        Message = "Message is not addressed to an observer. Message: '{Message}'."
    )]
    private static partial void LogNotAddressedToAnObserver(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.ProxyClient_OGC_TargetNotFound,
        Level = LogLevel.Error,
        Message = "Unexpected target grain in request: {TargetGrain}. Message: '{Message}'."
    )]
    private static partial void LogUnexpectedTargetInRequest(ILogger logger, GrainId targetGrain, Message message);

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)ErrorCode.Runtime_Error_100162,
        Message = "Object associated with id '{ObserverId}' has been garbage collected. Deleting object reference and unregistering it. Message: '{Message}'."
    )]
    private static partial void LogObserverGarbageCollected(ILogger logger, ObserverGrainId observerId, Message message);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "InvokeLocalObjectAsync '{Message}' start '{Start}'."
    )]
    private static partial void LogInvokeLocalObjectAsync(ILogger logger, Message message, bool start);

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)ErrorCode.ProxyClient_OGC_SendResponseFailed,
        Message = "Error sending a response."
    )]
    private static partial void LogErrorSendingResponse(ILogger logger, Exception exc);

    [LoggerMessage(
        Level = LogLevel.Error,
        EventId = (int)ErrorCode.ProxyClient_OGC_UnhandledExceptionInOneWayInvoke,
        Message = "Exception during invocation of notification '{Request}', interface '{Interface}'. Ignoring exception because this is a one way request."
    )]
    private static partial void LogErrorInvokingOneWayRequest(ILogger logger, Exception exception, string? request, GrainInterfaceType @interface);

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)ErrorCode.ProxyClient_OGC_SendExceptionResponseFailed,
        Message = "Error sending an exception response."
    )]
    private static partial void LogErrorSendingExceptionResponse(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception during message body deserialization for message: '{Message}'."
    )]
    private static partial void LogErrorDeserializingMessageBody(ILogger logger, Exception exception, Message message);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception processing message '{Message}'."
    )]
    private static partial void LogErrorProcessingMessage(ILogger logger, Exception exception, Message message);
}
