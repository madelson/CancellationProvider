# CancellationProvider [![NuGet Status](http://img.shields.io/nuget/v/CancellationProvider.svg?style=flat)](https://www.nuget.org/packages/CancellationProvider/)

CancellationProvider is a tiny .NET library to facilitate the flow of `CancellationToken` through your code.

More and more .NET APIs are cancelable, which is great for writing robust, hang-free code. However, passing an instance of `CancellationToken` down through the call stack to reach all of these cancelable method calls is rather clunky. `CancellationProvider` solves this by using dependency injection to flow the token, thereby allowing services that call cancelable APIs to consume it without other services being aware:

```
// in Program.cs (ASP.NET Core app)
builder.Services.AddScoped(p => new CancellationProvider(p.GetRequiredService<IHttpContextAccessor>().HttpContext?.RequestAborted));

// OR in Program.cs (Console app)
using CancellationTokenSource source = new();
Console.CancelKeyPress += (o, e) =>
{
    source.Cancel();
    e.Cancel = true; // Prevent the process from terminating immediately
};
services.AddSingleton(new CancellationProvider(source.Token));

// in MyService.cs
class MyService(CancellationProvider cancellationProvider)
{
    public async Task DoSomethingAsync()
    {
        ...
        await command.ExecuteNonQueryAsync(cancellationProvider.Token); // use the token for cancelable operations
        ...
    }
}
```

## Scopes

Sometimes, you might still want more fine-grained control over cancellation, for example setting a timeout for a particular operation or eschewing cancellation during a critical step. `CancellationProvider` offers a scoping mechanism to customize the provider's behavior in logical regions of code:

```
class MyService(CancellationProvider cancellationProvider, OtherService otherService)
{
    public async Task DoStuff()
    {
      // run some code with a strict timeout. Assumes OtherService and/or its dependencies are leveraging CancellationProvider
      using CancellationTokenSource timeoutSource = new(TimeSpan.FromSeconds(5));
      using (cancellationProvider.BeginScope(timeoutSource.Token))
      {
        await otherService.DoSomethingAsync();
      }

      // avoid canceling a critical operation. Assumes OtherService and/or its dependencies are leveraging CancellationProvider
      using (cancellationProvider.BeginCleanScope())
      {
        await otherService.DoSomethingAsync();
      }
    }
}
```

Scopes are also useful when you have singleton services that want to use the cancellation provider in a web application. For example in an ASP.NET Core app you could do this:
```
// register a singleton CancellationProvider linked to the host lifetime
builder.Services.AddSingleton(p => new CancellationProvider(p.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));

app.Use(async (context, next) =>
{
    // on each request, enter a scope tied to the request's cancellation token
    using var scope = context.RequestServices.GetRequiredService<CancellationProvider>().BeginScope(context.RequestAborted);
    await next();
});
```

## Release notes
- 1.0.0
  - Initial release



