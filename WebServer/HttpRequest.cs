using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;

namespace Gosub.Web
{
    /// <summary>
    /// HTTP request header received from client
    /// </summary>
    public class HttpRequest
    {
        public DateTime ReceiveDate;

        public string Method = "";
        public int ProtocolVersionMajor;
        public int ProtocolVersionMinor;

        /// <summary>
        /// Path of request excluding query
        /// </summary>
        public string Path = "";
        public string PathLower = "";

        /// <summary>
        /// File extension excluding ".", lower case.
        /// </summary>
        public string Extension = "";


        public string Host = "";
        public string HostNoPort = "";
        public string Connection = "";
        public string Referer = "";

        
        public string Fragment = "";
        public HttpDict Query = new HttpDict();
        public HttpDict Cookies = new HttpDict();

        // Common Headers
        public HttpDict Headers = new HttpDict();
        public long ContentLength;
        public string AcceptEncoding = "";
        public bool IsWebSocketRequest { get; private set;  }

        static readonly Dictionary<string, bool> sMethods = new Dictionary<string, bool>()
        {
            { "GET", true },
            { "HEAD", true },
            { "POST", true },
            { "PUT", true },
            { "DELETE", true },
            { "OPTIONS", true },
            { "CONNECT", true },
            { "TRACE", true }
        };

        public static bool ValidHttpMethod(string method)
        {
            return sMethods.ContainsKey(method);
        }


        public static HttpRequest Parse(ArraySegment<byte> buffer)
        {
            var request = new HttpRequest();
            request.ReceiveDate = DateTime.Now;

            for (int i = 0; i < buffer.Count; i++)
            {
                int ch = buffer[i];
                if (ch < ' ' && ch != 10 && ch != 13)
                    throw new HttpProtocolException($"HTTP header contains control character, {ch}");
                if (ch > '~')
                    throw new HttpProtocolException($"HTTP header contains UTF-8 character, {ch}");
            }

            var header = Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count).Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (header.Length == 0)
                throw new HttpProtocolException("HTTP header is empty");

            var headerParts = header[0].Split(' ');
            if (headerParts.Length != 3)
                throw new HttpProtocolException("HTTP request line needs 3 parts separated by space");

            // Parse method
            request.Method = headerParts[0];
            if (!sMethods.ContainsKey(request.Method))
                throw new HttpProtocolException("HTTP request has unknown method");

            // Parse URL Fragment
            var target = headerParts[1];
            int fragmentIndex = target.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                request.Fragment = target.Substring(fragmentIndex + 1);
                target = target.Substring(0, fragmentIndex);
            }
            // Parse URL query string
            int queryIndex = target.IndexOf('?');
            if (queryIndex >= 0)
            {
                var query = target.Substring(queryIndex + 1);
                target = target.Substring(0, queryIndex);
                ParseQueryString(query, request.Query);
            }
            // Remove leading and trailing '/'
            while (target.EndsWith("/"))
                target = target.Substring(0, target.Length - 1);
            while (target.StartsWith("/"))
                target = target.Substring(1);
            request.Path = target;
            request.PathLower = target.ToLower();

            request.Extension = "";
            int extensionIndex = target.LastIndexOf('.');
            if (extensionIndex >= 0 && target.IndexOf('/', extensionIndex) < 0)
                request.Extension = target.Substring(extensionIndex + 1).ToLower();

            // Parse protocol and version
            var protocolParts = headerParts[2].Split('/');
            if (protocolParts.Length != 2 || protocolParts[0].ToUpper() != "HTTP")
                throw new HttpProtocolException("HTTP request line has unrecognized protocol.  Only HTTP is supported");

            var versionParts = protocolParts[1].Split('.');
            if (versionParts.Length != 2
                    || !int.TryParse(versionParts[0], out request.ProtocolVersionMajor)
                    || !int.TryParse(versionParts[1], out request.ProtocolVersionMinor))
                throw new HttpProtocolException("HTTP header has invalid protocol version format, expecting #.#");

            if (request.ProtocolVersionMajor != 1)
                throw new HttpProtocolException("HTTP header version is incorrect, expecting 1.#");

            // Read header fields
            var headers = request.Headers;
            for (int lineIndex = 1; lineIndex < header.Length; lineIndex++)
            {
                var fieldLine = header[lineIndex];

                int index;
                if ((index = fieldLine.IndexOf(':')) < 0)
                    throw new HttpProtocolException("HTTP header field missing ':'");

                var key = fieldLine.Substring(0, index).Trim().ToLower();
                var value = fieldLine.Substring(index + 1).Trim();

                switch (key)
                {
                    case "": throw new HttpProtocolException("HTTP header field missing key or value");
                    case "cookie": ParseCookie(value, request.Cookies);  break;
                    case "host": request.Host = value;  break;
                    case "accept-encoding":  request.AcceptEncoding = value.ToLower();  break;
                    case "content-length": long.TryParse(value, out request.ContentLength);  break;
                    case "connection": request.Connection = value.ToLower(); break;
                    case "referer": request.Referer = value;  break;
                    default: headers[key] = value;  break;
                }
            }

            // Parse well known header fields
            request.Headers = headers;
            var host = request.Host;
            if (host.Contains(':'))
                host = host.Substring(0, host.IndexOf(':'));
            request.HostNoPort = host;

            // Websocket connection?  RFC 6455, 4.2.1
            if (request.Connection.Contains("upgrade")
                && headers["upgrade"].ToLower() == "websocket")
            {
                if (headers.Get("sec-websocket-version", 0) < 13)
                    throw new HttpProtocolException("Web socket request version must be >= 13");
                request.IsWebSocketRequest = true;
            }
            return request;
        }

        public static void ParseQueryString(string query, HttpDict queryStrings)
        {
            foreach (var q in query.Split('&'))
            {
                int keyIndex = q.IndexOf('=');
                if (keyIndex >= 0)
                    queryStrings[q.Substring(0, keyIndex)] = q.Substring(keyIndex + 1);
                else if (q != "")
                    queryStrings[q] = "";
            }
        }

        static void ParseCookie(string cookieHeader, HttpDict cookies)
        {
            foreach (var cookie in cookieHeader.Split(';'))
            {
                int index = cookie.IndexOf('=');
                if (index > 0)
                    cookies[cookie.Substring(0, index).Trim()] = cookie.Substring(index + 1).Trim();
            }
        }

    }
}
