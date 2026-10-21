using redb.Core.Interception;

namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Save interceptors a module subscribes for the life of its route context. The host keeps the redb service and its
/// DI container to itself; a module reaches this service through its context instead:
/// <code>
/// public override async Task&lt;IRouteContext&gt; InitializeAsync(IRouteContext context)
/// {
///     context.GetService&lt;ITsakSaveInterceptors&gt;()?.Add(new NotifyFrontInterceptor());
///     ...
/// }
/// </code>
/// The context holds the subscription: when Tsak removes the context (the module is unloaded, or reloaded and its
/// context created anew) every interceptor it added is removed before the module's AssemblyLoadContext unloads, so
/// the module needs no cleanup of its own. Stopping the context keeps them, as it keeps the routes.
/// <para>
/// What an interceptor added here sees: every save and delete of the worker made through the host's redb service and
/// the named redb instances of the contexts, including those of other modules. An exception thrown from
/// <see cref="IRedbSaveInterceptor.SavingAsync"/> or <see cref="IRedbSaveInterceptor.DeletingAsync"/> therefore
/// cancels any save or delete of the worker, not only the module's own. The semantics are those of
/// <see cref="IRedbSaveInterceptor"/>: SavedAsync after the commit, inside the transaction within
/// <c>ExecuteAtomicAsync</c>.
/// </para>
/// <para>
/// The service is in the context only when the host runs redb (<c>Tsak:Redb:Provider</c> is set); without it
/// <c>GetService</c> returns null.
/// </para>
/// </summary>
public interface ITsakSaveInterceptors
{
    /// <summary>
    /// Subscribes <paramref name="interceptor"/> until the context is removed. Dispose the result to remove it
    /// earlier; disposing twice is safe. Fails once the context has been removed.
    /// </summary>
    IDisposable Add(IRedbSaveInterceptor interceptor);
}
