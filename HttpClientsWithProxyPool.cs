using Microsoft.Extensions.Logging;
using System.Reactive.Disposables;

namespace HttpClientsWithProxy;

/// <summary>
/// Provides <see cref="HttpClient"/>s pool for proxy-per-request operations. The instance of 
/// this class should be disposed properly. This class is designed to be singleton, however, there
/// is no known limitation on the number of instances.
/// </summary>
/// <param name="maxNumberOfHttpClients">
/// Maximum number of <see cref="HttpClient"/>s in pool. Default value is 16.
/// This value is actually displays a max degree of parallelism of requests.
/// </param>
/// <param name="configureHttpHandler">
/// Action applied to every <see cref="HttpClientHandler"/> created.
/// </param>
/// <param name="configureHttpClient">
/// Action applied to every <see cref="HttpClient"/> created.
/// </param>
/// <param name="logger"></param>
public class HttpClientsWithProxyPool(
	in ushort? maxNumberOfHttpClients = null,
	in Action<HttpClientHandler>? configureHttpHandler = null,
	in Action<HttpClient>? configureHttpClient = null,
	in ILogger<HttpClientsWithProxyPool>? logger = null
) : IDisposable
{
	protected readonly ushort _maxNumberOfHttpClients = maxNumberOfHttpClients ?? 16;
	protected Action<HttpClientHandler>? _configureHttpHandler = configureHttpHandler;
	protected Action<HttpClient>? _configureHttpClient = configureHttpClient;
	protected ILogger<HttpClientsWithProxyPool>? _logger = logger;
	public ushort MaxNumberOfHttpClients => _maxNumberOfHttpClients;

	protected class HttpClientWithProxy : IDisposable
	{
		public readonly HttpClient HttpClient;
		public readonly HttpClientHandler HttpClientHandler;
		public readonly DynamicWebProxy DynamicWebProxy;
		public int LockCount;

		public HttpClientWithProxy(HttpClient httpClient, HttpClientHandler httpClientHandler,
			DynamicWebProxy dynamicWebProxy, int lockCount = 0)
		{
			HttpClient = httpClient;
			HttpClientHandler = httpClientHandler;
			DynamicWebProxy = dynamicWebProxy;
			LockCount = lockCount;
		}

		public void Dispose()
		{
			HttpClientHandler.Dispose();
			HttpClient.Dispose();
		}
	}

	protected readonly List<HttpClientWithProxy> _httpClients = new(maxNumberOfHttpClients ?? 16);
	protected virtual HttpClient CreateLockedHttpClient()
	{
		var proxy = new DynamicWebProxy();
		var handler = new HttpClientHandler()
		{
			Proxy = proxy,
			UseProxy = true,
			//ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
		};
		_configureHttpHandler?.Invoke(handler);
		var client = new HttpClient(handler);
		_configureHttpClient?.Invoke(client);
		_httpClients.Add(new(client, handler, proxy, 1));
		return client;
	}

	protected readonly SemaphoreSlim _availableHttpClientsCountdown = 
		new(maxNumberOfHttpClients ?? 16, maxNumberOfHttpClients ?? 16);
	protected readonly SemaphoreSlim _creatingNewHttpClientLock = new(1, 1);

	/// <summary>
	///  Acquires <see cref="HttpClient"/> from pool after setting <paramref name="proxyUrl"/> to it.<br/>
	///  Returned <see cref="IDisposable"/> Lock properly should be disposed after client usage (see example).<br/> 
	///  Returned <see cref="HttpClient"/> HttpClient should not be disposed manually,
	///  instead, dispose the instance of <see cref="HttpClientsWithProxyPool"/>.<br/>
	/// </summary>
	/// <remarks>
	/// Example:
	/// <code>
	///  var lockAndClient = await httpClientsWithProxyPool
	///    .AcquireHttpClient(proxyUri, ct);
	///  using(lockAndClient.Lock) {
	///    lockAndClient.HttpClient.Get(...
	///    ...
	///  }
	/// </code>
	/// </remarks>
	public virtual async Task<(IDisposable Lock, HttpClient HttpClient)> AcquireHttpClient(
		Uri proxyUrl, CancellationToken cancellationToken = default)
	{
		await _availableHttpClientsCountdown.WaitAsync(cancellationToken);

		var chosenClientIndex = -1;

		for (int i = 0; i < _httpClients.Count; i++)
			if (Interlocked.CompareExchange(ref _httpClients[i].LockCount, 1, 0) == 0)
			{
				chosenClientIndex = i;
				break;
			}

		if (chosenClientIndex == -1)
		{
			await _creatingNewHttpClientLock.WaitAsync(cancellationToken);
			using var _ = Disposable.Create(() => _creatingNewHttpClientLock.Release());
			CreateLockedHttpClient();
			chosenClientIndex = _httpClients.Count - 1;
			_logger?.LogInformation($"Created {nameof(HttpClient)}:{_httpClients[chosenClientIndex].GetHashCode() / 100_000}. Current count: {_httpClients.Count}.");
		}

		var chosenClient = _httpClients[chosenClientIndex];
		chosenClient.DynamicWebProxy.SetProxy(proxyUrl);
		_logger?.LogInformation($"Dedicated {nameof(HttpClient)}:{chosenClient.GetHashCode()/100_000} for {proxyUrl}.");
		return (Disposable.Create(() =>
		{
			Volatile.Write(ref chosenClient.LockCount, 0);
			_availableHttpClientsCountdown.Release();
			_logger?.LogInformation($"Released {nameof(HttpClient)}:{chosenClient.GetHashCode() / 100_000} from {proxyUrl}.");
		}), chosenClient.HttpClient);
	}

	public void Dispose()
	{
		foreach (var client in _httpClients)
			client.Dispose();
	}
}
