using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.ClientObservers;

#nullable enable

internal sealed class GrainObserverFactory : IGrainObserverFactory
{
    public bool TryCreateObserver(Guid observerId, [NotNullWhen(true)] out IGrainObserver? observer)
    {
        observer = null;
        return false;
    }
}
