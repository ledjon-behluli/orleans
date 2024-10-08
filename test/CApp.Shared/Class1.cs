namespace CApp.Shared;

public interface ITestGrain : IGrainWithGuidKey
{
    Task Subscribe(ITestGrainObserver observer);
    Task Start();
}

public interface ITestGrainObserver : IGrainObserver
{
    Task Receive(string msg);
}