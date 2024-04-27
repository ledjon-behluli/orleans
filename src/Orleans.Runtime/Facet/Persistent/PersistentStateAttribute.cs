using System;

namespace Orleans.Runtime;

/// <summary>
/// Specifies options for the <see cref="IPersistentState{TState}"/> constructor argument which it is applied to.
/// </summary>
/// <seealso cref="Attribute" />
/// <seealso cref="IFacetMetadata" />
/// <seealso cref="IPersistentStateConfiguration" />
[AttributeUsage(AttributeTargets.Parameter)]
public class PersistentStateAttribute : Attribute, IFacetMetadata, IPersistentStateConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PersistentStateAttribute"/> class.
    /// </summary>
    /// <param name="stateName">Name of the state.</param>
    /// <param name="storageName">Name of the storage provider.</param>
    /// <param name="loadStateAutomatically">Wether to load the state automatically or not.</param>
    public PersistentStateAttribute(string stateName, string storageName = null, bool loadStateAutomatically = true)
    {
        ArgumentNullException.ThrowIfNull(stateName);

        StateName = stateName;
        StorageName = storageName;
        LoadStateAutomatically = loadStateAutomatically;
    }

    /// <inheritdoc/>
    public string StateName { get; }

    /// <inheritdoc/>
    public string StorageName { get; }

    /// <inheritdoc/>
    public bool LoadStateAutomatically { get; }
}