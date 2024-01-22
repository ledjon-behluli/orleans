using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Messaging;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Snapshot of current runtime statistics for a silo
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class SiloRuntimeStatistics
    {
        /// <summary>
        /// Total number of activations in a silo.
        /// </summary>
        [Id(0)]
        public int ActivationCount { get; }

        /// <summary>
        /// Number of activations in a silo that have been recently used.
        /// </summary>
        [Id(1)]
        public int RecentlyUsedActivationCount { get; }

        /// <summary>
        /// Gets the system CPU usage.
        /// </summary>
        /// <remarks>Ranges from 0.0-100.0.</remarks>
        [Id(2)]
        public float? CpuUsagePercentage { get; }

        /// <summary>
        /// Gets the currently available memory for allocations to the process.
        /// </summary>
        /// <remarks>
        /// Includes the currently available memory of the process, and the system.
        /// </remarks>
        [Id(3)]
        public float? AvailableMemoryBytes { get; }

        /// <summary>
        /// Gets the currently occupied memory by the process.
        /// </summary>
        /// <remarks>Includes fragmented memory.</remarks>
        [Id(4)]
        public long? MemoryUsageBytes { get; }

        /// <summary>
        /// Gets the maximum possible memory of the system.
        /// </summary>
        /// <remarks>Represents the physical memory, unless a lower-bound (typically in containers) has been specified.</remarks>
        [Id(5)]
        public long? MaximumAvailableMemoryBytes { get; }

        /// <summary>
        /// Is this silo overloaded.
        /// </summary>
        [Id(6)]
        public bool IsOverloaded { get; }

        /// <summary>
        /// The number of clients currently connected to that silo.
        /// </summary>
        [Id(7)]
        public long ClientCount { get; }

        [Id(8)]
        public long ReceivedMessages { get; }

        [Id(9)]
        public long SentMessages { get; }

        /// <summary>
        /// The DateTime when this statistics was created.
        /// </summary>
        [Id(10)]
        public DateTime DateTime { get; }

        internal SiloRuntimeStatistics(
            int activationCount,
            int recentlyUsedActivationCount,
            IEnvironmentStatistics environmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            DateTime dateTime)
        {
            ActivationCount = activationCount;
            RecentlyUsedActivationCount = recentlyUsedActivationCount;
            CpuUsagePercentage = environmentStatistics.CpuUsagePercentage;
            AvailableMemoryBytes = environmentStatistics.AvailableMemoryBytes;
            MemoryUsageBytes = environmentStatistics.MemoryUsageBytes;
            IsOverloaded = loadSheddingOptions.Value.LoadSheddingEnabled && OverloadDetectionLogic.IsOverloaded(environmentStatistics, loadSheddingOptions.Value);
            ClientCount = SiloRuntimeMetricsListener.ConnectedClientCount;
            MaximumAvailableMemoryBytes = environmentStatistics.MaximumAvailableMemoryBytes;
            ReceivedMessages = SiloRuntimeMetricsListener.MessageReceivedTotal;
            SentMessages = SiloRuntimeMetricsListener.MessageSentTotal;
            DateTime = dateTime;
        }

        public override string ToString() => @$"SiloRuntimeStatistics: ActivationCount={ActivationCount} RecentlyUsedActivationCount={RecentlyUsedActivationCount
            } CpuUsage={CpuUsagePercentage?.ToString() ?? "<unset>"} AvailableMemory={AvailableMemoryBytes} MemoryUsage={MemoryUsageBytes} IsOverloaded={IsOverloaded
            } ClientCount={ClientCount} MaximumAvailableMemory={MaximumAvailableMemoryBytes} DateTime={DateTime}";
    }

    /// <summary>
    /// Simple snapshot of current statistics for a given grain type on a given silo.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class SimpleGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this SimpleGrainStatistic.
        /// </summary>
        [Id(0)]
        public string GrainType { get; init; }

        /// <summary>
        /// The silo address for this SimpleGrainStatistic.
        /// </summary>
        [Id(1)]
        public SiloAddress SiloAddress { get; init; }

        /// <summary>
        /// The number of activations of this grain type on this given silo.
        /// </summary>
        [Id(2)]
        public int ActivationCount { get; init; }

        /// <summary>
        /// Returns the string representation of this SimpleGrainStatistic.
        /// </summary>
        public override string ToString() => $"SimpleGrainStatistic: GrainType={GrainType} Silo={SiloAddress} NumActivations={ActivationCount} ";
    }

    [Serializable, GenerateSerializer, Immutable]
    public sealed class DetailedGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this DetailedGrainStatistic.
        /// </summary>
        [Id(0)]
        public string GrainType { get; init; }

        /// <summary>
        /// The silo address for this DetailedGrainStatistic.
        /// </summary>
        [Id(1)]
        public SiloAddress SiloAddress { get; init; }

        /// <summary>
        /// Unique Id for the grain.
        /// </summary>
        [Id(2)]
        public GrainId GrainId { get; init; }
    }

    [Serializable, GenerateSerializer, Immutable]
    internal sealed class DetailedGrainReport
    {
        [Id(0)]
        public GrainId Grain { get; init; }

        /// <summary>silo on which these statistics come from</summary>
        [Id(1)]
        public SiloAddress SiloAddress { get; init; }

        /// <summary>silo on which these statistics come from</summary>
        [Id(2)]
        public string SiloName { get; init; }

        /// <summary>activation addresses in the local directory cache</summary>
        [Id(3)]
        public GrainAddress LocalCacheActivationAddress { get; init; }

        /// <summary>activation addresses in the local directory.</summary>
        [Id(4)]
        public GrainAddress LocalDirectoryActivationAddress { get; init; }

        /// <summary>primary silo for this grain</summary>
        [Id(5)]
        public SiloAddress PrimaryForGrain { get; init; }

        /// <summary>the name of the class that implements this grain.</summary>
        [Id(6)]
        public string GrainClassTypeName { get; init; }

        /// <summary>activation on this silo</summary>
        [Id(7)]
        public string LocalActivation { get; init; }

        public override string ToString() => @$"{Environment.NewLine
            }**DetailedGrainReport for grain {Grain} from silo {SiloName} SiloAddress={SiloAddress}{Environment.NewLine
            }   LocalCacheActivationAddresses={LocalCacheActivationAddress}{Environment.NewLine
            }   LocalDirectoryActivationAddresses={LocalDirectoryActivationAddress}{Environment.NewLine
            }   PrimaryForGrain={PrimaryForGrain}{Environment.NewLine
            }   GrainClassTypeName={GrainClassTypeName}{Environment.NewLine
            }   LocalActivation:{Environment.NewLine
            }{LocalActivation}.{Environment.NewLine}";
    }
}
