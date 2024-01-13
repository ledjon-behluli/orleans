using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration.Options;

namespace Orleans.Runtime.Placement;

// See: https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/
internal sealed class ResourceOptimizedPlacementDirector : IPlacementDirector, ISiloStatisticsChangeListener
{
    /// <summary>
    /// 1 / (1024 * 1024)
    /// </summary>
    private const float PhysicalMemoryScalingFactor = 0.00000095367431640625f;
    private const int FourKiloByte = 4096;

    private readonly NormalizedWeights _weights;
    private readonly float _localSiloPreferenceMargin;
    private readonly ConcurrentDictionary<SiloAddress, FilteredSiloStatistics> _siloStatistics = [];

    private Task<SiloAddress> _cachedLocalSilo;

    public ResourceOptimizedPlacementDirector(
        DeploymentLoadPublisher deploymentLoadPublisher,
        IOptions<ResourceOptimizedPlacementOptions> options)
    {
        _weights = NormalizeWeights(options.Value);
        _localSiloPreferenceMargin = options.Value.LocalSiloPreferenceMargin;
        deploymentLoadPublisher.SubscribeToStatisticsChangeEvents(this);
    }

    private static NormalizedWeights NormalizeWeights(ResourceOptimizedPlacementOptions input)
    {
        var totalWeight = input.CpuUsageWeight + input.MemoryUsageWeight + input.PhysicalMemoryWeight + input.AvailableMemoryWeight;
    
        return new (
            CpuUsageWeight: input.CpuUsageWeight / totalWeight,
            MemoryUsageWeight: input.MemoryUsageWeight / totalWeight,
            PhysicalMemoryWeight: input.PhysicalMemoryWeight / totalWeight,
            AvailableMemoryWeight: input.AvailableMemoryWeight / totalWeight);
    }

    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatibleSilos = context.GetCompatibleSilos(target);

        if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
        {
            return Task.FromResult(placementHint);
        }

        if (compatibleSilos.Length == 0)
        {
            throw new SiloUnavailableException($"Cannot place grain with Id = [{target.GrainIdentity}], because there are no compatible silos.");
        }

        if (compatibleSilos.Length == 1)
        {
            return Task.FromResult(compatibleSilos[0]);
        }

        if (_siloStatistics.IsEmpty)
        {
            return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
        }

        var bestCandidate = GetBestSiloCandidate(compatibleSilos);
        if (IsLocalSiloPreferable(context, compatibleSilos, bestCandidate.Value))
        {
            return _cachedLocalSilo ??= Task.FromResult(context.LocalSilo);
        }

        return Task.FromResult(bestCandidate.Key);
    }

    private KeyValuePair<SiloAddress, float> GetBestSiloCandidate(SiloAddress[] compatibleSilos)
    {
        (int Index, float Score) pick;
        int compatibleSilosCount = compatibleSilos.Length;

        // It is good practice not to allocate more than 1[KB] on the stack
        // but the size of (int, ResourceStatistics) = 64 in (64-bit architecture), by increasing
        // the limit to 4[KB] we can stackalloc for up to 4096 / 64 = 64 silos in a cluster.
        if (compatibleSilosCount * Unsafe.SizeOf<(int, ResourceStatistics)>() <= FourKiloByte)
        {
            pick = MakePick(stackalloc (int, ResourceStatistics)[compatibleSilosCount]);
        }
        else
        {
            var relevantSilos = ArrayPool<(int, ResourceStatistics)>.Shared.Rent(compatibleSilosCount);
            pick = MakePick(relevantSilos.AsSpan());
            ArrayPool<(int, ResourceStatistics)>.Shared.Return(relevantSilos);
        }

        return new KeyValuePair<SiloAddress, float>(compatibleSilos[pick.Index], pick.Score);

        (int, float) MakePick(Span<(int, ResourceStatistics)> relevantSilos)
        {
            // Get all compatible silos which aren't overloaded
            int relevantSilosCount = 0;
            for (var i = 0; i < compatibleSilos.Length; ++i)
            {
                var silo = compatibleSilos[i];
                if (_siloStatistics.TryGetValue(silo, out var stats))
                {
                    var filteredStats = stats.Value;
                    if (!filteredStats.IsOverloaded)
                    {
                        relevantSilos[relevantSilosCount++] = new(i, filteredStats);
                    }
                }
            }

            // Limit to the number of candidates added.
            relevantSilos = relevantSilos[0..relevantSilosCount];
            Debug.Assert(relevantSilos.Length == relevantSilosCount);

            // Pick K silos from the list of compatible silos, where K is equal to the square root of the number of silos.
            // Eg, from 10 silos, we choose from 4.
            int candidateCount = (int)Math.Ceiling(Math.Sqrt(relevantSilosCount));
            ShufflePrefix(relevantSilos, candidateCount);
            var candidates = relevantSilos[0..candidateCount];

            (int Index, float Score) pick = (0, 1f);

            foreach (var (index, statistics) in candidates)
            {
                float score = CalculateScore(statistics);

                // It's very unlikely, but there could be more than 1 silo that has the same score,
                // so we apply some jittering to avoid pick the first one in the short-list.
                float scoreJitter = Random.Shared.NextSingle() / 100_000f;

                if (score + scoreJitter < pick.Score)
                {
                    pick = (index, score);
                }
            }

            return pick;
        }

        // Variant of the Modern Fisher-Yates shuffle which stops after shuffling the first `prefixLength` elements,
        // which are the only elements we are interested in.
        // See: https://en.wikipedia.org/wiki/Fisher%E2%80%93Yates_shuffle
        static void ShufflePrefix(Span<(int SiloIndex, ResourceStatistics SiloStatistics)> values, int prefixLength)
        {
            Debug.Assert(prefixLength >= 0);
            Debug.Assert(prefixLength <= values.Length);
            var max = values.Length;
            for (var i = 0; i < prefixLength; i++)
            {
                var chosen = Random.Shared.Next(i, max);
                if (chosen != i)
                {
                    (values[chosen], values[i]) = (values[i], values[chosen]);
                }
            }
        }
    }

    private bool IsLocalSiloPreferable(IPlacementContext context, SiloAddress[] compatibleSilos, float bestCandidateScore)
    {
        if (context.LocalSiloStatus != SiloStatus.Active || !compatibleSilos.Contains(context.LocalSilo))
        {
            return false;
        }

        if (!_siloStatistics.TryGetValue(context.LocalSilo, out var local))
        {
            return false;
        }

        var statistics = local.Value;
        if (statistics.IsOverloaded)
        {
            return false;
        }

        var localSiloScore = CalculateScore(statistics);
        return localSiloScore - _localSiloPreferenceMargin <= bestCandidateScore;
    }

    /// <summary>
    /// Always returns a value [0-1]
    /// </summary>
    /// <returns>
    /// score = cpu_weight * (cpu_usage / 100) +
    ///         mem_usage_weight * (mem_usage / physical_mem) +
    ///         mem_avail_weight * [1 - (mem_avail / physical_mem)]
    ///         physical_mem_weight * (1 / (1024 * 1024 * physical_mem)
    /// </returns>
    /// <remarks>physical_mem is represented in [MB] to keep the result within [0-1] in cases of silos having physical_mem less than [1GB]</remarks>
    private float CalculateScore(ResourceStatistics stats)
    {
        float normalizedCpuUsage = stats.CpuUsage.HasValue ? stats.CpuUsage.Value / 100f : 0f;

        if (stats.TotalPhysicalMemory is { } physicalMemory && physicalMemory > 0)
        {
            float normalizedMemoryUsage = stats.MemoryUsage.HasValue ? stats.MemoryUsage.Value / physicalMemory : 0f;
            float normalizedAvailableMemory = 1 - (stats.AvailableMemory.HasValue ? stats.AvailableMemory.Value / physicalMemory : 0f);
            float normalizedPhysicalMemory = PhysicalMemoryScalingFactor * physicalMemory;

            return _weights.CpuUsageWeight * normalizedCpuUsage +
                   _weights.MemoryUsageWeight * normalizedMemoryUsage +
                   _weights.AvailableMemoryWeight * normalizedAvailableMemory +
                   _weights.PhysicalMemoryWeight * normalizedPhysicalMemory;
        }

        return _weights.CpuUsageWeight * normalizedCpuUsage;
    }

    public void RemoveSilo(SiloAddress address)
         => _siloStatistics.TryRemove(address, out _);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
        => _siloStatistics.AddOrUpdate(
            address,
            addValueFactory: static (_, statistics) => new(statistics),
            updateValueFactory: static (_, existing, statistics) =>
            {
                existing.Update(statistics);
                return existing;
            },
            statistics);

    private readonly record struct ResourceStatistics(float? CpuUsage, float? AvailableMemory, long? MemoryUsage, long? TotalPhysicalMemory, bool IsOverloaded);
    private readonly record struct NormalizedWeights(float CpuUsageWeight, float MemoryUsageWeight, float AvailableMemoryWeight, float PhysicalMemoryWeight);

    private sealed class FilteredSiloStatistics(SiloRuntimeStatistics statistics)
    {
        private readonly DualModeKalmanFilter _cpuUsageFilter = new();
        private readonly DualModeKalmanFilter _availableMemoryFilter = new();
        private readonly DualModeKalmanFilter _memoryUsageFilter = new();

        private float? _cpuUsage = statistics.CpuUsage;
        private float? _availableMemory = statistics.AvailableMemory;
        private long? _memoryUsage = statistics.MemoryUsage;
        private long? _totalPhysicalMemory = statistics.TotalPhysicalMemory;
        private bool _isOverloaded = statistics.IsOverloaded;

        public ResourceStatistics Value => new(_cpuUsage, _availableMemory, _memoryUsage, _totalPhysicalMemory, _isOverloaded);

        public void Update(SiloRuntimeStatistics statistics)
        {
            _cpuUsage = _cpuUsageFilter.Filter(statistics.CpuUsage);
            _availableMemory = _availableMemoryFilter.Filter(statistics.AvailableMemory);
            _memoryUsage = (long)_memoryUsageFilter.Filter((float)statistics.MemoryUsage);
            _totalPhysicalMemory = statistics.TotalPhysicalMemory;
            _isOverloaded = statistics.IsOverloaded;
        }
    }

    // The rationale behind using a dual-mode KF, is that we want the input signal to follow a trajectory that
    // decays with a slower rate than the original one, but also tracks the signal in case of signal increases
    // (which represent potential of overloading). Both are important, but they are inversely correlated to each other.
    private sealed class DualModeKalmanFilter
    {
        private const float SlowProcessNoiseCovariance = 0f;
        private const float FastProcessNoiseCovariance = 0.01f;

        private KalmanFilter _slowFilter = new();
        private KalmanFilter _fastFilter = new();

        private FilterRegime _regime = FilterRegime.Slow;

        private enum FilterRegime
        {
            Slow,
            Fast
        }

        public float Filter(float? measurement)
        {
            float _measurement = measurement ?? 0f;

            float slowEstimate = _slowFilter.Filter(_measurement, SlowProcessNoiseCovariance);
            float fastEstimate = _fastFilter.Filter(_measurement, FastProcessNoiseCovariance);

            if (_measurement > slowEstimate)
            {
                if (_regime == FilterRegime.Slow)
                {
                    _regime = FilterRegime.Fast;
                    _fastFilter.SetState(_measurement, 0f);
                    fastEstimate = _fastFilter.Filter(_measurement, FastProcessNoiseCovariance);
                }

                return fastEstimate;
            }
            else
            {
                if (_regime == FilterRegime.Fast)
                {
                    _regime = FilterRegime.Slow;
                    _slowFilter.SetState(_fastFilter.PriorEstimate, _fastFilter.PriorErrorCovariance);
                    slowEstimate = _slowFilter.Filter(_measurement, SlowProcessNoiseCovariance);
                }

                return slowEstimate;
            }
        }

        private struct KalmanFilter()
        {
            public float PriorEstimate { get; private set; } = 0f;
            public float PriorErrorCovariance { get; private set; } = 1f;

            public void SetState(float estimate, float errorCovariance)
            {
                PriorEstimate = estimate;
                PriorErrorCovariance = errorCovariance;
            }

            public float Filter(float measurement, float processNoiseCovariance)
            {
                float estimate = PriorEstimate;
                float errorCovariance = PriorErrorCovariance + processNoiseCovariance;

                float gain = errorCovariance / (errorCovariance + 1f);
                float newEstimate = estimate + gain * (measurement - estimate);
                float newErrorCovariance = (1f - gain) * errorCovariance;

                PriorEstimate = newEstimate;
                PriorErrorCovariance = newErrorCovariance;

                return newEstimate;
            }
        }
    }
}