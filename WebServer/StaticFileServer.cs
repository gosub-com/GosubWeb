using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;

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
        const string DEFAULT_TEMPLATE_FILE_EXTENSIONS = "html;htm;css;js";
        const string DEFAULT_COMPRESSED_FILE_EXTENSIONS = "html;htm;css;js;svg;ttf;otf;xml;json";

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
        string mTemplateStartString = "${{";
        string mTemplateEndString = "}}";
        string mTemplateFileExtensions = "";
        Dictionary<string, bool> mTemplateFileExtensionsDict = new Dictionary<string, bool>();
        string mCompressedFileExtensions = "";
        Dictionary<string, bool> mCompressedFileExtensionsDict = new Dictionary<string, bool>();
        Dictionary<string, FileCache> mFileCache = new Dictionary<string, FileCache>();

        class FileCache
        {
            public string PathLower = ""; // As specified by URL, but in lower case
            public string PathCase = "";
            public DateTime LastWriteTimeUtc;
            public string[] Dependencies = Array.Empty<string>();
            public byte[] Cached; // Null if invalid or not loaded from disk
            public byte[] CachedGzip;
            public override string ToString() { return PathLower; }
        }


        public readonly string FileSystemRoot = "";

        /// <summary>
        /// When true (the default), re-write the browser URL to send them to
        /// case proper URL.
        /// </summary>
        public bool RewriteCase = true;

        /// <summary>
        /// Non-template/non-compressable files are cached if they are under MaxCacheSize.
        /// Template and compressable files are cached regardless of size.
        /// </summary>
        public int MaxCacheSize = 80000;

        /// <summary>
        /// Serve files from fileSystemRoot directory.  The request must
        /// start with `webRoot`, which is then stripped from the request path.
        /// Logs an error if the directory doesn't exist.
        /// Use `TemplateFileExtensions` to process server side includes
        /// in the file.
        /// </summary>
        public StaticFileServer(string fileSystemRoot, string webRoot)
        {
            TemplateFileExtensions = DEFAULT_TEMPLATE_FILE_EXTENSIONS;
            CompressedFileExtensions = DEFAULT_COMPRESSED_FILE_EXTENSIONS;
            FileSystemRoot = Path.GetFullPath(fileSystemRoot);
            try
            {
                if (!Directory.Exists(FileSystemRoot))
                {
                    Log.Error($"Can't serve files from '{FileSystemRoot}, directory doesn't exist'");
                    return;
                }
                EnumerateFiles(FileSystemRoot);
            }
            catch (Exception ex)
            {
                Log.Error($"Can't serve files from '{FileSystemRoot}, error='{ex.Message}'");
                return;
            }
            Log.Info($"Serving {mFileCache.Count} files from '{FileSystemRoot}'");
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
                    FlushCache();
                    foreach (var extension in value.ToLower().Split(';'))
                        mTemplateFileExtensionsDict[extension] = true;
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
                    FlushCache();
                    foreach (var extension in value.ToLower().Split(';'))
                        mCompressedFileExtensionsDict[extension] = true;
                }
            }
        }

        /// <summary>
        /// Call this if anything on the file system changes.
        /// </summary>
        public void FlushCache(bool reEnumerate = false)
        {
            lock (mLock)
            {
                if (reEnumerate)
                {
                    EnumerateFiles(FileSystemRoot);
                }
                else
                {
                    foreach (var fi in mFileCache.Values)
                    {
                        fi.Cached = null;
                    }
                }
            }
        }
        void EnumerateFiles(string root)
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
                EnumerateFiles(dir);
            foreach (var path in Directory.EnumerateFiles(root))
                AddFile(path);
            return;

            void AddFile(string path)
            {
                var pathLower = path.ToLower();
                var fi = new FileInfo(path);
                lock (mLock)
                {
                    if (!mFileCache.TryGetValue(pathLower, out var fc))
                        fc = new FileCache();
                    fc.PathLower = pathLower;
                    fc.PathCase = path;
                    if (fi.LastWriteTimeUtc != fc.LastWriteTimeUtc)
                    {
                        fc.Cached = null;
                        fc.CachedGzip = null;
                    }
                    fc.LastWriteTimeUtc = fi.LastWriteTimeUtc;
                    mFileCache[pathLower] = fc;
                }
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

            // Choose "index.html" if no name is given
            var extension = request.Extension;
            if (path.Length == 0)
            {
                path = "index.html";
                extension = "html";
            }

            if (MimeTypes.TryGetValue(extension, out string contentType))
                context.Response.ContentType = contentType;

            var (file, isGzipped) = GetFile(path, extension, context.Request.AcceptEncoding.Contains("gzip"));
            if (file != null)
            {
                if (isGzipped)
                    context.Response.ContentEncoding = "gzip";
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
        /// Returns (file, isGZipped), where file can be NULL
        /// </summary>
        (byte [], bool) GetFile(string path, string extension, bool allowGzip)
        {
            var pathLower = path.ToLower();
            var isTemplate = false;
            var isCompressable = false;
            lock (mLock)
            {
                if (mFileCache.TryGetValue(pathLower, out var fcc))
                {
                    // Cache hit
                    // TBD: Periodically check to see if file on disk was changed.
                    if (fcc.CachedGzip != null && allowGzip)
                        return (fcc.CachedGzip, true);
                    if (fcc.Cached != null)
                        return (fcc.Cached, false);
                }
                isTemplate = mTemplateFileExtensionsDict.ContainsKey(extension);
                isCompressable = mCompressedFileExtensionsDict.ContainsKey(extension);
            }

            var fsPath = Path.Combine(FileSystemRoot, path);
            if (!File.Exists(fsPath))
                return (null, false);
            var fi = new FileInfo(fsPath);
            if (!isTemplate && !isCompressable && fi.Length > MaxCacheSize)
                return (null, false);

            var startTime = DateTime.UtcNow;
            var uncompressedFile = File.ReadAllBytes(fsPath);
            var readTime = DateTime.UtcNow;
            if (isTemplate)
                uncompressedFile = ProcessTemplate(fsPath, uncompressedFile);
            var templateTime = DateTime.UtcNow;

            // GZip for future reference
            byte[] compressedFile = null;
            if (isCompressable)
            {
                var compressedStream = new MemoryStream();
                using (var gz = new GZipStream(compressedStream, CompressionMode.Compress, true))
                    gz.Write(uncompressedFile, 0, uncompressedFile.Length);
                if (compressedStream.Length < uncompressedFile.Length)
                    compressedFile = compressedStream.ToArray();
            }
            var compressTime = DateTime.UtcNow;


            var compressRatio = "";
            if (compressedFile != null && uncompressedFile.Length != 0)
                compressRatio = $" ({(uncompressedFile.Length-compressedFile.Length) * 100 / uncompressedFile.Length}%)";
            Log.Info($"Load '{Path.GetFileName(path)}', {uncompressedFile.Length/1000 } kb in {(int)(readTime - startTime).TotalMilliseconds} ms"
                    + (isTemplate ? $", templated in {(int)(templateTime - readTime).TotalMilliseconds} ms" : "")
                    + (isCompressable ? $", compressed{compressRatio} in {(int)(compressTime-templateTime).TotalMilliseconds} ms" : ""));

            var fc = new FileCache();
            fc.PathLower = path;
            fc.Cached = uncompressedFile;
            fc.CachedGzip = compressedFile;
            fc.LastWriteTimeUtc = fi.LastWriteTimeUtc;
            lock (mLock)
                mFileCache[pathLower] = fc;

            if (allowGzip && compressedFile != null)
                return (compressedFile, true);
            return (uncompressedFile, false);
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
