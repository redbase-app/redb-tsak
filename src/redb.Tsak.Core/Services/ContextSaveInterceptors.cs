using redb.Core.Interception;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services;

/// <summary>
/// The <see cref="ITsakSaveInterceptors"/> of one route context: subscriptions to the host's
/// <see cref="IRedbSaveInterceptorRegistry"/> that the context manager removes, all of them, with the context.
/// </summary>
internal sealed class ContextSaveInterceptors : ITsakSaveInterceptors, IDisposable
{
    private readonly IRedbSaveInterceptorRegistry _registry;
    private readonly string _contextName;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private bool _disposed;

    internal ContextSaveInterceptors(IRedbSaveInterceptorRegistry registry, string contextName)
    {
        _registry = registry;
        _contextName = contextName;
    }

    /// <summary>Subscriptions still in place (for tests and diagnostics).</summary>
    internal int Count
    {
        get { lock (_gate) return _subscriptions.Count; }
    }

    /// <inheritdoc />
    public IDisposable Add(IRedbSaveInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, $"save interceptors of context '{_contextName}'");
            var subscription = new Subscription(this, _registry.Add(interceptor));
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    /// <summary>Removes every interceptor of the context; called when the context is removed.</summary>
    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
            subscription.Release();
    }

    private void Forget(Subscription subscription)
    {
        lock (_gate) _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(ContextSaveInterceptors owner, IDisposable registration) : IDisposable
    {
        private IDisposable? _registration = registration;

        // The registry handle drops its reference to the interceptor, and this one drops the handle.
        internal void Release() => Interlocked.Exchange(ref _registration, null)?.Dispose();

        public void Dispose()
        {
            Release();
            owner.Forget(this);
        }
    }
}
