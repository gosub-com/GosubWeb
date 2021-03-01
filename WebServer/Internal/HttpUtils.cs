using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gosub.Web
{
    /// <summary>
    /// Non-public Http server utilities
    /// </summary>
    internal static class HttpUtils
    {
        static readonly byte[] sLowTable = new byte[256];

        static HttpUtils()
        {
            // Create table of lower case ASCII
            for (int i = 0; i < sLowTable.Length; i++)
            {
                if (i >= 'A' && i <= 'Z')
                    sLowTable[i] = (byte)(i - 'A' + 'a');
                else
                    sLowTable[i] = (byte)i;
            }
        }

        /// <summary>
        /// Convert ASCII bytes to lower case (ignore unicode characters)
        /// </summary>
        public static void AsciiLower(this Span<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = sLowTable[bytes[i]];
        }

        /// <summary>
        /// Convert ASCII to string (128..255 are converted to '?')
        /// </summary>
        public static string AsciiToString(this byte []array, int offset, int count)
        {
            var s = (array, offset, count);
            return String.Create(count, s, (chars, state) =>
            {
                for (int i = 0; i < chars.Length; i++)
                {
                    var b = state.array[state.offset + i];
                    chars[i] = (char)state.array[state.offset + i];
                }
            });
        }

        public static void AddSpan(this List<byte> list, ReadOnlySpan<byte> span)
        {
            foreach (var b in span)
                list.Add(b);
        }

    }
}
