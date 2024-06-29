namespace Medallion.Threading;

public class CancellationProvider(CancellationToken token)
{
    private static CancellationTokenSource? _canceledSource;

    private AsyncLocal<Scope?>? _currentScope;

    public static CancellationProvider None { get; } = new(CancellationToken.None);

    public CancellationToken Token => _currentScope?.Value?.Token ?? token;

    public IDisposable BeginScope(CancellationToken cancellationToken = default, TimeSpan? timeout = null, bool clean = false) =>
        BeginScope(timeout ?? Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken, clean);

#if NET8_0_OR_GREATER
    public
#else
    private
#endif
    IDisposable BeginScope(TimeSpan timeout, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default, bool clean = false)
    {
        var current = LazyInitializer.EnsureInitialized(ref _currentScope, static () => new())!;
        var previousScope = current.Value;
        var currentToken = previousScope?.Token ?? token;

        // Simplify calculations below by setting clean to false if we're already in a clean scope
        if (!currentToken.CanBeCanceled) { clean = false; }
        else if (clean) { currentToken = CancellationToken.None; }

        // If we're already canceled and we're not creating a clean scope, this is a noop
        if (currentToken.IsCancellationRequested) { return NoOpScope.Instance; }

        // If we're not making any changes, return noop
        var hasTimeout = timeout != Timeout.InfiniteTimeSpan;
        var hasCancellationToken = cancellationToken.CanBeCanceled;
        if (!hasTimeout && !hasCancellationToken && !clean) { return NoOpScope.Instance; }

        Scope scope;
        // If we're immediately canceled, use the cached canceled token
        if (timeout == TimeSpan.Zero || cancellationToken.IsCancellationRequested)
        {
            scope = new(current, previousScope, CanceledToken);
        }
        // If only the new token is cancelable, use that directly
        else if (hasCancellationToken && !hasTimeout && !currentToken.CanBeCanceled)
        {
            scope = new(current, previousScope, cancellationToken);
        }
        // If we have a custom time provider then we need to build a dedicated CancellationTokenSource
        else if (hasTimeout && timeProvider != null && timeProvider != TimeProvider.System)
        {
            var source = Shims.CreateTimeoutSource(timeout, timeProvider);
            List<CancellationTokenRegistration>? registrations = null;
            Link(currentToken);
            Link(cancellationToken);
            scope = new(current, previousScope, source.Token, source, registrations);

            void Link(CancellationToken token)
            {
                if (!token.CanBeCanceled) { return; }

                (registrations ??= []).Add(token.Register(static o => ((CancellationTokenSource)o!).Cancel(), source));
            }
        }
        // General case
        else
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(currentToken, cancellationToken);
            if (hasTimeout) { source.CancelAfter(timeout); }
            scope = new(current, previousScope, source.Token, source);
        }

        return current.Value = scope;
    }

    private static CancellationToken CanceledToken
    {
        get
        {
            if (_canceledSource is { } source)
            {
                return source.Token;
            }

            CancellationTokenSource newSource = new();
            newSource.Cancel();
            
            source = Interlocked.CompareExchange(ref _canceledSource, newSource, null);
            if (source is null) { return newSource.Token; }
            newSource.Dispose();
            return source.Token;
        }
    }

    private sealed class Scope(
        AsyncLocal<Scope?> current,
        Scope? previous, 
        CancellationToken token,
        CancellationTokenSource? source = null,
        List<CancellationTokenRegistration>? registrations = null) : IDisposable
    {
        public CancellationToken Token => token;

        public void Dispose()
        {
            if (current.Value == this)
            {
                current.Value = previous;
            }

            if (registrations != null)
            {
                foreach (var registration in registrations) { registration.Dispose(); }
                registrations.Clear();
            }

            source?.Dispose();
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        private NoOpScope() { }

        public void Dispose() { }
    }
}
