
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

var host = Host.CreateDefaultBuilder(args)
    .UseOrleans(builder => builder
        .UseLocalhostClustering())
    .ConfigureLogging(builder => builder.AddConsole())
    .Build();

await host.StartAsync();

var observer = new QueueObserver();

var obsreverRef = host.Services
    .GetRequiredService<IGrainFactory>()
    .CreateObjectReference<IQueueObserver>(observer);

await foreach (var job in obsreverRef.GetJobsAsync())
{
    Console.WriteLine("Read job with Id: " + job.Id);
}

Console.WriteLine("Reader finished");
Console.ReadKey();


public interface IQueueObserver : IGrainObserver
{
    IAsyncEnumerable<QueueJob> GetJobsAsync();
}


public class QueueObserver : IQueueObserver
{
    private readonly Channel<QueueJob> _jobs = Channel.CreateUnbounded<QueueJob>();

    public QueueObserver()
    {
        for (var i = 1; i < 10; i++)
        {
            _jobs.Writer.TryWrite(new(i.ToString()));
        }

        _jobs.Writer.TryComplete();
    }

    public async IAsyncEnumerable<QueueJob> GetJobsAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            await Task.Yield();
            yield return job;
        }

        Console.WriteLine("GetAsyncEnumerator finished");
    }
}


[GenerateSerializer]
public record QueueJob(string Id);
