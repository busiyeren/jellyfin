using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Emby.Server.Implementations.HttpClientManager
{
    /// <summary>
    /// Class HttpClientManager
    /// </summary>
    public class HttpClientManager : IHttpClient
    {
        /// <summary>
        /// When one request to a host times out, we'll ban all other requests for this period of time, to prevent scans from stalling
        /// </summary>
        private const int TimeoutSeconds = 30;

        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// The _app paths
        /// </summary>
        private readonly IApplicationPaths _appPaths;

        private readonly IFileSystem _fileSystem;
        private readonly Func<string> _defaultUserAgentFn;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpClientManager" /> class.
        /// </summary>
        public HttpClientManager(
            IApplicationPaths appPaths,
            ILoggerFactory loggerFactory,
            IFileSystem fileSystem,
            Func<string> defaultUserAgentFn)
        {
            if (appPaths == null)
            {
                throw new ArgumentNullException(nameof(appPaths));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _logger = loggerFactory.CreateLogger("HttpClient");
            _fileSystem = fileSystem;
            _appPaths = appPaths;
            _defaultUserAgentFn = defaultUserAgentFn;

            // http://stackoverflow.com/questions/566437/http-post-returns-the-error-417-expectation-failed-c
            ServicePointManager.Expect100Continue = false;
        }

        /// <summary>
        /// Holds a dictionary of http clients by host.  Use GetHttpClient(host) to retrieve or create a client for web requests.
        /// DON'T dispose it after use.
        /// </summary>
        /// <value>The HTTP clients.</value>
        private readonly ConcurrentDictionary<string, HttpClientInfo> _httpClients = new ConcurrentDictionary<string, HttpClientInfo>();

        /// <summary>
        /// Gets
        /// </summary>
        /// <param name="host">The host.</param>
        /// <param name="enableHttpCompression">if set to <c>true</c> [enable HTTP compression].</param>
        /// <returns>HttpClient.</returns>
        /// <exception cref="ArgumentNullException">host</exception>
        private HttpClientInfo GetHttpClient(string host, bool enableHttpCompression)
        {
            if (string.IsNullOrEmpty(host))
            {
                throw new ArgumentNullException(nameof(host));
            }

            var key = host + enableHttpCompression;

            if (!_httpClients.TryGetValue(key, out var client))
            {
                client = new HttpClientInfo();

                _httpClients.TryAdd(key, client);
            }

            return client;
        }

        private WebRequest GetRequest(HttpRequestOptions options, string method)
        {
            string url = options.Url;

            var uriAddress = new Uri(url);
            string userInfo = uriAddress.UserInfo;
            if (!string.IsNullOrWhiteSpace(userInfo))
            {
                _logger.LogInformation("Found userInfo in url: {0} ... url: {1}", userInfo, url);
                url = url.Replace(userInfo + "@", string.Empty);
            }

            var request = WebRequest.Create(url);

            if (request is HttpWebRequest httpWebRequest)
            {
                AddRequestHeaders(httpWebRequest, options);

                if (options.EnableHttpCompression)
                {
                    httpWebRequest.AutomaticDecompression = DecompressionMethods.Deflate;
                    if (options.DecompressionMethod.HasValue
                        && options.DecompressionMethod.Value == CompressionMethod.Gzip)
                    {
                        httpWebRequest.AutomaticDecompression = DecompressionMethods.GZip;
                    }
                }
                else
                {
                    httpWebRequest.AutomaticDecompression = DecompressionMethods.None;
                }

                httpWebRequest.KeepAlive = options.EnableKeepAlive;

                if (!string.IsNullOrEmpty(options.Host))
                {
                    httpWebRequest.Host = options.Host;
                }

                if (!string.IsNullOrEmpty(options.Referer))
                {
                    httpWebRequest.Referer = options.Referer;
                }
            }

            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);

            request.Method = method;
            request.Timeout = options.TimeoutMs;

            if (!string.IsNullOrWhiteSpace(userInfo))
            {
                var parts = userInfo.Split(':');
                if (parts.Length == 2)
                {
                    request.Credentials = GetCredential(url, parts[0], parts[1]);
                    // TODO: .net core ??
                    request.PreAuthenticate = true;
                }
            }

            return request;
        }

        private static CredentialCache GetCredential(string url, string username, string password)
        {
            //ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3;
            var credentialCache = new CredentialCache();
            credentialCache.Add(new Uri(url), "Basic", new NetworkCredential(username, password));
            return credentialCache;
        }

        private void AddRequestHeaders(HttpWebRequest request, HttpRequestOptions options)
        {
            var hasUserAgent = false;

            foreach (var header in options.RequestHeaders)
            {
                if (string.Equals(header.Key, HeaderNames.Accept, StringComparison.OrdinalIgnoreCase))
                {
                    request.Accept = header.Value;
                }
                else if (string.Equals(header.Key, HeaderNames.UserAgent, StringComparison.OrdinalIgnoreCase))
                {
                    SetUserAgent(request, header.Value);
                    hasUserAgent = true;
                }
                else
                {
                    request.Headers.Set(header.Key, header.Value);
                }
            }

            if (!hasUserAgent && options.EnableDefaultUserAgent)
            {
                SetUserAgent(request, _defaultUserAgentFn());
            }
        }

        private static void SetUserAgent(HttpWebRequest request, string userAgent)
        {
            request.UserAgent = userAgent;
        }

        /// <summary>
        /// Gets the response internal.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>Task{HttpResponseInfo}.</returns>
        public Task<HttpResponseInfo> GetResponse(HttpRequestOptions options)
        {
            return SendAsync(options, "GET");
        }

        /// <summary>
        /// Performs a GET request and returns the resulting stream
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>Task{Stream}.</returns>
        public async Task<Stream> Get(HttpRequestOptions options)
        {
            var response = await GetResponse(options).ConfigureAwait(false);
            return response.Content;
        }

        /// <summary>
        /// send as an asynchronous operation.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="httpMethod">The HTTP method.</param>
        /// <returns>Task{HttpResponseInfo}.</returns>
        /// <exception cref="HttpException">
        /// </exception>
        public async Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string httpMethod)
        {
            if (options.CacheMode == CacheMode.None)
            {
                return await SendAsyncInternal(options, httpMethod).ConfigureAwait(false);
            }

            var url = options.Url;
            var urlHash = url.ToLowerInvariant().GetMD5().ToString("N");

            var responseCachePath = Path.Combine(_appPaths.CachePath, "httpclient", urlHash);

            var response = GetCachedResponse(responseCachePath, options.CacheLength, url);
            if (response != null)
            {
                return response;
            }

            response = await SendAsyncInternal(options, httpMethod).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                await CacheResponse(response, responseCachePath).ConfigureAwait(false);
            }

            return response;
        }

        private HttpResponseInfo GetCachedResponse(string responseCachePath, TimeSpan cacheLength, string url)
        {
            if (File.Exists(responseCachePath)
                && _fileSystem.GetLastWriteTimeUtc(responseCachePath).Add(cacheLength) > DateTime.UtcNow)
            {
                var stream = _fileSystem.GetFileStream(responseCachePath, FileOpenMode.Open, FileAccessMode.Read, FileShareMode.Read, true);

                return new HttpResponseInfo
                {
                    ResponseUrl = url,
                    Content = stream,
                    StatusCode = HttpStatusCode.OK,
                    ContentLength = stream.Length
                };
            }

            return null;
        }

        private async Task CacheResponse(HttpResponseInfo response, string responseCachePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(responseCachePath));

            using (var fileStream = _fileSystem.GetFileStream(responseCachePath, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.None, true))
            {
                await response.Content.CopyToAsync(fileStream).ConfigureAwait(false);

                response.Content.Position = 0;
            }
        }

        private async Task<HttpResponseInfo> SendAsyncInternal(HttpRequestOptions options, string httpMethod)
        {
            ValidateParams(options);

            options.CancellationToken.ThrowIfCancellationRequested();

            var client = GetHttpClient(GetHostFromUrl(options.Url), options.EnableHttpCompression);

            if ((DateTime.UtcNow - client.LastTimeout).TotalSeconds < TimeoutSeconds)
            {
                throw new HttpException(string.Format("Cancelling connection to {0} due to a previous timeout.", options.Url))
                {
                    IsTimedOut = true
                };
            }

            var httpWebRequest = GetRequest(options, httpMethod);

            if (options.RequestContentBytes != null ||
                !string.IsNullOrEmpty(options.RequestContent) ||
                string.Equals(httpMethod, "post", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = options.RequestContentBytes ?? Encoding.UTF8.GetBytes(options.RequestContent ?? string.Empty);

                    var contentType = options.RequestContentType ?? "application/x-www-form-urlencoded";

                    if (options.AppendCharsetToMimeType)
                    {
                        contentType = contentType.TrimEnd(';') + "; charset=\"utf-8\"";
                    }

                    httpWebRequest.ContentType = contentType;
                    (await httpWebRequest.GetRequestStreamAsync().ConfigureAwait(false)).Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex)
                {
                    throw new HttpException(ex.Message) { IsTimedOut = true };
                }
            }

            if (options.ResourcePool != null)
            {
                await options.ResourcePool.WaitAsync(options.CancellationToken).ConfigureAwait(false);
            }

            if ((DateTime.UtcNow - client.LastTimeout).TotalSeconds < TimeoutSeconds)
            {
                options.ResourcePool?.Release();

                throw new HttpException($"Connection to {options.Url} timed out") { IsTimedOut = true };
            }

            if (options.LogRequest)
            {
                if (options.LogRequestAsDebug)
                {
                    _logger.LogDebug("HttpClientManager {0}: {1}", httpMethod.ToUpper(CultureInfo.CurrentCulture), options.Url);
                }
                else
                {
                    _logger.LogInformation("HttpClientManager {0}: {1}", httpMethod.ToUpper(CultureInfo.CurrentCulture), options.Url);
                }
            }

            try
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                if (!options.BufferContent)
                {
                    var response = await GetResponseAsync(httpWebRequest, TimeSpan.FromMilliseconds(options.TimeoutMs)).ConfigureAwait(false);

                    var httpResponse = (HttpWebResponse)response;

                    EnsureSuccessStatusCode(client, httpResponse, options);

                    options.CancellationToken.ThrowIfCancellationRequested();

                    return GetResponseInfo(httpResponse, httpResponse.GetResponseStream(), GetContentLength(httpResponse), httpResponse);
                }

                using (var response = await GetResponseAsync(httpWebRequest, TimeSpan.FromMilliseconds(options.TimeoutMs)).ConfigureAwait(false))
                {
                    var httpResponse = (HttpWebResponse)response;

                    EnsureSuccessStatusCode(client, httpResponse, options);

                    options.CancellationToken.ThrowIfCancellationRequested();

                    using (var stream = httpResponse.GetResponseStream())
                    {
                        var memoryStream = new MemoryStream();
                        await stream.CopyToAsync(memoryStream).ConfigureAwait(false);

                        memoryStream.Position = 0;

                        return GetResponseInfo(httpResponse, memoryStream, memoryStream.Length, null);
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                throw GetCancellationException(options, client, options.CancellationToken, ex);
            }
            catch (Exception ex)
            {
                throw GetException(ex, options, client);
            }
            finally
            {
                options.ResourcePool?.Release();
            }
        }

        private HttpResponseInfo GetResponseInfo(HttpWebResponse httpResponse, Stream content, long? contentLength, IDisposable disposable)
        {
            var responseInfo = new HttpResponseInfo(disposable)
            {
                Content = content,
                StatusCode = httpResponse.StatusCode,
                ContentType = httpResponse.ContentType,
                ContentLength = contentLength,
                ResponseUrl = httpResponse.ResponseUri.ToString()
            };

            if (httpResponse.Headers != null)
            {
                SetHeaders(httpResponse.Headers, responseInfo);
            }

            return responseInfo;
        }

        private HttpResponseInfo GetResponseInfo(HttpWebResponse httpResponse, string tempFile, long? contentLength)
        {
            var responseInfo = new HttpResponseInfo
            {
                TempFilePath = tempFile,
                StatusCode = httpResponse.StatusCode,
                ContentType = httpResponse.ContentType,
                ContentLength = contentLength
            };

            if (httpResponse.Headers != null)
            {
                SetHeaders(httpResponse.Headers, responseInfo);
            }

            return responseInfo;
        }

        private static void SetHeaders(WebHeaderCollection headers, HttpResponseInfo responseInfo)
        {
            foreach (var key in headers.AllKeys)
            {
                responseInfo.Headers[key] = headers[key];
            }
        }

        public Task<HttpResponseInfo> Post(HttpRequestOptions options)
        {
            return SendAsync(options, "POST");
        }

        /// <summary>
        /// Performs a POST request
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="postData">Params to add to the POST data.</param>
        /// <returns>stream on success, null on failure</returns>
        public async Task<Stream> Post(HttpRequestOptions options, Dictionary<string, string> postData)
        {
            options.SetPostData(postData);

            var response = await Post(options).ConfigureAwait(false);

            return response.Content;
        }

        /// <summary>
        /// Downloads the contents of a given url into a temporary location
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>Task{System.String}.</returns>
        public async Task<string> GetTempFile(HttpRequestOptions options)
        {
            var response = await GetTempFileResponse(options).ConfigureAwait(false);

            return response.TempFilePath;
        }

        public async Task<HttpResponseInfo> GetTempFileResponse(HttpRequestOptions options)
        {
            ValidateParams(options);

            Directory.CreateDirectory(_appPaths.TempDirectory);

            var tempFile = Path.Combine(_appPaths.TempDirectory, Guid.NewGuid() + ".tmp");

            if (options.Progress == null)
            {
                throw new ArgumentException("Options did not have a Progress value.", nameof(options));
            }

            options.CancellationToken.ThrowIfCancellationRequested();

            var httpWebRequest = GetRequest(options, "GET");

            if (options.ResourcePool != null)
            {
                await options.ResourcePool.WaitAsync(options.CancellationToken).ConfigureAwait(false);
            }

            options.Progress.Report(0);

            if (options.LogRequest)
            {
                if (options.LogRequestAsDebug)
                {
                    _logger.LogDebug("HttpClientManager.GetTempFileResponse url: {0}", options.Url);
                }
                else
                {
                    _logger.LogInformation("HttpClientManager.GetTempFileResponse url: {0}", options.Url);
                }
            }

            var client = GetHttpClient(GetHostFromUrl(options.Url), options.EnableHttpCompression);

            try
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                using (var response = await httpWebRequest.GetResponseAsync().ConfigureAwait(false))
                {
                    var httpResponse = (HttpWebResponse)response;

                    EnsureSuccessStatusCode(client, httpResponse, options);

                    options.CancellationToken.ThrowIfCancellationRequested();

                    var contentLength = GetContentLength(httpResponse);

                    using (var stream = httpResponse.GetResponseStream())
                    using (var fs = _fileSystem.GetFileStream(tempFile, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.Read, true))
                    {
                        await stream.CopyToAsync(fs, StreamDefaults.DefaultCopyToBufferSize, options.CancellationToken).ConfigureAwait(false);
                    }

                    options.Progress.Report(100);

                    return GetResponseInfo(httpResponse, tempFile, contentLength);
                }
            }
            catch (Exception ex)
            {
                DeleteTempFile(tempFile);
                throw GetException(ex, options, client);
            }
            finally
            {
                options.ResourcePool?.Release();
            }
        }

        private static long? GetContentLength(HttpWebResponse response)
        {
            var length = response.ContentLength;

            if (length == 0)
            {
                return null;
            }

            return length;
        }

        protected static readonly CultureInfo UsCulture = new CultureInfo("en-US");

        private Exception GetException(Exception ex, HttpRequestOptions options, HttpClientInfo client)
        {
            if (ex is HttpException)
            {
                return ex;
            }

            var webException = ex as WebException
                               ?? ex.InnerException as WebException;

            if (webException != null)
            {
                if (options.LogErrors)
                {
                    _logger.LogError(webException, "Error {status} getting response from {url}", webException.Status, options.Url);
                }

                var exception = new HttpException(webException.Message, webException);

                using (var response = webException.Response as HttpWebResponse)
                {
                    if (response != null)
                    {
                        exception.StatusCode = response.StatusCode;

                        if ((int)response.StatusCode == 429)
                        {
                            client.LastTimeout = DateTime.UtcNow;
                        }
                    }
                }

                if (!exception.StatusCode.HasValue)
                {
                    if (webException.Status == WebExceptionStatus.NameResolutionFailure ||
                        webException.Status == WebExceptionStatus.ConnectFailure)
                    {
                        exception.IsTimedOut = true;
                    }
                }

                return exception;
            }

            var operationCanceledException = ex as OperationCanceledException
                                             ?? ex.InnerException as OperationCanceledException;

            if (operationCanceledException != null)
            {
                return GetCancellationException(options, client, options.CancellationToken, operationCanceledException);
            }

            if (options.LogErrors)
            {
                _logger.LogError(ex, "Error getting response from {url}", options.Url);
            }

            return ex;
        }

        private void DeleteTempFile(string file)
        {
            try
            {
                _fileSystem.DeleteFile(file);
            }
            catch (IOException)
            {
                // Might not have been created at all. No need to worry.
            }
        }

        private void ValidateParams(HttpRequestOptions options)
        {
            if (string.IsNullOrEmpty(options.Url))
            {
                throw new ArgumentNullException(nameof(options));
            }
        }

        /// <summary>
        /// Gets the host from URL.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <returns>System.String.</returns>
        private static string GetHostFromUrl(string url)
        {
            var index = url.IndexOf("://", StringComparison.OrdinalIgnoreCase);

            if (index != -1)
            {
                url = url.Substring(index + 3);
                var host = url.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(host))
                {
                    return host;
                }
            }

            return url;
        }

        /// <summary>
        /// Throws the cancellation exception.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="client">The client.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>Exception.</returns>
        private Exception GetCancellationException(HttpRequestOptions options, HttpClientInfo client, CancellationToken cancellationToken, OperationCanceledException exception)
        {
            // If the HttpClient's timeout is reached, it will cancel the Task internally
            if (!cancellationToken.IsCancellationRequested)
            {
                var msg = string.Format("Connection to {0} timed out", options.Url);

                if (options.LogErrors)
                {
                    _logger.LogError(msg);
                }

                client.LastTimeout = DateTime.UtcNow;

                // Throw an HttpException so that the caller doesn't think it was cancelled by user code
                return new HttpException(msg, exception)
                {
                    IsTimedOut = true
                };
            }

            return exception;
        }

        private void EnsureSuccessStatusCode(HttpClientInfo client, HttpWebResponse response, HttpRequestOptions options)
        {
            var statusCode = response.StatusCode;

            var isSuccessful = statusCode >= HttpStatusCode.OK && statusCode <= (HttpStatusCode)299;

            if (isSuccessful)
            {
                return;
            }

            if (options.LogErrorResponseBody)
            {
                try
                {
                    using (var stream = response.GetResponseStream())
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                var msg = reader.ReadToEnd();

                                _logger.LogError(msg);
                            }
                        }
                    }
                }
                catch
                {

                }
            }

            throw new HttpException(response.StatusDescription)
            {
                StatusCode = response.StatusCode
            };
        }

        private static Task<WebResponse> GetResponseAsync(WebRequest request, TimeSpan timeout)
        {
            var taskCompletion = new TaskCompletionSource<WebResponse>();

            var asyncTask = Task.Factory.FromAsync(request.BeginGetResponse, request.EndGetResponse, null);

            ThreadPool.RegisterWaitForSingleObject((asyncTask as IAsyncResult).AsyncWaitHandle, TimeoutCallback, request, timeout, true);
            var callback = new TaskCallback { taskCompletion = taskCompletion };
            asyncTask.ContinueWith(callback.OnSuccess, TaskContinuationOptions.NotOnFaulted);

            // Handle errors
            asyncTask.ContinueWith(callback.OnError, TaskContinuationOptions.OnlyOnFaulted);

            return taskCompletion.Task;
        }

        private static void TimeoutCallback(object state, bool timedOut)
        {
            if (timedOut && state != null)
            {
                var request = (WebRequest)state;
                request.Abort();
            }
        }

        private class TaskCallback
        {
            public TaskCompletionSource<WebResponse> taskCompletion;

            public void OnSuccess(Task<WebResponse> task)
            {
                taskCompletion.TrySetResult(task.Result);
            }

            public void OnError(Task<WebResponse> task)
            {
                if (task.Exception == null)
                {
                    taskCompletion.TrySetException(Enumerable.Empty<Exception>());
                }
                else
                {
                    taskCompletion.TrySetException(task.Exception);
                }
            }
        }
    }
}
