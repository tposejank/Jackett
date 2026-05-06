using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using com.LandonKey.SocksWebProxy;
using com.LandonKey.SocksWebProxy.Proxy;
using Jackett.Common.Models.Config;
using Jackett.Common.Services.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Common.Utils.Clients
{
    public abstract class WebClient : IObserver<ServerConfig>
    {
        private static readonly Regex _RefreshHeaderRegex = new Regex("^(.*?url)=(.*?)(?:;|$)", RegexOptions.Compiled);

        protected IDisposable ServerConfigUnsubscriber;
        protected Logger logger;
        protected IConfigurationService configService;
        protected readonly ServerConfig serverConfig;
        protected IProcessService processService;
        protected static readonly HttpClient FlareSolverrClient = new HttpClient();
        protected DateTime lastRequest = DateTime.MinValue;
        protected TimeSpan requestDelayTimeSpan;
        protected string ClientType;
        protected int ClientTimeout = 100; // default timeout is 100 s
        public bool EmulateBrowser = true;

        protected static Dictionary<string, ICollection<string>> trustedCertificates = new Dictionary<string, ICollection<string>>();
        protected static string webProxyUrl;
        protected static IWebProxy webProxy;

        public void InitProxy(ServerConfig serverConfig)
        {
            // dispose old SocksWebProxy
            if (webProxy is SocksWebProxy proxy)
                proxy.Dispose();
            webProxy = null;

            webProxyUrl = serverConfig.GetProxyUrl();
            if (serverConfig.ProxyType == ProxyType.Disabled || string.IsNullOrWhiteSpace(webProxyUrl))
                return;
            if (serverConfig.ProxyType == ProxyType.Http)
            {
                NetworkCredential creds = null;
                if (!serverConfig.ProxyIsAnonymous)
                {
                    var username = serverConfig.ProxyUsername;
                    var password = serverConfig.ProxyPassword;
                    creds = new NetworkCredential(username, password);
                }
                webProxy = new WebProxy(serverConfig.GetProxyUrl(false)) // proxy URL without credentials
                {
                    BypassProxyOnLocal = false,
                    Credentials = creds
                };
            }
            else if (serverConfig.ProxyType == ProxyType.Socks4 || serverConfig.ProxyType == ProxyType.Socks5)
            {
                // in case of error in DNS resolution, we use a fake proxy to avoid leaking the user IP (disabling proxy)
                // https://github.com/Jackett/Jackett/issues/8826
                var addresses = new[] { IPAddress.Parse(serverConfig.LocalBindAddress) };
                try
                {
                    addresses = Dns.GetHostAddressesAsync(serverConfig.ProxyUrl).Result;
                }
                catch (Exception e)
                {
                    logger.Error($"Unable to resolve proxy URL: {serverConfig.ProxyUrl}. The proxy will not work properly.\n{e}");
                }
                var socksConfig = new ProxyConfig
                {
                    SocksAddress = addresses.FirstOrDefault(),
                    Username = serverConfig.ProxyUsername,
                    Password = serverConfig.ProxyPassword,
                    Version = serverConfig.ProxyType == ProxyType.Socks4 ?
                        ProxyConfig.SocksVersion.Four :
                        ProxyConfig.SocksVersion.Five
                };
                if (serverConfig.ProxyPort.HasValue)
                    socksConfig.SocksPort = serverConfig.ProxyPort.Value;
                webProxy = new SocksWebProxy(socksConfig, false);
            }
            else
                throw new Exception($"Proxy type '{serverConfig.ProxyType}' is not implemented!");
        }

        public double requestDelay
        {
            get => requestDelayTimeSpan.TotalSeconds;
            set => requestDelayTimeSpan = TimeSpan.FromSeconds(value);
        }

        protected virtual void OnConfigChange()
        {
        }

        public virtual void AddTrustedCertificate(string host, string hash)
        {
            hash = hash.ToUpper();
            trustedCertificates.TryGetValue(hash.ToUpper(), out var hosts);
            if (hosts == null)
            {
                hosts = new HashSet<string>();
                trustedCertificates[hash] = hosts;
            }
            hosts.Add(host);
        }

        public WebClient(IProcessService p, Logger l, IConfigurationService c, ServerConfig sc)
        {
            processService = p;
            logger = l;
            configService = c;
            serverConfig = sc;
            ClientType = GetType().Name;
            ServerConfigUnsubscriber = serverConfig.Subscribe(this);

            if (webProxyUrl == null)
                InitProxy(sc);
        }

        protected async Task DelayRequest(WebRequest request)
        {
            if (request.EmulateBrowser == null)
                request.EmulateBrowser = EmulateBrowser;

            if (requestDelay != 0)
            {
                var timeElapsed = DateTime.Now - lastRequest;
                if (timeElapsed < requestDelayTimeSpan)
                {
                    var delay = requestDelayTimeSpan - timeElapsed;
                    logger.Debug(string.Format("WebClient({0}): delaying request for {1} by {2} seconds", ClientType, request.Url, delay.TotalSeconds.ToString()));
                    await Task.Delay(delay);
                }
            }
        }

        protected virtual void PrepareRequest(WebRequest request)
        {
            // add Accept/Accept-Language header if not set
            // some webservers won't accept requests without accept
            // e.g. elittracker requieres the Accept-Language header
            if (request.Headers == null)
                request.Headers = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            var hasAccept = false;
            var hasAcceptLanguage = false;
            foreach (var header in request.Headers)
            {
                var key = header.Key.ToLower();
                if (key == "accept")
                {
                    hasAccept = true;
                }
                else if (key == "accept-language")
                {
                    hasAcceptLanguage = true;
                }
            }
            if (!hasAccept)
                request.Headers.Add("Accept", "*/*");
            if (!hasAcceptLanguage)
                request.Headers.Add("Accept-Language", "*");

            // User-Agent
            if (!request.Headers.ContainsKey("User-Agent"))
            {
                // Always use Chrome UA as default to be consistent and avoid detection
                request.Headers.Add("User-Agent", BrowserUtil.ChromeUserAgent);
            }

            return;
        }

        public virtual async Task<WebResult> GetResultAsync(WebRequest request)
        {
            if (logger.IsDebugEnabled) // performance optimization
            {
                var postData = "";
                if (request.Type == RequestType.POST)
                {
                    var lines = request.PostData?.Select(kvp => kvp.Key + "=" + kvp.Value).ToList() ?? new List<string>();
                    postData = $" PostData: {{{string.Join(", ", lines)}}} RawBody: {request.RawBody}";
                }
                logger.Debug($"WebClient({ClientType}).GetResultAsync(Method: {request.Type} Url: {request.Url}{postData})");
            }

            PrepareRequest(request);
            await DelayRequest(request);

            WebResult result;
            if (!string.IsNullOrEmpty(serverConfig.FlareSolverrUrl))
            {
                result = await RunThroughFlareSolverr(request);
            }
            else
            {
                result = await Run(request);
            }

            lastRequest = DateTime.Now;
            result.Request = request;

            if (logger.IsDebugEnabled) // performance optimization to compute result.ContentString in debug mode only
            {
                var body = "";
                var bodySize = 0;
                var isBinary = false;
                if (result.ContentBytes is { Length: > 0 })
                {
                    bodySize = result.ContentBytes.Length;
                    var contentString = result.ContentString.Trim();
                    if (contentString.StartsWith("<") || contentString.StartsWith("{") || contentString.StartsWith("["))
                        body = "\n" + contentString;
                    else
                    {
                        body = " <BINARY>";
                        isBinary = true;
                    }
                }
                logger.Debug($@"WebClient({ClientType}): Returning {result.Status} => {(result.IsRedirect ? result.RedirectingTo + " " : "")}{bodySize} bytes{body}");
                if (isBinary)
                {
                    // show the first 20 bytes in a hex dump
                    var contentString = result.ContentString.Trim();
                    contentString = contentString.Length <= 20 ? contentString : contentString.Substring(0, 20);
                    var HexData = string.Join("", contentString.Select(c => c + "(" + ((int)c).ToString("X2") + ")"));
                    logger.Debug(string.Format("WebClient({0}): HexDump20: {1}", ClientType, HexData));
                }
            }

            return result;
        }

        protected virtual Task<WebResult> Run(WebRequest webRequest) => throw new NotImplementedException();

        protected async Task<WebResult> RunThroughFlareSolverr(WebRequest webRequest)
        {
            var apiUrl = serverConfig.FlareSolverrUrl;
            if (!apiUrl.EndsWith("/v1"))
            {
                apiUrl = apiUrl.TrimEnd('/') + "/v1";
            }

            var fsRequest = new JObject();
            fsRequest["cmd"] = webRequest.Type == RequestType.POST ? "request.post" : "request.get";
            fsRequest["url"] = webRequest.Url;
            fsRequest["maxTimeout"] = serverConfig.FlareSolverrMaxTimeout;

            if (webRequest.Type == RequestType.POST)
            {
                if (!string.IsNullOrEmpty(webRequest.RawBody))
                {
                    fsRequest["postData"] = webRequest.RawBody;
                }
                else if (webRequest.PostData != null && webRequest.PostData.Any())
                {
                    var postDataStr = string.Join("&", webRequest.PostData.Select(kv => $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));
                    fsRequest["postData"] = postDataStr;
                }
            }

            if (!string.IsNullOrEmpty(webRequest.Cookies))
            {
                var cookieList = new JArray();
                var cookieDictionary = CookieUtil.CookieHeaderToDictionary(webRequest.Cookies);
                foreach (var kv in cookieDictionary)
                {
                    cookieList.Add(new JObject { { "name", kv.Key }, { "value", kv.Value } });
                }
                fsRequest["cookies"] = cookieList;
            }

            if (!string.IsNullOrEmpty(webRequest.Referer))
            {
                // Note: Referer is not directly supported as a top-level param in FlareSolverr v2/v3
            }

            // Add Proxy if configured
            var proxyUrl = serverConfig.GetProxyUrl(false);
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                fsRequest["proxy"] = proxyUrl;
            }

            var content = new StringContent(fsRequest.ToString(), Encoding.UTF8, "application/json");
            var response = await FlareSolverrClient.PostAsync(apiUrl, content);
            var responseString = await response.Content.ReadAsStringAsync();
            var fsResponse = JObject.Parse(responseString);

            if (fsResponse["status"]?.ToString() == "ok")
            {
                var solution = fsResponse["solution"];
                var contentString = solution["response"]?.ToString();

                // If it's an API request returning JSON/XML, FlareSolverr (the browser) often wraps it in HTML tags like <pre>.
                // Since FlareSolverr doesn't return the original Content-Type header, we must guess and unwrap.
                if (contentString != null && contentString.TrimStart().StartsWith("<"))
                {
                    // 1. Check for <pre> tag which browsers use to wrap JSON or raw text
                    var match = Regex.Match(contentString, @"<pre[^>]*>(.*)</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        contentString = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                    }
                    // 2. Check for XML declaration if it's an XML response wrapped in HTML
                    else if (contentString.Contains("<?xml"))
                    {
                        var xmlMatch = Regex.Match(contentString, @"(<\?xml.*)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (xmlMatch.Success)
                        {
                            contentString = xmlMatch.Groups[1].Value;
                            // Strip any trailing HTML garbage the browser might have added
                            var lastTag = contentString.LastIndexOf(">");
                            if (lastTag > -1) contentString = contentString.Substring(0, lastTag + 1);
                        }
                    }
                    else
                    {
                        // 3. Fallback: if it's just inside <body>
                        match = Regex.Match(contentString, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var bodyContent = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                            // Only unwrap if the body doesn't look like actual HTML (no tags)
                            if (!bodyContent.Contains("<") || !bodyContent.Contains(">"))
                            {
                                contentString = bodyContent;
                            }
                        }
                    }
                }

                var result = new WebResult
                {
                    Status = (HttpStatusCode)(int)solution["status"],
                    ContentString = contentString,
                    Request = webRequest
                };

                if (solution["cookies"] != null)
                {
                    var cookies = new List<string>();
                    foreach (var cookie in (JArray)solution["cookies"])
                    {
                        cookies.Add($"{cookie["name"]}={cookie["value"]}");
                    }
                    result.Cookies = string.Join("; ", cookies);
                }

                // If FlareSolverr returns a UA, we capture it, but we don't send it in the request anymore.
                if (solution["userAgent"] != null)
                {
                    result.Headers["user-agent"] = new[] { solution["userAgent"].ToString() };
                }

                result.ContentBytes = result.Encoding.GetBytes(result.ContentString ?? "");

                return result;
            }
            else
            {
                throw new Exception("FlareSolverr error: " + fsResponse["message"]);
            }
        }

        public virtual void OnCompleted() => throw new NotImplementedException();

        public virtual void OnError(Exception error) => throw new NotImplementedException();

        public virtual void OnNext(ServerConfig value)
        {
            var newProxyUrl = serverConfig.GetProxyUrl();
            if (webProxyUrl != newProxyUrl) // if proxy URL changed
                InitProxy(serverConfig);
        }

        public virtual void SetTimeout(int seconds) => throw new NotImplementedException();

        /**
         * This method does the same as FormUrlEncodedContent but with custom encoding instead of utf-8
         * https://stackoverflow.com/a/13832544
         */
        protected static ByteArrayContent FormUrlEncodedContentWithEncoding(
            IEnumerable<KeyValuePair<string, string>> nameValueCollection, Encoding encoding)
        {
            // utf-8 / default
            if (Encoding.UTF8.Equals(encoding) || encoding == null)
                return new FormUrlEncodedContent(nameValueCollection);

            // other encodings
            var builder = new StringBuilder();
            foreach (var pair in nameValueCollection)
            {
                if (builder.Length > 0)
                    builder.Append('&');
                builder.Append(HttpUtility.UrlEncode(pair.Key, encoding));
                builder.Append('=');
                builder.Append(HttpUtility.UrlEncode(pair.Value, encoding));
            }
            // HttpRuleParser.DefaultHttpEncoding == "latin1"
            var data = Encoding.GetEncoding("latin1").GetBytes(builder.ToString());
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            return content;
        }

        protected static Uri RedirectUri(HttpResponseMessage response)
        {
            var newUri = response.Headers.Location;

            if (newUri == null)
            {
                var refreshHeader = response.Headers.TryGetValues("Refresh", out var refreshHeaders)
                    ? refreshHeaders.FirstOrDefault()
                    : null;

                if (refreshHeader == null)
                {
                    return null;
                }

                var match = _RefreshHeaderRegex.Match(refreshHeader);

                if (match.Success)
                {
                    return new Uri(response.RequestMessage.RequestUri, new Uri(match.Groups[2].Value, UriKind.RelativeOrAbsolute));
                }

                return null;
            }

            return new Uri(response.RequestMessage.RequestUri, newUri);
        }
    }
}
