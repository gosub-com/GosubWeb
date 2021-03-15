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
    public class HttpWriter
    {
        Stream mStream;
        CancellationToken mCancellationToken;
        long mLength;
        long mPosition;
        Task mPreWriteTask;

        internal HttpWriter(Stream stream, CancellationToken cancellationToken)
        {
            mStream = stream;
            mCancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get => mCancellationToken; }
        public long Length => mLength;
        public long Position => mPosition;
        public int WriteTimeout { get => mStream.WriteTimeout; set => mStream.WriteTimeout = value; }
        
        /// <summary>
        /// Server only
        /// </summary>
        internal long PositionInternal { get => mPosition; set => mPosition = value; }
        internal long LengthInternal { get => mLength; set => mLength = value; }
        internal void SetPreWriteTaskInternal(Task task) { mPreWriteTask = task; }

        public Task WriteAsync(byte[] buffer)
        {
            return WriteAsync(buffer, 0, buffer.Length);
        }

        public async Task WriteAsync(Stream stream)
        {
            byte[] buffer = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(buffer, 0, buffer.Length, mCancellationToken)) != 0)
                await WriteAsync(buffer, 0, length);
        }

        public async Task WriteAsync(byte[] buffer, int offset, int count)
        {
            if (mPreWriteTask != null)
            {
                await mPreWriteTask;
                mPreWriteTask = null;
            }

            if (count == 0)
                return;

            // Send data
            mPosition += count;
            if (mPosition > mLength)
                throw new HttpProtocolException("Request handler wrote too many bytes");

            try
            {
                await mStream.WriteAsync(buffer, offset, count, mCancellationToken);
            }
            catch (Exception ex)
            {
                // Instead of stack dump, intercept the broken pipe and re-issue the error
                throw new HttpProtocolException($"Error writing to stream: {ex.Message}");
            }
        }

        public async Task FlushAsync()
        {
            if (mPreWriteTask != null)
            {
                await mPreWriteTask;
                mPreWriteTask = null;
            }
            try
            {
                await mStream.FlushAsync();
            }
            catch (Exception ex)
            {
                // Instead of stack dump, intercept the broken pipe and re-issue the error
                throw new HttpProtocolException($"Error flushing to stream: {ex.Message}");
            }
        }
    }
}
