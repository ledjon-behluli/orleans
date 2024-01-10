using System;
using Microsoft.Extensions.Options;

namespace Orleans.Runtime.Configuration.Options;

/// <summary>
/// Settings which regulate the placement of grains across a cluster when using <see cref="ResourceOptimizedPlacement"/>.
/// </summary>
public sealed class ResourceOptimizedPlacementOptions
{
    /// <summary>
    /// The importance of the CPU utilization by the silo.
    /// </summary>
    /// <remarks><i>Valid range of values are [0-1]</i></remarks>
    public float CpuUsageWeight { get; set; } = DEFAULT_CPU_USAGE_WEIGHT;
    /// <summary>
    /// The default value of <see cref="CpuUsageWeight"/>.
    /// </summary>
    public const float DEFAULT_CPU_USAGE_WEIGHT = 0.4f;

    /// <summary>
    /// The importance of the memory utilization by the silo.
    /// </summary>
    /// <remarks><i>Valid range of values are [0-1]</i></remarks>
    public float MemoryUsageWeight { get; set; } = DEFAULT_MEMORY_USAGE_WEIGHT;
    /// <summary>
    /// The default value of <see cref="MemoryUsageWeight"/>.
    /// </summary>
    public const float DEFAULT_MEMORY_USAGE_WEIGHT = 0.4f;

    /// <summary>
    /// The importance of the available memory to the silo.
    /// </summary>
    /// <remarks><i>Valid range of values are [0-1]</i></remarks>
    public float AvailableMemoryWeight { get; set; } = DEFAULT_AVAILABLE_MEMORY_WEIGHT;
    /// <summary>
    /// The default value of <see cref="AvailableMemoryWeight"/>.
    /// </summary>
    public const float DEFAULT_AVAILABLE_MEMORY_WEIGHT = 0.1f;

    /// <summary>
    /// The specified margin for which: if two silos (one of them being the local to the current pending activation), have a utilization score that should be considered "the same" within this margin.
    /// <list type="bullet">
    /// <item>When this value is 0, then the policy will always favor the silo with the higher utilization score, even if that silo is remote to the current pending activation.</item>
    /// <item>When this value is 100, then the policy will always favor the local silo, regardless of its relative utilization score. This policy essentially becomes equivalent to <see cref="PreferLocalPlacement"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks><i>Valid range of values are [0-1]</i></remarks>
    public float LocalSiloPreferenceMargin { get; set; }
    /// <summary>
    /// The default value of <see cref="LocalSiloPreferenceMargin"/>.
    /// </summary>
    public const float DEFAULT_LOCAL_SILO_PREFERENCE_MARGIN = 0.05f;
}

internal sealed class ResourceOptimizedPlacementOptionsValidator
    (IOptions<ResourceOptimizedPlacementOptions> options) : IConfigurationValidator
{
    private readonly ResourceOptimizedPlacementOptions _options = options.Value;

    public void ValidateConfiguration()
    {
        if (_options.CpuUsageWeight < 0f || _options.CpuUsageWeight > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.CpuUsageWeight));
        }

        if (_options.MemoryUsageWeight < 0f || _options.MemoryUsageWeight > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.MemoryUsageWeight));
        }

        if (_options.AvailableMemoryWeight < 0f || _options.AvailableMemoryWeight > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.AvailableMemoryWeight));
        }

        if (_options.LocalSiloPreferenceMargin < 0f || _options.LocalSiloPreferenceMargin > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.LocalSiloPreferenceMargin));
        }

        if (_options.CpuUsageWeight + _options.MemoryUsageWeight + _options.AvailableMemoryWeight != 1f)
        {
            throw new OrleansConfigurationException(
                $"The total sum across all the weights of {nameof(ResourceOptimizedPlacementOptions)} must equal 1");
        }

        static void ThrowOutOfRange(string propertyName)
            => throw new OrleansConfigurationException($"{propertyName} must be inclusive between [0-1]");
    }
}