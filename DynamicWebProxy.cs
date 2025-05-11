using System.Net;

namespace HttpClientsWithProxy;

public class DynamicWebProxy : IWebProxy
{
	public ICredentials? Credentials { get; set; }

	protected Uri? _proxyUri;
	public Uri? GetProxy(Uri destination) => _proxyUri;
	public void SetProxy(Uri proxyUri) => _proxyUri = proxyUri;
	public bool IsBypassed(Uri host) => false;
}
