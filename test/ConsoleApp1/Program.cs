using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Runtime;

var host = Host.CreateDefaultBuilder(args)
    .UseOrleans(builder => builder
        .UseLocalhostClustering()
        .AddAzureTableGrainStorageAsDefault(options =>
        {
            options.TableServiceClient = new Azure.Data.Tables.TableServiceClient("UseDevelopmentStorage=true");
        }))
    .ConfigureLogging(builder => builder.AddConsole())
    .Build();

await host.StartAsync();

var grain = host.Services
    .GetRequiredService<IGrainFactory>()
    .GetGrain<ITestGrain>("test");

//await grain.WriteBothStates();

_ = await grain.GetBothStates();
_ = await grain.GetBothLoadedStates();

await host.StopAsync();

public interface ITestGrain : IGrainWithStringKey
{
    Task WriteBothStates();
    Task<(int, int)> GetBothStates();
    Task<(int, int)> GetBothLoadedStates();
}

public class TestGrain(
    [PersistentState("state1")] IPersistentState<TestState> state1,
     [PersistentState("state2", loadStateAutomatically: false)] IPersistentState<TestState> state2)
        : Grain, ITestGrain
{
    public async Task<(int, int)> GetBothLoadedStates()
    {
        if (!state2.RecordExists)
        {
            await Console.Out.WriteLineAsync("State 2 was never read");
            await state2.ReadStateAsync();
        }
        else
        {
            await Console.Out.WriteLineAsync("State 2 has already been read");
        }

        return (state1.State.Prop, state2.State.Prop);
    }

    public async Task WriteBothStates()
    {
        state1.State = new(1);
        state2.State = new(1);

        await state1.WriteStateAsync();
        await state2.WriteStateAsync();
    }

    public Task<(int, int)> GetBothStates() =>
        Task.FromResult((state1.State.Prop, state2.State.Prop));

}

[GenerateSerializer]
public record TestState(int Prop);