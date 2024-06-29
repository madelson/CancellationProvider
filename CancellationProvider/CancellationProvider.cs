namespace Medallion.Threading;

/// <summary>
/// Provides a mechanism for flowing <see cref="CancellationToken"/> through an application
/// via dependency injection instead of manually passing it as a parameter. 
/// 
/// Abstracts away the root source of the cancellation such as HttpContext.RequestAborted or a timeout.
/// 
/// Also supports modifying cancellation locally via logical scopes with <see cref="BeginScope(CancellationToken, CancellationToken, bool)"/>
/// </summary>
public sealed class CancellationProvider(CancellationToken token)
{
    private readonly CancellationToken _rootToken = token;
    private AsyncLocal<Scope?>? _currentScope;

    /// <summary>
    /// An instance of <see cref="CancellationProvider"/> constructed with <see cref="CancellationToken.None"/>.
    /// </summary>
    public static CancellationProvider Default { get; } = new(CancellationToken.None);

    /// <summary>
    /// The current <see cref="CancellationToken"/> from the provider. This may change when entering a new scope.
    /// </summary>
    public CancellationToken Token => _currentScope?.Value?.Token ?? _rootToken;

    /// <summary>
    /// Creates a logical scope which establishes a new local cancellation regime, modifying the value of <see cref="Token"/>.
    /// 
    /// This method creates a "clean" scope which ignores the <see cref="CancellationToken"/> of the parent scope.
    /// 
    /// Therefore, this is useful for creating a region of uncancelable code.
    /// </summary>
    /// <returns>An <see cref="IDisposable"/> used to close the scope.</returns>
    public IDisposable BeginCleanScope() => BeginScope(CancellationToken.None, clean: true);

    /// <summary>
    /// Creates a logical scope which establishes a new local cancellation regime, modifying the value of <see cref="Token"/>.
    /// 
    /// The new scope adds up to 2 <see cref="CancellationToken"/>s which can trigger cancellation. The <paramref name="clean"/>
    /// parameter determines whether or not the <see cref="CancellationToken"/> from the outer scope is respected in the inner
    /// scope.
    /// </summary>
    /// <returns>An <see cref="IDisposable"/> used to close the scope.</returns>
    public IDisposable BeginScope(CancellationToken token, CancellationToken token2 = default, bool clean = false)
    {
        var current = LazyInitializer.EnsureInitialized(ref _currentScope, static () => new())!;
        var previousScope = current.Value;
        var previousToken = previousScope?.Token ?? _rootToken;

        // Simplify calculations below by setting clean to false if we're already in a clean scope
        if (!previousToken.CanBeCanceled) { clean = false; }
        else if (clean) { previousToken = CancellationToken.None; }

        // If we're already canceled and we're not creating a clean scope, this is a noop
        if (previousToken.IsCancellationRequested) { return NoOpScope.Instance; }

        // If we're not making any changes, return noop
        if (!token.CanBeCanceled && !token2.CanBeCanceled && !clean) { return NoOpScope.Instance; }

        Scope scope;
        // If we have only one cancelable token among previousToken, token, and token2,
        // or if one of our new tokens is already canceled, use it directly
        if ((!previousToken.CanBeCanceled && !token2.CanBeCanceled) || token.IsCancellationRequested)
        {
            scope = new(current, previousScope, token);
        }
        else if ((!previousToken.CanBeCanceled && !token.CanBeCanceled) || token2.IsCancellationRequested)
        {
            scope = new(current, previousScope, token2);
        }
        // General case
        else
        {
            // Note that this source cannot safely be disposed because we want any token reference taken
            // from the provider to stay live even if the scope is disposed out from under it (consider
            // the case of starting a scope to kick off some async work). This shouldn't be too big of a
            // deal since we know this source won't have a timer, so the only unmanaged resource is the
            // WaitHandle which is rarely used. The source will still become eligible for GC once the scope does.
            var source = CancellationTokenSource.CreateLinkedTokenSource(previousToken, token, token2);
            scope = new(current, previousScope, source.Token);
        }

        return current.Value = scope;
    }

    private sealed class Scope(
        AsyncLocal<Scope?> current,
        Scope? previous, 
        CancellationToken token) : IDisposable
    {
        private readonly Scope? _previous = previous;

        public CancellationToken Token => token;

        public void Dispose()
        {
            // Scopes should be disposed in natural order (innermost first). However, if the user
            // messes that up we don't want to corrupt the provider. Therefore, we walk the scope tree
            // back to the root until we find this scope. If we find it then we restore the parent scope.
            // If not, we leave things unchanged.

            for (var scope = current.Value; scope != null; scope = scope._previous)
            {
                if (scope == this) 
                {
                    current.Value = _previous;
                    break;
                }
            }
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        private NoOpScope() { }

        public void Dispose() { }
    }
}
