using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Runtime.CompilerServices;

namespace Gosub.Web
{
    /// <summary>
    /// Class to server static files from a root directory, and optionally
    /// do server side includes, when  `TemplateFileExtensions` is set.
    /// </summary>
    public class StaticFileServer
    {
        /// <summary>
        /// Send "/page" to "/page/index.html"
        /// </summary>
        const string DEFAULT_FILE_NAME = "index.html";

        /// <summary>
        /// Send "/page" to "/page.html" so we can put our pages in the same
        /// directory without having to name them all "index.html".
        /// </summary>
        const string DEFAULT_FILE_EXTENSION = "html";

        const string DEFAULT_TEMPLATE_FILE_EXTENSIONS = "html;htm;css;js";
        const string DEFAULT_COMPRESSED_FILE_EXTENSIONS = "html;htm;css;js;svg;ttf;otf;xml;json;dat;dll;wasm";

        public readonly Dictionary<string, string> MimeTypes = new Dictionary<string, string>()
        {
            {"htm", "text/html" },
            {"html", "text/html" },
            {"jpg", "image/jpeg" },
            {"jpeg", "image/jpeg" },
            {"png", "image/png" },
            {"gif", "image/gif" },
            {"css", "text/css" },
            {"js", "application/javascript" },
            {"svg", "image/svg+xml" },
            {"woff", "font/woff" },
            {"woff2", "font/woff2" },
            {"mp3", "audio/mpeg" },
            {"ogg", "audio/ogg" },
        };

        object mLock = new object();
        string mDefaultFileName = DEFAULT_FILE_NAME;
        string mDefaultFileExtension = DEFAULT_FILE_EXTENSION;
        string mTemplateStartString = "${{";
        string mTemplateEndString = "}}";
        string mTemplateFileExtensions = "";
        Dictionary<string, bool> mTemplateFileExtensionsDict = new Dictionary<string, bool>();
        string mCompressedFileExtensions = "";
        Dictionary<string, bool> mCompressedFileExtensionsDict = new Dictionary<string, bool>();
        Dictionary<string, FileCache> mFileCache = new Dictionary<string, FileCache>();

        class FileCache
        {
            public string PathHttp = ""; // As specified by HTTP URL
            public string PathFull = "";  // Absolute position in file system
            public string Extension = "";
            public DateTime LastWriteTimeUtc;
            public byte[] Data;
            public long Hits;
            public override string ToString() { return PathHttp; }
        }


        public string FileSystemRoot { get; private set; } = "";

        public StaticFileServer()
        {
            TemplateFileExtensions = DEFAULT_TEMPLATE_FILE_EXTENSIONS;
            CompressedFileExtensions = DEFAULT_COMPRESSED_FILE_EXTENSIONS;
        }

        /// <summary>
        /// Set the file system root directory.
        /// Throws exception if the directory doesn't exist or file
        /// enumeration fails. WebRoot not implemented yet.
        /// </summary>
        public void SetRoot(string fileSystemRoot, string webRoot)
        {
            if (webRoot != "")
                throw new Exception("webRoot not implemented yet");

            FileSystemRoot = Path.GetFullPath(fileSystemRoot);
            try
            {
                if (!Directory.Exists(FileSystemRoot))
                {
                    Log.Error($"Can't serve files from '{FileSystemRoot}, directory doesn't exist'");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Can't serve files from '{FileSystemRoot}, error='{ex.Message}'");
                return;
            }
            Log.Info($"Serving {mFileCache.Count} files from '{FileSystemRoot}'");
        }

        void FlushCache()
        {
            lock (mLock)
                mFileCache.Clear();
        }


        /// <summary>
        /// Server side include start string.  "${{" by default
        /// </summary>
        public string TemplateStartString
        {
            get { return mTemplateStartString; }
            set
            {
                if (mTemplateStartString == value)
                    return;
                mTemplateStartString = value;
                FlushCache();
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
                mTemplateEndString = value;
                FlushCache();
            }
        }

        /// <summary>
        /// Set this to template file extensions separated by ";". 
        /// For example, "htm;html;js;css"
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
                    foreach (var extension in value.ToLower().Split(';'))
                        mTemplateFileExtensionsDict[extension] = true;
                    FlushCache();
                }
            }
        }

        /// <summary>
        /// Set this to compressed file extensions separated by ";" 
        /// </summary>
        public string CompressedFileExtensions
        {
            get { return mCompressedFileExtensions; }
            set
            {
                if (mCompressedFileExtensions == value)
                    return;
                lock (mLock)
                {
                    mCompressedFileExtensions = value;
                    mCompressedFileExtensionsDict.Clear();
                    foreach (var extension in value.ToLower().Split(';'))
                        mCompressedFileExtensionsDict[extension] = true;
                    FlushCache();
                }
            }
        }

        /// <summary>
        /// When a directory is specified, try this file name.
        ///     "/" -> "/index.html"
        ///     "/page" -> "/page/index.html"
        /// </summary>
        public string DefaultFileName
        {
            get { return mDefaultFileName; }
            set
            {
                if (value == mDefaultFileName)
                    return;
                mDefaultFileName = value;
                FlushCache();
            }
        }

        /// <summary>
        /// When a directory is specified, try this file extension.
        ///     "/page" -> "/page.html"
        /// This allows us to put our pages in the same directory
        /// without having to name them all "index.html"
        /// </summary>
        public string DefaultFileExtension
        {
            get { return mDefaultFileExtension; }
            set
            {
                if (value == mDefaultFileExtension)
                    return;
                mDefaultFileExtension = value.ToLower();
                FlushCache();
            }
        }

        public string GetLog()
        {
            var sb = new StringBuilder();
            sb.Append($"Root: {FileSystemRoot}\r\n");
            lock (mLock)
            {
                foreach (var item in mFileCache.OrderBy(i => i.Key))
                    sb.Append($"{item.Value.Hits,7}: {item.Value.PathHttp}\r\n");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Serve the file, or send invalid response if not in the file system.
        /// </summary>
        public async Task SendFile(HttpContext context)
        {
            var request = context.Request;
            if (request.Method != "GET")
            {
                await context.SendResponseAsync("Invalid HTTP request: Only GET method is allowed for serving", 405);
                return;
            }

            // Check illegal file names (outside of the public directory, invisible files, windows path, etc.)
            string path = request.Path;
            if (path.Contains("..") || path.Contains("//") || path.Contains("\\"))
            {
                await context.SendResponseAsync("Invalid Request: File name is invalid", 400);
                return;
            }

            UpdateCache(request.Path);

            var compressions = context.Request.AcceptEncoding.ToLower().Replace(" ", "").Split(",");
            var allowGzip = compressions.Contains("gzip");
            var allowBrotli = compressions.Contains("br");


            var (fileData, compression, extension) 
                = GetFileFromCache(path, allowGzip, allowBrotli);

            if (fileData == null)
            {
                Log.Debug($"File not found, path='{context.Request.Path}', referer='{context.Request.Referer}', ip={context.RemoteEndPoint}'");
                await context.SendResponseAsync($"File not found: '{path}'", 404);
                return;
            }
            if (MimeTypes.TryGetValue(extension, out string contentType))
                context.Response.ContentType = contentType;

            // Send file
            context.Response.ContentEncoding = compression;
            await context.SendResponseAsync(fileData);
        }

        /// <summary>
        /// Retrieve the file from the cache.  Must call `UpdateCache` before
        /// calling this function.  When `allowGzip` or `allowBrotli` are true,
        /// try finding the same file with ".gz" or ".br" extension. 
        /// Hits are marked only on the uncompressed file.
        /// </summary>
        (byte[] file, string compression, string ext) GetFileFromCache(
            string path, bool allowGzip, bool allowBrotli)
        {
            lock (mLock)
            {
                if (!mFileCache.TryGetValue(path, out var fc))
                    return (null, "", "");

                // We have a hit, check for compressions
                fc.Hits++;
                var ext = fc.Extension;
                if (allowBrotli && mFileCache.TryGetValue(path + ".br", out var fcBrotli))
                    return (fcBrotli.Data, "br", ext);
                if (allowGzip && mFileCache.TryGetValue(path + ".gz", out var fcGzip))
                    return (fcGzip.Data, "gzip", ext);
                return (fc.Data, "", ext);
            }
        }



        /// <summary>
        /// Make sure the cache loaded the file.  This is very fast
        /// for cache hits, so call this before each request.  If the file is
        /// compressible, create a compressed version with the same name but
        /// with ".gz" extension. If a compressed version already exists,
        /// use that instead.
        /// </summary>
        void UpdateCache(string httpPath)
        {
            lock (mLock)
            {
                if (mFileCache.TryGetValue(httpPath, out var fileCache))
                {
                    // Cache hit.  Make sure the file system hasn't changed.
                    // NOTE: Even though this is IO, it's probably very fast
                    //       since it's cached, so we'll keep the lock while
                    //       doing this test.
                    if (File.Exists(fileCache.PathFull))
                    {
                        var fileInfo = new FileInfo(fileCache.PathFull);
                        if (fileInfo.LastWriteTimeUtc == fileCache.LastWriteTimeUtc)
                            return;
                    }
                    Log.Info($"File changed '{httpPath}', reloading from cache.");
                    mFileCache.Remove(httpPath);
                    mFileCache.Remove(httpPath + ".gz");
                    mFileCache.Remove(httpPath + ".br");
                }
            }

            // Check for file.  Route directory requests to "/index.html"
            // in that directory, or to "/directory.html" at that level.
            //      "/" -> "/index.html"
            //      "/page" -> either "/page/index.html" or "/page.html"
            var fullPath = Path.Combine(FileSystemRoot, httpPath);
            if (!File.Exists(fullPath))
            {
                // Check for directory query (e.g. "/" -> "/index.html")
                if (File.Exists(Path.Combine(fullPath, DefaultFileName)))
                    fullPath = Path.Combine(fullPath, DefaultFileName);
                else if (File.Exists(fullPath + "." + DefaultFileExtension))
                    fullPath = fullPath + "." + DefaultFileExtension;
                else
                    return;  // File not found
            }

            // Read the file (also read compressed versions if they exist)
            var fi = new FileInfo(fullPath);
            var loadStartTime = DateTime.UtcNow;
            var extension = Path.GetExtension(fullPath).ToLower().Replace(".", "");
            var uncompressedFile = File.ReadAllBytes(fullPath);
            LoadCacheFile(extension, fullPath + ".gz", httpPath + ".gz");
            LoadCacheFile(extension, fullPath + ".br", httpPath + ".br");
            var loadTime = DateTime.UtcNow - loadStartTime;


            // Optionally make a template
            var templateStartTime = DateTime.UtcNow;
            var isTemplate = mTemplateFileExtensionsDict.ContainsKey(extension);
            if (isTemplate)
                uncompressedFile = ProcessTemplate(fullPath, uncompressedFile);
            var templateTime = DateTime.UtcNow - templateStartTime;

            // Save the uncompressed file
            bool alreadyHasCompressedFile = false;
            lock (mLock)
            {
                mFileCache[httpPath] = new FileCache()
                {
                    PathHttp = httpPath,
                    PathFull = fullPath,
                    Extension = extension,
                    LastWriteTimeUtc = fi.LastWriteTimeUtc,
                    Data = uncompressedFile
                };
                alreadyHasCompressedFile = mFileCache.ContainsKey(httpPath + ".gz");
            }

            // GZip for future reference
            var compressStartTime = DateTime.Now;
            byte[] compressedFile = null;
            var isCompressable = mCompressedFileExtensionsDict.ContainsKey(extension);
            if (isCompressable && !alreadyHasCompressedFile)
            {
                var compressedStream = new MemoryStream();
                using (var gz = new GZipStream(compressedStream, CompressionMode.Compress, true))
                    gz.Write(uncompressedFile, 0, uncompressedFile.Length);
                if (compressedStream.Length < uncompressedFile.Length)
                    compressedFile = compressedStream.ToArray();

                lock (mLock)
                {
                    mFileCache[httpPath + ".gz"] = new FileCache()
                    {
                        PathHttp = httpPath + ".gz",
                        PathFull = fullPath,
                        Extension = extension,
                        LastWriteTimeUtc = fi.LastWriteTimeUtc,
                        Data = compressedFile
                    };
                }
            }
            var compressTime = DateTime.Now - compressStartTime;

            // Log file load times
            var compressMessage = "not compressed";
            if (alreadyHasCompressedFile)
                compressMessage = "pre-compressed";
            else if (compressedFile != null && uncompressedFile.Length != 0)
                compressMessage = $"compressed {(uncompressedFile.Length-compressedFile.Length) * 100 / uncompressedFile.Length}%"
                                   + $" in {(int)compressTime.TotalMilliseconds}ms";

            Log.Info($"Loaded '{httpPath}', " 
                + $"{uncompressedFile.Length/1024}kb in {(int)loadTime.TotalMilliseconds}ms, "
                + $"{compressMessage}"
                + (isTemplate ? $", templated in {(int)templateTime.TotalMilliseconds} ms" : ""));

        }

        private void LoadCacheFile(string extension, string path, string hPath)
        {
            if (!File.Exists(path))
                return;
            var fileData = File.ReadAllBytes(path);
            var fi = new FileInfo(path);
            lock (mLock)
            {
                mFileCache[hPath] = new FileCache()
                {
                    PathHttp = hPath,
                    PathFull = path,
                    Extension = extension,
                    LastWriteTimeUtc = fi.LastWriteTimeUtc,
                    Data = fileData
                };
            }
        }



        /// <summary>
        /// For now, don't process these recursively
        /// </summary>
        byte[] ProcessTemplate(string fsPath, byte[] file)
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
