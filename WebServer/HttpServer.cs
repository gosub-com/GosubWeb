using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Gosub.Web
{
    public struct HttpStats
    {
        public DateTime Time;
        public long Connects;
        public long Hits;
        public long WaitingForRequest;
        public long ServingHttp;
        public long ServingWs;
        public long InvalidProtocol;
        public long InvalidHeaders;
    }

    /// <summary>
    /// RFC 7230 and 7231: HTTP Server
    /// </summary>
    public class HttpServer
    {
        public delegate Task HttpHandlerDelegate(HttpContext context);

        object mLock = new object();
        HashSet<TcpListener> mListeners = new HashSet<TcpListener>();
        CancellationToken mCancellationToken = CancellationToken.None; // TBD: Implement cancellation token

        HttpStats mStats;

        public HttpServer()
        {
        }

        public event HttpHandlerDelegate HttpHandler;


        /// <summary>
        /// NOTE: Assuming we are on 64 bit machine with 64 bit CLR, values should not tear.
        /// </summary>
        public HttpStats Stats { get { var t = mStats;  t.Time = DateTime.Now;  return t; } }

        /// <summary>
        /// Start server on this listener (ssl if certificate is not null)
        /// </summary>
        public async Task Start(TcpListener listener, X509Certificate certificate = null)
        {
            if (certificate == null)
                Log.Info($"Starting HTTP web server running on port {listener.LocalEndpoint}");
            else
                Log.Info($"Starting HTTPS web server running on port {listener.LocalEndpoint}, cert={certificate.Subject}");

            listener.Start();
            lock (mLock)
                mListeners.Add(listener);

            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref mStats.Connects);
                    TryProcessRequestsAsync(client, certificate);
                }
            }
            catch (Exception ex)
            {
                Log.Error("HttpServer exception", ex);
            }
            lock (mLock)
                mListeners.Remove(listener);
            try { listener.Stop(); }
            catch { }
        }

        public void Stop()
        {
            lock (mLock)
            {
                foreach (var listener in mListeners)
                    try { listener.Stop(); }
                    catch { }
                mListeners.Clear();
            }
        }

        async void TryProcessRequestsAsync(TcpClient client, X509Certificate certificate)
        {
            using (client)
            {
                HttpContext context = null;
                try
                {
                    // Setup stream or SSL stream
                    client.NoDelay = true;
                    var tcpStream = (Stream)client.GetStream();
                    bool isSecure = false;
                    if (certificate != null)
                    {
                        // Wrap stream in an SSL stream, and authenticate
                        var sslStream = new SslStream(tcpStream, false);
                        await sslStream.AuthenticateAsServerAsync(certificate);
                        tcpStream = sslStream;
                        isSecure = true;
                    }
                    // Process requests on this possibly persistent TCP stream
                    var reader = new HttpReader(tcpStream, mCancellationToken);
                    var writer = new HttpWriter(tcpStream, mCancellationToken);
                    context = new HttpContext(client, reader, writer, isSecure);
                    await ProcessRequests(context, reader, writer).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        await ProcessExceptionAsync(context, ex);
                    }
                    catch (Exception doubleFaultEx)
                    {
                        Log.Error($"DOUBLE FAULT EXCEPTION: {doubleFaultEx.Message}", doubleFaultEx);
                    }
                }
            }
        }

        /// <summary>
        /// Process requests as long as there is not an error
        /// </summary>
        async Task ProcessRequests(HttpContext context, HttpReader reader, HttpWriter writer)
        {
            do
            {
                if (!await ReadHeader(context, reader))
                    return; // Failed header means we need to close the connection

                await ProcessBody(context, writer).ConfigureAwait(false);

                // Any of these problems will terminate a persistent connection
                var response = context.Response;
                var request = context.Request;
                var isWebsocket = request.IsWebSocketRequest;
                if (!response.HeaderSent)
                    throw new HttpServerException("Request handler did not send a response for: " + context.Request.Path);
                if (!isWebsocket && reader.Position != request.ContentLength && request.ContentLength >= 0)
                    throw new HttpServerException("Request handler did not read correct number of bytes: " + request.Path);
                if (!isWebsocket && writer.Position != response.ContentLength)
                    throw new HttpServerException("Request handler did not write correct number of bytes: " + request.Path);
            } while (!context.Request.IsWebSocketRequest && context.Response.Connection == "keep-alive");
        }

        /// <summary>
        /// If the HTTP header is valid, sets the context's request field and
        /// returns TRUE.  Otherwise, returns FALSE, and the connection must
        /// be closed.
        /// </summary>
        async Task<bool> ReadHeader(HttpContext context, HttpReader reader)
        {
            Interlocked.Increment(ref mStats.WaitingForRequest);
            try
            {
                // Read header
                var buffer = await reader.ScanHttpHeaderAsyncInternal();
                reader.PositionInternal = 0;
                reader.LengthInternal = HttpContext.HTTP_HEADER_MAX_SIZE;
                if (buffer.Count == 0)
                {
                    if (reader.InvalidProtocol)
                        Interlocked.Increment(ref mStats.InvalidProtocol);
                    return false;  // Don't even bother responding
                }

                // Parse header
                var httpRequest = HttpRequest.Parse(buffer);
                context.SetRequestInternal(httpRequest, new HttpResponse());
            }
            catch (HttpProtocolException exp)
            {
                Interlocked.Increment(ref mStats.InvalidHeaders);
                Log.Debug($"Invalid HTTP header, error='{exp.Message}'");
                try { await context.SendResponseAsync(exp.Message, 400); }
                catch { }
                return false; // Close connection
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref mStats.InvalidHeaders);
                Log.Error($"Exception while processing HTTP header, error='{ex.Message}'", ex);
                try { await context.SendResponseAsync("Error processing HTTP header", 500); }
                catch { }
                return false; // Close connection
            }
            finally
            {
                Interlocked.Decrement(ref mStats.WaitingForRequest);
            }
            return true;
        }

        async Task ProcessBody(HttpContext context, HttpWriter writer)
        {
            // Handle body
            if (context.Request.IsWebSocketRequest)
                Interlocked.Increment(ref mStats.ServingWs);
            else
                Interlocked.Increment(ref mStats.ServingHttp);
            try
            {
                // Process HTTP request
                var handler = HttpHandler;
                if (handler == null)
                    throw new HttpServerException("HTTP request handler not installed");
                await handler(context).ConfigureAwait(false);
                await writer.FlushAsync();
                Interlocked.Increment(ref mStats.Hits);
            }
            catch (HttpServerException ex) when (!context.Response.HeaderSent)
            {
                // Keep the persistent connection open since the response wasn't sent
                await ProcessExceptionAsync(context, ex);
                await writer.FlushAsync();
            }
            finally
            {
                if (context.Request.IsWebSocketRequest)
                    Interlocked.Decrement(ref mStats.ServingWs);
                else
                    Interlocked.Decrement(ref mStats.ServingHttp);
            }
        }

        // Try to send an error message back to the client
        async Task ProcessExceptionAsync(HttpContext context, Exception ex)
        {
            // Unwrap aggregate exceptions.  Async likes to throw these.
            var aggEx = ex as AggregateException;
            if (aggEx != null && aggEx.InnerException != null)
            {
                // Unwrap exception
                Log.Error("SERVER ERROR Aggregate with " + aggEx.InnerExceptions.Count + " inner exceptions");
                ex = ex.InnerException;
            }

            var code = 500;
            var message = "Server error";
            if (ex is HttpProtocolException protoEx)
            {
                // Protocol failure, show error to client
                code = protoEx.Code;
                message = protoEx.Message;
                Log.Debug(protoEx.Message);
            }
            else if (ex is HttpServerException serverEx)
            {
                // Server error in user code.  Stack trace optional.
                code = 500;
                message = "There was a server error.  It has been logged and we are looking into it.";
                Log.Error(serverEx.Message, serverEx.LogStackTrace ? serverEx : null,
                    serverEx.LineNumber, serverEx.FileName, serverEx.MemberName);
            }
            else
            {
                // Anything else needs a stack trace
                code = 500;
                message = "There was a server error.  It has been logged and we are looking into it.";
                Log.Error(ex.Message, ex);
            }

            // Send response to client if it looks OK to do so
            if (context != null && !context.Request.IsWebSocketRequest && !context.Response.HeaderSent)
            {
                context.Response.StatusCode = code;
                context.Response.StatusMessage = message;
                await context.SendResponseAsync(message);
            }
        }
    }
}
