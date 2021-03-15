using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Gosub.Web
{
    /// <summary>
    /// An HTTP stream reader.  The web server owns and re-uses these
    /// </summary>
    public class HttpReader
    {
        const int HTTP_MAX_HEADER_LENGTH = 16000;

        TcpClient mTcpClient;
        Stream mStream;
        CancellationToken mCancellationToken;
        long mLength;
        long mPosition;

        int mBufferIndex;
        int mBufferLength;
        byte[] mBuffer = new byte[HTTP_MAX_HEADER_LENGTH];
        bool mIsSecure;
        DateTime mLastReadTime;
        int mLocalPort;
        EndPoint mLocalEndPoint;
        EndPoint mRemoteEndPoint;


        /// <summary>
        /// Only created by the server
        /// </summary>
        internal HttpReader()
        {
        }

        public CancellationToken CancellationToken { get => mCancellationToken; }
        public long Length => mLength;
        public long Position => mPosition;
        public bool IsSecure => mIsSecure;
        public EndPoint RemoteEndPoint => mRemoteEndPoint;
        public EndPoint LocalEndPoint => mLocalEndPoint;
        public int LocalPort => mLocalPort;
        public DateTime LastReadTime => mLastReadTime;

        /// <summary>
        /// Called only by server to start the reader and read the first packet.
        /// Returns a stream if everything looks good, or NULL if anything is wrong.
        /// Quickly and silently close any connection that doesn't meet the
        /// bare minimums.  Owns and closes the tcp client.
        /// </summary>
        internal async Task<Stream> StartConnectionAsync(TcpClient tcpClient, X509Certificate certificate , CancellationToken cancellationToken)
        {
            Close();
            mTcpClient = tcpClient;
            mLocalEndPoint = tcpClient.Client.LocalEndPoint;
            mRemoteEndPoint = tcpClient.Client.RemoteEndPoint;
            mLocalPort = 0;
            if (mLocalEndPoint is IPEndPoint ep)
                mLocalPort = ep.Port;
            mStream = null;
            mLength = 0;
            mPosition = 0;
            mBufferIndex = 0;
            mBufferLength = 0;
            mIsSecure = false;
            mCancellationToken = cancellationToken;
            mLastReadTime = DateTime.Now;

            try
            {
                tcpClient.NoDelay = true;
                var length = await tcpClient.Client.ReceiveAsync(new Memory<byte>(mBuffer), SocketFlags.Peek);
                if (length == 0)
                    return null;

                if (length < 3)
                {
                    Log.Debug($"HTTP protocol, short first packet: {length} bytes, first byte={(int)mBuffer[0]}, ip={mRemoteEndPoint}");
                    return null;
                }
                int b0 = mBuffer[0];
                if (certificate == null && b0 == 22)
                {
                    Log.Debug($"HTTP protocol, got TLS on HTTP connection, local port={LocalPort}, ip={mRemoteEndPoint}");
                    return null;
                }
                if (certificate != null)
                {
                    if (b0 != 22)
                    {
                        Log.Debug($"HTTPS protocol, expecting TLS, first byte={b0}, local port={LocalPort}, ip={mRemoteEndPoint}");
                        return null;
                    }
                    if (mBuffer[1] < 3 || mBuffer[1] == 3 && mBuffer[2] < 1)
                    {
                        Log.Debug($"HTTPS protocol, old SSL not supported, ip={mRemoteEndPoint}");
                        return null;
                    }
                }

                // Setup stream or SSL stream
                mStream = tcpClient.GetStream();
                if (certificate != null)
                {
                    // Wrap stream in an SSL stream, and authenticate
                    var sslStream = new SslStream(mStream, false);
                    mStream = sslStream;
                    await sslStream.AuthenticateAsServerAsync(certificate);
                    mIsSecure = true;
                }
                return mStream;
            }
            catch (Exception ex)
            {
                // For HTTPS, we get lots of closed connections and authentication failures
                Close();
                Log.Debug($"HTTP{(mIsSecure ? "S" :"")} protocol, first packet: {ex.GetType()}: '{ex.Message}', ip={mRemoteEndPoint}");
                return null;
            }
        }

        internal void Close()
        {
            if (mStream != null)
                try { mStream.Close(); } catch { Log.Error("Close stream"); }
            mStream = null;
            if (mTcpClient != null)
                try { mTcpClient.Close(); } catch { Log.Error("Close tcp client"); }
            mTcpClient = null;
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count)
        {
            // Limit number of bytes to read
            count = (int)Math.Min(mLength - mPosition, count);

            // Read data from internal buffer
            int length;
            if (mBufferIndex != mBufferLength)
            {
                length = Math.Min(count, mBufferLength - mBufferIndex);
                Array.Copy(mBuffer, mBufferIndex, buffer, offset, length);
                mBufferIndex += length;
                mPosition += length;
                return length;
            }
            // Pass request to underlying stream
            try
            {
                mLastReadTime = DateTime.Now;
                length = await mStream.ReadAsync(buffer, offset, count, mCancellationToken);
            }
            catch (Exception ex)
            {
                // Any error on the underlying stream closes the persisten connection
                throw new HttpProtocolException(ex.Message);
            }

            mPosition += length;
            return length;
        }

        /// <summary>
        /// Fill a buffer with the requested number of bytes, do not return 
        /// until they are all there or a timeout exception is thrown
        /// </summary>
        public async Task<int> ReadAllAsync(ArraySegment<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Count)
            {
                var length = await ReadAsync(buffer.Array, buffer.Offset + offset, buffer.Count - offset);
                if (length == 0)
                    throw new HttpProtocolException("Unexpected end of stream");
                offset += length;
            }
            return offset;
        }

        /// <summary>
        /// Called only by the server
        /// </summary>
        internal void SetLengthPosition(long length, long position)
        {
            mLength = length;
            mPosition = position;
        }

        /// <summary>
        /// Returns the HTTP request if the header is valid.
        /// Otherwise, returns NULL, and the connection must be closed. 
        /// </summary>
        internal async Task<HttpRequest> ReadHeaderAsync()
        {
            try
            {
                // Read header
                var buffer = await ScanHeaderAsync();
                mPosition = 0;
                mLength = HttpContext.HTTP_HEADER_MAX_SIZE;
                if (buffer.Count == 0)
                    return null;  // Connection closed normally

                // Parse header
                return HttpRequest.Parse(buffer);
            }
            catch (HttpProtocolException exp)
            {
                Log.Debug($"Failed HTTP header: '{exp.Message}', local port={LocalPort}, ip={RemoteEndPoint}");
                return null;
            }
            catch (IOException ioex)
            {
                // Lots of closed connections and TLS failures
                Log.Debug($"Failed HTTP header (IOException):  {ioex.Message}, local port={LocalPort}, ip={RemoteEndPoint}");
                return null;
            }
            catch (Exception ex)
            {
                // Stack trace for this
                Log.Error($"Exception while processing HTTP header, error='{ex.Message}'", ex);
                return null;
            }
        }

        /// <summary>
        /// Read the HTTP header into an internal buffer.
        /// Returns an empty buffer if the connection is closed or there is an
        /// invalid header (doesn't start with an HTTP method, or header is too long)
        /// </summary>
        async Task<ArraySegment<byte>> ScanHeaderAsync()
        {
            // Shift buffer
            if (mBufferIndex != mBufferLength)
                Array.Copy(mBuffer, mBufferIndex, mBuffer, 0, mBufferLength - mBufferIndex);
            mBufferLength = mBufferLength - mBufferIndex;
            mBufferIndex = 0;

            // Wait for HTTP header
            int headerEndIndex = 0;
            bool validHeaderVerb = false;
            mLastReadTime = DateTime.Now;
            while (!FindEndOfHttpHeaderIndex(ref headerEndIndex))
            {
                var readCount = mBuffer.Length - mBufferLength;
                if (readCount <= 0)
                    throw new HttpProtocolException("HTTP header is too long");

                var length = await mStream.ReadAsync(mBuffer, mBufferLength, readCount, mCancellationToken);
                mBufferLength += length;

                if (length == 0)
                {
                    if (mBufferIndex != 0)
                        throw new HttpProtocolException("Connection closed after reading partial HTTP header");
                    return new ArraySegment<byte>();
                }

                // Quick check to make sure HTTP method is valid
                const int MAX_METHOD_LENGTH = 8; // Including ' '
                if (!validHeaderVerb && mBufferLength >= MAX_METHOD_LENGTH)
                {
                    var headerIndex = new Span<byte>(mBuffer, 0, MAX_METHOD_LENGTH).IndexOf((byte)' ');
                    if (headerIndex <= 2
                        || !HttpRequest.ValidHttpMethod(mBuffer.AsciiToString(0, headerIndex)))
                    {
                        throw new HttpProtocolException($"Invalid HTTP method, buffer length={mBufferLength}, first byte={(int)mBuffer[0]}");
                    }
                    validHeaderVerb = true;
                }
            }

            // Consume the header and return the buffer
            mBufferIndex = headerEndIndex;
            return new ArraySegment<byte>(mBuffer, 0, mBufferIndex);
        }

        bool FindEndOfHttpHeaderIndex(ref int index)
        {
            int i = index;
            while (i < mBufferLength - 3)
            {
                if (mBuffer[i] == '\r' && mBuffer[i+1] == '\n' && mBuffer[i+2] == '\r' && mBuffer[i+3] == '\n')
                {
                    index = i + 4;
                    return true;
                }
                i++;
            }
            index = i;
            return false;
        }


    }
}
