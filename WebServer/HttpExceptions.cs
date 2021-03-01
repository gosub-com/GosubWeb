using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Gosub.Web
{
    /// <summary>
    /// Throw this exception if there is a problem processing the request.
    /// The log will contain an error with file name and line number.
    /// Stack trace is optional, pass stackTrace = true.
    /// </summary>
    public class HttpServerException : Exception
    {
        public readonly bool LogStackTrace;
        public readonly int LineNumber;
        public readonly string FileName;
        public readonly string MemberName;
        public HttpServerException(string message, bool stackTrace = false,
            [CallerLineNumber] int lineNumber = -1,
            [CallerFilePath] string fileName = "",
            [CallerMemberName] string memberName = "")
            : base(message) 
        {
            LogStackTrace = stackTrace;
            LineNumber = lineNumber;
            FileName = fileName;
            MemberName = memberName;
        }
    }

    /// <summary>
    /// Generate an HTTP client error 400 and closes the connection.
    /// This is only logged in debug mode since it is expected based on user input.
    /// </summary>
    public class HttpProtocolException : Exception
    {
        public readonly int Code;
        public HttpProtocolException(string message, int code = 400) 
            : base(message) { Code = code; }
    }

}
