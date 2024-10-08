using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans;

#nullable enable

public interface IGrainObserverFactory
{
    bool TryCreateObserver(Guid observerId, [NotNullWhen(true)] out IGrainObserver? observer); 
}
