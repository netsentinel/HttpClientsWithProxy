# HttpClientsWithProxy
Provides `HttpClient`s pool for proxy-per-request operations.

# Quick start
### Install package
```
dotnet add package netsentinel.HttpClientsWithProxy
```

### Inject HttpClientsWithProxyPool
```csharp
builder.Services.AddSingleton<HttpClientsWithProxyPool>(sp => new(
  maxNumberOfHttpClients: 16,
  configureHttpHandler: httpClientHandler => { },
  configureHttpClient: httpClient => { },
  logger: sp.GetRequiredService<ILogger<HttpClientsWithProxyPool>>()
));
```

### Acquire and use HttpClient
```csharp
var lockAndClient = await _httpClientsWithProxyPool.AcquireHttpClient(proxyUri, stoppingToken);
using (lockAndClient.Lock) {
  using var response = await lockAndClient.HttpClient.GetAsync(requestUrl);
  // your code goes here
}
```

### Parallelisation example
```csharp
await Parallel.ForEachAsync(proxyUris.SelectMany(
    proxyUri => requestUrls.Select(requestUrl => (proxyUri, requestUrl))),
  async (proxyAndUrl, ct) =>
  {
    var lockAndClient = await _clientsCache.AcquireHttpClient(proxyAndUrl.proxyUri, ct);
    using (lockAndClient.Lock) {
      using var response = await lockAndClient.HttpClient.GetAsync(requestUrl);
      // your code goes here
    }
});
```
