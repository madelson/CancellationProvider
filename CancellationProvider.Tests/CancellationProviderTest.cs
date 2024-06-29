namespace Medallion.Threading.Tests;

public class CancellationProviderTest
{
    [Fact]
    public void DefaultProviderUsesNoneToken()
    {
        Assert.Equal(CancellationToken.None, CancellationProvider.Default.Token);
    }

    [Fact]
    public void ProviderUsesTokenFromConstructor()
    {
        using CancellationTokenSource source = new();
        CancellationProvider provider = new(source.Token);
        Assert.Equal(source.Token, provider.Token);
    }

    [Fact]
    public void ScopeTokenRemainsLiveAfterDisposal()
    {
        using CancellationTokenSource source = new();
        CancellationToken scopeToken;
        using(CancellationProvider.Default.BeginScope(source.Token))
        {
            scopeToken = CancellationProvider.Default.Token;
        }
        Assert.Equal(CancellationToken.None, CancellationProvider.Default.Token);
        Assert.Equal(source.Token, scopeToken);
    }

    [Fact]
    public void ReturnsNoOpScopeWherePossible()
    {
        using CancellationTokenSource source = new();
        using CancellationTokenSource source2 = new();
        // clean scope on top of clean scope
        using var scope1 = CancellationProvider.Default.BeginCleanScope();
        // new scope with None tokens
        using var scope2 = new CancellationProvider(source.Token).BeginScope(CancellationToken.None);
        source.Cancel();
        // new scope on top of canceled scope
        using var scope3 = new CancellationProvider(source.Token).BeginScope(source2.Token);

        Assert.Same(scope1, scope2);
        Assert.Same(scope1, scope3);
    }

    [Fact]
    public void ScopeAvoidsCreatingLinkedSourceWherePossible()
    {
        // Case 1: default source with single live token
        using CancellationTokenSource source = new();
        using (CancellationProvider.Default.BeginScope(source.Token))
        {
            Assert.Equal(source.Token, CancellationProvider.Default.Token);
        }
        using (CancellationProvider.Default.BeginScope(CancellationToken.None, source.Token))
        {
            Assert.Equal(source.Token, CancellationProvider.Default.Token);
        }

        // Case 2: live source with clean + single live token
        CancellationProvider provider = new(source.Token);
        using CancellationTokenSource source2 = new();
        using (provider.BeginScope(source2.Token, clean: true))
        {
            Assert.Equal(source2.Token, provider.Token);
        }
        using (provider.BeginScope(CancellationToken.None, source2.Token, clean: true))
        {
            Assert.Equal(source2.Token, provider.Token);
        }

        // Case 3: live source with canceled token
        source2.Cancel();
        using (provider.BeginScope(source2.Token))
        {
            Assert.Equal(source2.Token, provider.Token);
        }
        using (provider.BeginScope(CancellationToken.None, source2.Token))
        {
            Assert.Equal(source2.Token, provider.Token);
        }
    }

    [Fact]
    public void NestedScopes()
    {
        using CancellationTokenSource source1 = new();
        using CancellationTokenSource source2 = new();
        using CancellationTokenSource source3 = new();
        CancellationProvider provider = new(source1.Token);

        using (provider.BeginScope(source2.Token))
        {
            using (provider.BeginScope(source3.Token))
            {
                var token = provider.Token;
                Assert.False(token.IsCancellationRequested);
                source2.Cancel();
                Assert.True(token.IsCancellationRequested);
            }

            Assert.True(provider.Token.IsCancellationRequested);
        }

        Assert.False(provider.Token.IsCancellationRequested);
    }

    [Fact]
    public void CleanScopeProtectsFromCancellation()
    {
        using CancellationTokenSource source = new();
        CancellationProvider provider = new(source.Token);

        using (provider.BeginCleanScope())
        {
            source.Cancel();
            Assert.False(provider.Token.IsCancellationRequested);
        }

        Assert.True(provider.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task DisorderedDisposalDoesNotCorruptScopeStack()
    {
        using CancellationTokenSource source1 = new();
        using CancellationTokenSource source2 = new();
        using CancellationTokenSource source3 = new();
        CancellationProvider provider = new(default);

        using var threadScope1 = await Task.Run(() => provider.BeginScope(source1.Token));
        using var scope2 = provider.BeginScope(source2.Token, clean: true);
        using var scope3 = provider.BeginScope(source3.Token, clean: true);

        Assert.Equal(source3.Token, provider.Token);
        threadScope1.Dispose();
        Assert.Equal(source3.Token, provider.Token);
        scope2.Dispose();
        Assert.Equal(default, provider.Token);
    }
}