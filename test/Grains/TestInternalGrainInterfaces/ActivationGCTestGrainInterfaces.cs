namespace UnitTests.GrainInterfaces
{
    public interface IIdleActivationGcTestGrain1 : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface IIdleActivationGcTestGrain2 : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface IBusyActivationGcTestGrain1 : IGrainWithGuidKey
    {
        Task Nop();
        Task Delay(TimeSpan dt);
        Task<string> IdentifyActivation();
    }

    public interface IBusyActivationGcTestGrain2 : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface ICollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface IStatelessWorkerActivationCollectorTestGrain1 : IGrainWithGuidKey
    {
        Task Nop();
        Task Delay(TimeSpan dt);
        Task<string> IdentifyActivation();
    }
}
