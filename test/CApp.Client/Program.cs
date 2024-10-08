using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using CApp.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration.Internal;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .ConfigureServices(services =>
    {
        services.AddSingleton<ObserverFactory>();
        services.AddFromExisting<IGrainObserverFactory, ObserverFactory>();
    })
    .UseOrleansClient(client => client.UseLocalhostClustering())
    .Build();

await host.StartAsync();

var clusterClient = host.Services.GetRequiredService<IClusterClient>();
var observerFactory = host.Services.GetRequiredService<ObserverFactory>();

(var id, var observer) = observerFactory.CreateRandom();

var grain = clusterClient.GetGrain<ITestGrain>(Guid.NewGuid());
var _ref = clusterClient.CreateObjectReference<ITestGrainObserver>(observer, id);

await grain.Subscribe(_ref);
Console.WriteLine("Subscribed to grain as reference: " + ((GrainReference)_ref).ToString());
await grain.Start();

await Task.Delay(10_000);
await host.StopAsync();
Environment.Exit(0);

PeriodicTimer timer = new(TimeSpan.FromSeconds(5));

while (await timer.WaitForNextTickAsync())
{
    //clusterClient.DeleteObjectReference<ITestGrainObserver>(_ref);
    //Console.WriteLine("Deleted reference: " + ((GrainReference)_ref).ToString());
}

Console.ReadKey();

public class ClassSubscriber : ITestGrainObserver
{
    public Task Receive(string msg)
    {
        Console.WriteLine($"{nameof(ClassSubscriber)}: {msg}");
        return Task.CompletedTask;
    }
}

public class ObserverFactory : IGrainObserverFactory
{
    private readonly ConcurrentDictionary<Guid, ClassSubscriber> _observers = [];

    public (Guid, ClassSubscriber) CreateRandom()
    {
        var observerId = Guid.NewGuid();
        var observer = new ClassSubscriber();

        _observers.TryAdd(observerId, observer);

        return (observerId, observer);
    }

    public bool TryCreateObserver(Guid observerId, [NotNullWhen(true)] out IGrainObserver observer)
    {
        if (_observers.TryGetValue(observerId, out var value))
        {
            Console.WriteLine($"{nameof(ObserverFactory)}: found object");
            observer = value;
            return true;
        }

        Console.WriteLine($"{nameof(ObserverFactory)}: did not found object");
        observer = null;
        return false;
    }
}