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
using System.Text.Json.Serialization;

namespace Gosub.Web
{
    public struct HttpStats
    {
        public DateTime CurrentTime { get; set; }
        public int TcpAlive { get; set; }
        public int Buffers {  get; set; }
        public long TcpEverConnected { get; set; }
        public long Hits {  get; set; }
        public long Waiting {  get; set; }
        public long ServingHttpBody {  get; set; }
        public long ServingWebsockets {  get; set; }
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

        static int sTcpAlive;
        static int sBuffers;
        static long sTcpEverConnected;
        static long sHits;
        static long sWaiting;
        static long sServingHttpBody;
        static long sServingWebsockets;

        // They carry a big buffer, so re-use them
        Stack<HttpReader> mReaders = new Stack<HttpReader>();

        public HttpServer()
        {
        }

        public event HttpHandlerDelegate HttpHandler;


        /// <summary>
        /// NOTE: Assuming we are on 64 bit machine with 64 bit CLR, values should not tear.
        /// </summary>
        public HttpStats Stats => new HttpStats() 
        { 
            CurrentTime = DateTime.UtcNow,
            TcpAlive = sTcpAlive,
            Buffers = sBuffers,
            TcpEverConnected = sTcpEverConnected,
            Hits = sHits,
            Waiting = sWaiting,
            ServingHttpBody = sServingHttpBody,
            ServingWebsockets = sServingWebsockets
        };

        public int MaxConnections = 10000;

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
                    TryProcessConnectionAsync(client, certificate);
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

        async void TryProcessConnectionAsync(TcpClient client, X509Certificate certificate)
        {
            HttpReader reader;
            lock (mLock)
            {
                // Quick exit when overloaded
                if (sTcpAlive > MaxConnections)
                {
                    try { client.GetStream().Close(); } catch { }
                    try { client.Close(); } catch { }
                    return;
                }

                if (mReaders.Count != 0)
                    reader = mReaders.Pop();
                else
                    reader = new HttpReader();
                sBuffers = mReaders.Count;
                sTcpAlive++;
                sTcpEverConnected++;
            }

            HttpContext context = null;
            try
            {
                var stream = await reader.StartConnectionAsync(client, certificate, mCancellationToken).ConfigureAwait(false);
                if (stream == null)
                    return;

                // Process requests on this possibly persistent TCP stream
                var writer = new HttpWriter(stream, mCancellationToken);
                context = new HttpContext(reader, writer);
                await ProcessRequests(context, reader, writer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await ProcessExceptionAsync(context, ex).ConfigureAwait(false);
                }
                catch (Exception doubleFaultEx)
                {
                    Log.Error($"DOUBLE FAULT EXCEPTION: {doubleFaultEx.Message}", doubleFaultEx);
                }
            }
            finally
            {
                try { reader.Close(); } catch { Log.Error("Close reader"); }
                lock (mLock)
                {
                    mReaders.Push(reader);
                    sTcpAlive--;
                    sBuffers = mReaders.Count;
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
                try
                {
                    Interlocked.Increment(ref sWaiting);
                    var httpRequest = await reader.ReadHeaderAsync().ConfigureAwait(false);
                    if (httpRequest == null)
                    {
                        return; // Failed header, close connection don't even respond
                    }
                    context.SetRequestInternal(httpRequest, new HttpResponse());
                }
                finally
                {
                    Interlocked.Decrement(ref sWaiting);
                }

                await ProcessBody(context, writer).ConfigureAwait(false);

                // Any of these problems will terminate a persistent connection
                var response = context.Response;
                var request = context.Request;
                var isWebsocket = request.IsWebSocketRequest;
                if (!response.HeaderSent)
                    throw new HttpServerException("Request handler did not send a response for: " + request.Path);
                if (!isWebsocket && reader.Position != request.ContentLength && request.ContentLength >= 0)
                    throw new HttpServerException($"Request handler did not read correct number of bytes: '{request.Path}', "
                                                  + $"content-length={request.ContentLength}, position={reader.Position}, "
                                                  + $"method='{context.Request.Method}', ip={context.RemoteEndPoint}");
                if (!isWebsocket && writer.Position != response.ContentLength)
                    throw new HttpServerException("Request handler did not write correct number of bytes: " + request.Path);
            } while (!context.Request.IsWebSocketRequest && context.Response.Connection == "keep-alive");
        }


        async Task ProcessBody(HttpContext context, HttpWriter writer)
        {
            // Handle body
            if (context.Request.IsWebSocketRequest)
                Interlocked.Increment(ref sServingWebsockets);
            else
                Interlocked.Increment(ref sServingHttpBody);
            try
            {
                // Process HTTP request
                var handler = HttpHandler;
                if (handler == null)
                    throw new HttpServerException("HTTP request handler not installed");
                await handler(context).ConfigureAwait(false);
                await writer.FlushAsync();
                Interlocked.Increment(ref sHits);
            }
            catch (HttpProtocolException)
            {
                // Protocol exceptions kills the connection
                throw;
            }
            catch (Exception ex) when (!context.Response.HeaderSent)
            {
                // Keep the persistent connection open since the response wasn't sent
                await ProcessExceptionAsync(context, ex);
                await writer.FlushAsync();
            }
            finally
            {
                if (context.Request.IsWebSocketRequest)
                    Interlocked.Decrement(ref sServingWebsockets);
                else
                    Interlocked.Decrement(ref sServingHttpBody);
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
                // Protocol failure, show error to client (if possible)
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
