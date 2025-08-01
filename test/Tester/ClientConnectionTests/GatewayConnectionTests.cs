using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime.Messaging;

namespace Tester
{
    public class TestGatewayManager : IGatewayListProvider
    {
        public TimeSpan MaxStaleness => TimeSpan.FromSeconds(1);

        public bool IsUpdatable => true;

        public IList<Uri> Gateways { get; }

        public TestGatewayManager()
        {
            Gateways = new List<Uri>();
        }

        public Task InitializeGatewayListProvider()
        {
            return Task.CompletedTask;
        }

        public Task<IList<Uri>> GetGateways()
        {
            return Task.FromResult(Gateways);
        }
    }

    /// <summary>
    /// Tests for gateway connection handling including reconnection behavior and cluster mismatch scenarios.
    /// </summary>
    public class GatewayConnectionTests : TestClusterPerTest
    {
        private OutsideRuntimeClient runtimeClient;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.UseTestClusterMembership = false;
            builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.UseLocalhostClustering();
                });

                hostBuilder.ConfigureServices((context, services) =>
                {
                    var cfg = context.Configuration;
                    var siloPort = int.Parse(cfg[nameof(TestClusterOptions.BaseSiloPort)]);
                    var gatewayPort = int.Parse(cfg[nameof(TestClusterOptions.BaseGatewayPort)]);
                    services.Configure<EndpointOptions>(options =>
                    {
                        options.SiloPort = siloPort;
                        options.GatewayPort = gatewayPort;
                    });
                });
            }
        }

        public class ClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                var basePort = int.Parse(configuration[nameof(TestClusterOptions.BaseGatewayPort)]);
                var primaryGw = new IPEndPoint(IPAddress.Loopback, basePort).ToGatewayUri();
                clientBuilder.Configure<GatewayOptions>(options =>
                {
                    options.GatewayListRefreshPeriod = TimeSpan.FromMilliseconds(100);
                });
                clientBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(sp =>
                    {
                        var gateway = new TestGatewayManager();
                        gateway.Gateways.Add(primaryGw);
                        return gateway;
                    });
                    services.AddFromExisting<IGatewayListProvider, TestGatewayManager>();
                });
            }
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            this.runtimeClient = this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
        }

        [Fact, TestCategory("Functional")]
        public async Task NoReconnectionToGatewayNotReturnedByManager()
        {
            // Reduce timeout for this test
            this.runtimeClient.SetResponseTimeout(TimeSpan.FromSeconds(1));

            var connectionCount = 0;
            var timeoutCount = 0;

            // Fake Gateway
            var gateways = await this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayListProvider>().GetGateways();
            var port = gateways.First().Port + 2;
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var evt = new SocketAsyncEventArgs();
            var gatewayManager = this.runtimeClient.ServiceProvider.GetService<TestGatewayManager>();
            evt.Completed += (sender, args) =>
            {
                connectionCount++;
                gatewayManager.Gateways.Remove(endpoint.ToGatewayUri());
            };

            // Add the fake gateway and wait the refresh from the client
            gatewayManager.Gateways.Add(endpoint.ToGatewayUri());
            await Task.Delay(200);

            using (var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                // Start the fake gw
                socket.Bind(endpoint);
                socket.Listen(1);
                socket.AcceptAsync(evt);

                // Make a bunch of calls
                for (var i = 0; i < 100; i++)
                {
                    try
                    {
                        var g = this.Client.GetGrain<ISimpleGrain>(i);
                        await g.SetA(i);
                    }
                    catch (TimeoutException)
                    {
                        timeoutCount++;
                    }
                }
                socket.Close();
            }

            // Check that we only connected once to the fake GW
            Assert.Equal(1, connectionCount);
            Assert.Equal(1, timeoutCount);
        }

        [Fact, TestCategory("Functional")]
        public async Task ConnectionFromDifferentClusterIsRejected()
        {
            // Arange
            var gateways = await this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayListProvider>().GetGateways();
            var gwEndpoint  = gateways.First().ToIPEndPoint();
            var exceptions = new List<Exception>();

            Task<bool> RetryFunc(Exception exception, CancellationToken cancellationToken)
            {
                Assert.IsType<ConnectionFailedException>(exception);
                exceptions.Add(exception);
                return Task.FromResult(false);
            }

            // Close current client connection
            await this.HostedCluster.StopClusterClientAsync();
            var hostBuilder = new HostBuilder().UseOrleansClient(
                (ctx, clientBuilder) =>
                {
                    clientBuilder.Configure<ClientMessagingOptions>(
                        options => { options.ResponseTimeoutWithDebugger = TimeSpan.FromSeconds(10); });
                    clientBuilder.Configure<ClusterOptions>(
                        options =>
                        {
                            options.ClusterId = "myClusterId";
                        })
                        .UseStaticClustering(gwEndpoint)
                        .UseConnectionRetryFilter(RetryFunc);
                    ;
                });
            var host = hostBuilder.Build();
            var exception = await Assert.ThrowsAsync<ConnectionFailedException>(async () => await host.StartAsync());
            Assert.Contains("Unable to connect to", exception.Message);
            await host.StopAsync();
        }
    }
}