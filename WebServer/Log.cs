using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Gosub.Web
{

    /// <summary>
    /// Web server log files.
    /// </summary>
    static public class Log
    {
        static QueueList<string> mLog = new QueueList<string>();

        /// <summary>
        /// Use Level to index
        /// </summary>
        static readonly string[] LevelNames = ["DEBUG", " INFO", "ERROR"];

        /// <summary>
        /// Strip private parts of file name
        /// </summary>
        static readonly int SourcePathRootLength;

        public enum Level
        {
            Debug,
            Info,
            Error
        }

        /// <summary>
        /// Default: Write to console when debugger is attached
        /// </summary>
        static public Level WriteToConsole { get; set; } = Debugger.IsAttached ? Level.Debug : Level.Info;

        /// <summary>
        /// Number to keep in memory for quick recall
        /// </summary>
        static public int MaxEntries { get; set; } = 1000;

        static Log()
        {
            SourcePathRootLength = GetSourcePathRootLength();
            Console.WriteLine();
            Info("", null, 0);
            Info("*** STARTING LOG ***", null, 0, "", "");
            Info("", null, 0);
        }

        static int GetSourcePathRootLength([CallerFilePath] string filePath = "")
        {
            // Strip enough to get to root of project
            filePath = Path.GetDirectoryName(filePath.Replace("\\", "/"));
            filePath = Path.GetDirectoryName(filePath);
            filePath = Path.GetDirectoryName(filePath);
            return filePath.Length + 1;
        }

        /// <summary>
        /// Log an error
        /// </summary>
        static public void Error(string message,
            Exception exception = null,
            [CallerLineNumber] int lineNumber = -1,
            [CallerFilePath] string fileName = "",
            [CallerMemberName] string memberName = "")
        {
            Write(Level.Error, message, exception, lineNumber, fileName, memberName);
        }


        /// <summary>
        /// Log info
        /// </summary>
        static public void Info(string message,
            Exception exception = null,
            [CallerLineNumber]int lineNumber = -1,
            [CallerFilePath]string fileName = "",
            [CallerMemberName]string memberName = "")
        {
            Write(Level.Info, message, exception, lineNumber, fileName, memberName);
        }

        /// <summary>
        /// Log debug info
        /// </summary>
        static public void Debug(string message,
            Exception exception = null,
            [CallerLineNumber] int lineNumber = -1,
            [CallerFilePath] string fileName = "",
            [CallerMemberName] string memberName = "")
        {
            Write(Level.Debug, message, exception, lineNumber, fileName, memberName);
        }

        static void Write(Level level,  string message, Exception exception,
            int lineNumber, string fileName, string memberName)
        {
            message = $"{DateTime.Now.ToString("yyyy-MM-dd, HH:mm:ss.fff")}, {LevelNames[(int)level]}: {message}";
            if (lineNumber > 0)
                message += $" [{fileName.Substring(SourcePathRootLength)}:{lineNumber}, {memberName}]";
            if (exception != null)
                message += $", \"{exception.Message}\", {exception.GetType().Name}, STACK {exception.StackTrace}";
            Add(level, message);
        }

        static void Add(Level level, string message)
        {
            lock (mLog)
            {
                mLog.Enqueue(message);
                if (mLog.Count > MaxEntries)
                    mLog.Dequeue();
            }
            if (level >= WriteToConsole)
                Console.WriteLine(message);
        }

        static public string GetAsString(int maxLines)
        {
            lock (mLog)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = mLog.Count-1;  i >= 0;  i--)
                {
                    sb.Append(mLog[i]);
                    if (i != 0)
                        sb.Append("\r\n");
                }
                return sb.ToString();
            }
        }

    }
}
