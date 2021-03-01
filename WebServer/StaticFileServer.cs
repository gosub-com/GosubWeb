using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Gosub.Web
{
    /// <summary>
    /// Class to server static files from a root directory, and
    /// optionally do server side includes, but only when
    /// `TemplateFileExtensions` is set.  File names are case insensitive,
    /// but the server will redirect them to the correctly cased URL.
    /// </summary>
    public class StaticFileServer
    {
        public readonly Dictionary<string, string> MimeTypes = new Dictionary<string, string>()
        {
            {"htm", "text/html" },
            {"html", "text/html" },
            {"jpg", "image/jpeg" },
            {"jpeg", "image/jpeg" },
            {"png", "image/png" },
            {"gif", "image/gif" },
            {"css", "text/css" },
            {"js", "application/javascript" }
        };

        object mLock = new object();
        string mTemplateStartString = "$${{";
        string mTemplateEndString = "}}";
        string mTemplateFileExtensions = "";
        Dictionary<string, bool> mTemplateFileExtensionsDict = new Dictionary<string, bool>();
        Dictionary<string, FileCache> mFileCache = new Dictionary<string, FileCache>();

        class FileCache
        {
            public string Path = ""; // As specified by URL, but in lower case
            public string PathNormal = "";
            public DateTime LastCheckTimeUtc;
            public DateTime LastWriteTimeUtc;
            public string[] Dependencies = Array.Empty<string>();
            public byte[] CachedFile; // Null if invalid or not loaded from disk
            public override string ToString() { return Path; }
        }


        /// <summary>
        /// Serve files from fileSystemRoot directory.  The request must
        /// start with `webRoot`, which is then stripped from the request path.
        /// Throws exception if the directory doesn't exist.
        /// Use `TemplateFileExtensions` to process server side includes
        /// in the file.
        /// </summary>
        public StaticFileServer(string fileSystemRoot, string webRoot)
        {
            TemplateFileExtensions = "html;htm;css;js";
            FileSystemRoot = fileSystemRoot;
            try
            {
                if (!Directory.Exists(FileSystemRoot))
                    throw new HttpServerException($"Can't serve files from '{FileSystemRoot}, directory doesn't exist'");
            }
            catch (Exception ex)
            {
                Log.Error($"Can't serve files from '{FileSystemRoot}, error='{ex.Message}'");
                throw;
            }
        }

        public readonly string FileSystemRoot = "";

        /// <summary>
        /// When true (the default), re-write the browser URL to send them to
        /// case proper URL.
        /// </summary>
        public bool RewriteCase = true;

        /// <summary>
        /// Non-template files are cached if they are under MaxCacheSize.
        /// Template files are cached regardless of size.
        /// </summary>
        public int MaxCacheSize = 60000;

        /// <summary>
        /// Server side include start string.  "$${{" by default
        /// </summary>
        public string TemplateStartString
        {
            get { return mTemplateStartString; }
            set
            {
                if (mTemplateStartString == value)
                    return;
                lock (mLock)
                {
                    mTemplateStartString = value;
                    FlushCache();
                }
            }
        }

        /// <summary>
        /// Server side end string.  "}}" by default
        /// </summary>
        public string TemplateEndString
        {
            get { return mTemplateEndString; }
            set
            {
                if (mTemplateEndString == value)
                    return;
                lock (mLock)
                {
                    mTemplateEndString = value;
                    FlushCache();
                }
            }
        }

        /// <summary>
        /// Set this to a ";" separated list of file extensions.  For example,
        /// set "htm;html;js;css" to process those file extensions.
        /// </summary>
        public string TemplateFileExtensions
        {
            get { return mTemplateFileExtensions; }
            set
            {
                if (mTemplateFileExtensions == value)
                    return;
                lock (mLock)
                {
                    mTemplateFileExtensions = value;
                    mTemplateFileExtensionsDict.Clear();
                    FlushCache();
                    foreach (var extension in value.ToLower().Split(';'))
                        mTemplateFileExtensionsDict[extension] = true;
                }
            }
        } 

        public void FlushCache()
        {
            lock (mLock)
            {
                foreach (var fi in mFileCache.Values)
                    fi.CachedFile = null;
            }
        }



        /// <summary>
        /// Serve the file, or send invalid response if not in the file system.
        /// </summary>
        public async Task SendStaticFile(HttpContext context)
        {
            var request = context.Request;
            Log.Debug($"Load {context.Request.Path}");

            if (request.Method != "GET")
            {
                await context.SendResponseAsync("Invalid HTTP request: Only GET method is allowed for serving", 405);
                return;
            }

            // Check illegal file names (outside of the public directory, invisible files, windows path, etc.)
            string path = request.Path;
            if (path.Contains("..") || path.Contains("/.") || path.Contains("//") || path.Contains("\\"))
            {
                await context.SendResponseAsync("Invalid Request: File name is invalid", 400);
                return;
            }

            // Strip leading "\", and choose "index.html" if no name is given
            while (path.Length != 0 && path[0] == '/')
                path = path.Substring(1);

            var extension = request.Extension;
            if (path.Length == 0)
            {
                path = "index.html";
                extension = "html";
            }

            if (MimeTypes.TryGetValue(extension, out string contentType))
                context.Response.ContentType = contentType;

            byte[] file = GetFile(path, extension);
            if (file != null)
            {
                await context.SendResponseAsync(file);
                return;
            }

            var fsPath = Path.Combine(FileSystemRoot, path);
            if (!File.Exists(fsPath))
            {
                await context.SendResponseAsync("File not found: " + path, 404);
                return;
            }

            using (var stream = File.OpenRead(fsPath))
                await context.GetWriter(stream.Length).WriteAsync(stream);
        }

        /// <summary>
        /// Checks for the file in the cache, and returns it.  If not in cache,
        /// loads it from disk and does template stuff.  Some big files never
        /// enter the cache, so returns NULL in that case.  If NULL, you must
        /// serve the file from disk.  The path is the full path on the file system.
        /// </summary>
        byte []GetFile(string path, string extension)
        {
            lock (mLock)
            {
                var pathLower = path.ToLower();
                if (mFileCache.TryGetValue(pathLower, out var fcc))
                {
                    if (fcc.CachedFile != null)
                    {
                        // Cache hit
                        // TBD: Periodically check to see if file on disk was changed
                        return fcc.CachedFile;
                    }
                }
                var isTemplate = mTemplateFileExtensionsDict.ContainsKey(extension);

                var fsPath = Path.Combine(FileSystemRoot, path);
                if (!File.Exists(fsPath))
                    return null;
                var fi = new FileInfo(fsPath);
                if (!isTemplate && fi.Length > MaxCacheSize)
                    return null;

                var file = File.ReadAllBytes(fsPath);
                fi.Refresh();

                if (isTemplate)
                    file = ProcessTemplate(fsPath, file);

                var fc = new FileCache();
                fc.Path = path;
                fc.CachedFile = file;
                fc.LastWriteTimeUtc = fi.LastWriteTimeUtc;
                fc.LastCheckTimeUtc = DateTime.UtcNow;

                mFileCache[pathLower] = fc;

                return file;
            }

        }

        /// <summary>
        /// For now, don't process these recursively
        /// </summary>
        byte[] ProcessTemplate(string fsPath, byte []file)
        {
            var fileSpan = new Span<byte>(file);
            var startDelimeter = Encoding.UTF8.GetBytes(TemplateStartString);
            var i = fileSpan.IndexOf(startDelimeter);
            if (i < 0)
                return file;
            
            var endDelimeter = Encoding.UTF8.GetBytes(TemplateEndString);
            var fileList = new List<byte>();
            while (i >= 0)
            {
                fileList.AddSpan(fileSpan.Slice(0, i));
                fileSpan = fileSpan.Slice(i + startDelimeter.Length);

                var directiveLength = fileSpan.IndexOf(endDelimeter);
                if (directiveLength < 0)
                {
                    fileList.AddSpan(fileSpan);
                    Log.Error($"Error parsing template file '{fsPath}'");
                    return fileList.ToArray();
                }

                string directive = Encoding.UTF8.GetString(fileSpan.Slice(0, directiveLength));
                fileSpan = fileSpan.Slice(directiveLength + endDelimeter.Length);
                i = fileSpan.IndexOf(startDelimeter);

                // Since we only have one directive for now, just do it here
                string[] directiveParts = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (directiveParts.Length != 2 || directiveParts[0] != "#include")
                    throw new HttpServerException($"Error parsing '{fsPath}', invalid directive '{directive}'");
                var includeFile = Path.Combine(FileSystemRoot, directiveParts[1]);
                if (!File.Exists(includeFile))
                    throw new HttpServerException($"Error parsing '{fsPath}', include file '{includeFile}' does not exist.");
                fileList.AddSpan(new Span<byte>(File.ReadAllBytes(includeFile)));
            }

            fileList.AddSpan(fileSpan);
            return fileList.ToArray();
        }


    }
}
