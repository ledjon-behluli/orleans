using CApp.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information))
    .UseOrleans(builder => builder.UseLocalhostClustering())
    .UseConsoleLifetime()
    .Build();

await host.StartAsync();

Console.ReadKey();

public class PublisherGrain : Grain, ITestGrain
{
    IGrainTimer _timer;
    ITestGrainObserver _observer;

    public Task Subscribe(ITestGrainObserver observer)
    {
        _observer = observer;
        return Task.CompletedTask;
    }

    public Task Start()
    {
        _timer = null;
        _timer = this.RegisterGrainTimer(async () =>
        {
            try
            {
                await _observer.Receive("ping");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            Console.WriteLine("Published message to subscribers");

        }, new GrainTimerCreationOptions()
        {
            DueTime = TimeSpan.Zero,
            Period = TimeSpan.FromSeconds(5)
        });

        return Task.CompletedTask;
    }
}