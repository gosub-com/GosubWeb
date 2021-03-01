using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace Gosub.Web
{
    /// <summary>
    /// An HTTP stream reader.  Do not dispose (the web server owns and re-uses these)
    /// </summary>
    public class HttpReader
    {
        const int HTTP_MAX_HEADER_LENGTH = 16000;

        Stream mStream;
        CancellationToken mCancellationToken;
        long mLength;
        long mPosition;

        int mBufferIndex;
        int mBufferLength;
        byte[] mBuffer;

        internal HttpReader(Stream stream, CancellationToken cancellationToken)
        {
            mStream = stream;
            mCancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get => mCancellationToken; }
        public long Length => mLength;
        public long Position => mPosition;

        public bool InvalidProtocol { get; private set; }

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
            length = await mStream.ReadAsync(buffer, offset, count, mCancellationToken);

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
        /// Server only
        /// </summary>
        internal long PositionInternal { get => mPosition; set => mPosition = value; }
        internal long LengthInternal { get => mLength; set => mLength = value; }

        /// <summary>
        /// Called by the server to read the HTTP header into an internal buffer.
        /// Returns an empty buffer if the connection is closed or there is an
        /// invalid header (doesn't start with an HTTP method, or header is too long)
        /// </summary>
        internal async Task<ArraySegment<byte>> ScanHttpHeaderAsyncInternal()
        {
            // Create or shift buffer
            if (mBuffer == null)
                mBuffer = new byte[HTTP_MAX_HEADER_LENGTH];
            if (mBufferIndex != mBufferLength)
                Array.Copy(mBuffer, mBufferIndex, mBuffer, 0, mBufferLength - mBufferIndex);
            mBufferLength = mBufferLength - mBufferIndex;
            mBufferIndex = 0;

            // Wait for HTTP header
            int headerEndIndex = 0;
            bool validHeaderVerb = false;
            while (!FindEndOfHttpHeaderIndex(ref headerEndIndex))
            {
                var readCount = mBuffer.Length - mBufferLength;
                if (readCount <= 0)
                {
                    Log.Debug("HTTP header is too long");
                    InvalidProtocol = true;
                    return new ArraySegment<byte>();
                }

                var length = await mStream.ReadAsync(mBuffer, mBufferLength, readCount, mCancellationToken);
                mBufferLength += length;

                if (length == 0)
                {
                    if (mBufferIndex != 0)
                    {
                        Log.Debug("Connection closed after reading partial HTTP header");
                        InvalidProtocol = true;
                    }
                    return new ArraySegment<byte>();
                }

                // Quick check to make sure HTTP method is valid
                const int MAX_METHOD_LENGTH = 8; // Including ' '
                if (!validHeaderVerb && mBufferLength >= MAX_METHOD_LENGTH)
                {
                    if (mBuffer[0] == 22)
                    {
                        Log.Debug("Invalid HTTP method, got HTTPS on an HTTP port");
                        InvalidProtocol = true;
                        return new ArraySegment<byte>();
                    }
                    var headerIndex = new Span<byte>(mBuffer, 0, MAX_METHOD_LENGTH).IndexOf((byte)' ');
                    if (headerIndex <= 2
                        || !HttpRequest.ValidHttpMethod(mBuffer.AsciiToString(0, headerIndex)))
                    {
                        Log.Debug("Invalid HTTP method");
                        InvalidProtocol = true;
                        return new ArraySegment<byte>();
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
