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
    /// Class to server static files from a root directory, and optionally
    /// do server side includes, when  `TemplateFileExtensions` is set.
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
        string mDefaultFileName = "index";
        string mDefaultFileExtension = "html";
        string mTemplateStartString = "${{";
        string mTemplateEndString = "}}";
        string mTemplateFileExtensions = "";
        Dictionary<string, bool> mTemplateFileExtensionsDict = new Dictionary<string, bool>();
        string mCompressedFileExtensions = "";
        Dictionary<string, bool> mCompressedFileExtensionsDict = new Dictionary<string, bool>();
        Dictionary<string, FileCache> mFileCache = new Dictionary<string, FileCache>();

        class FileCache
        {
            public string PathLower = ""; // As specified by HTTP URL, but in lower case
            public string PathCase = ""; // As specified by HTTP URL
            public string PathOs = "";  // On disk
            public string Extension = "";
            public DateTime LastWriteTimeUtc;
            public DateTime UpdatedTime;
            public string[] Dependencies = Array.Empty<string>();
            public byte[] Cached;
            public byte[] CachedGzip;
            public override string ToString() { return PathCase; }
        }


        public string FileSystemRoot { get; private set;  } = "";

        /// <summary>
        /// When true (the default), re-write the browser URL to send them to
        /// case proper URL.
        /// </summary>
        public bool RewriteCase = true;

        /// <summary>
        /// Non-template/non-compressable files are cached if they are under MaxCacheSize.
        /// Template and compressable files are cached regardless of size.
        /// </summary>
        public int MaxCacheSize = 120000;

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
                EnumerateFiles(FileSystemRoot);
            }
            catch (Exception ex)
            {
                Log.Error($"Can't serve files from '{FileSystemRoot}, error='{ex.Message}'");
                return;
            }
            Log.Info($"Serving {mFileCache.Count} files from '{FileSystemRoot}'");
        }

        void EnumerateFiles(string root)
        {
            var fileCache = new Dictionary<string, FileCache>();
            EnumerateFilesRecurse(root);
            lock (mLock)
                mFileCache = fileCache;
            return;

            void EnumerateFilesRecurse(string dir)
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                    EnumerateFilesRecurse(d);
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    var extension = Path.GetExtension(path).ToLower().Replace(".", "");
                    var fc = new FileCache();
                    var fi = new FileInfo(path);
                    fc.LastWriteTimeUtc = fi.LastWriteTimeUtc;
                    fc.PathOs = path;
                    fc.Extension = extension;

                    // Account for default extension and default file name
                    var p = path;
                    if (extension == DefaultFileExtension)
                        p = path.Substring(0, p.Length - extension.Length - 1);
                    if (Path.GetFileNameWithoutExtension(p).ToLower() == DefaultFileName)
                        p = path.Substring(0, Math.Max(0, p.Length - DefaultFileName.Length-1));
                    fc.PathCase = Path.GetRelativePath(root, p).Replace("\\", "/");
                    if (fc.PathCase == ".")
                        fc.PathCase = "";
                    fc.PathLower = fc.PathCase.ToLower();
                    if (fileCache.TryGetValue(fc.PathLower, out var fcerror))
                    {
                        Log.Error($"Conflicting or duplicate names: '{fc.PathOs}' and '{fcerror.PathOs}'");
                        continue;
                    }
                    fileCache[fc.PathLower] = fc;
                }
            }

        }

        void FlushCache()
        {
            lock (mLock)
            {
                foreach (var fi in mFileCache.Values)
                {
                    fi.Cached = null;
                    fi.CachedGzip = null;
                    fi.UpdatedTime = new DateTime();
                }
            }
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

        public string DefaultFileName
        {
            get { return mDefaultFileName; }
            set
            {
                if (value == mDefaultFileName)
                    return;
                mDefaultFileName = value.ToLower();
                EnumerateFiles(FileSystemRoot);
            }
        }

        public string DefaultFileExtension
        {
            get { return mDefaultFileExtension; }
            set
            {
                if (value == mDefaultFileExtension)
                    return;
                mDefaultFileExtension = value.ToLower();
                EnumerateFiles(FileSystemRoot);
            }
        }


        /// <summary>
        /// Serve the file, or send invalid response if not in the file system.
        /// </summary>
        public async Task SendStaticFile(HttpContext context)
        {
            var request = context.Request;
            if (request.Method != "GET")
            {
                await context.SendResponseAsync("Invalid HTTP request: Only GET method is allowed for serving", 405);
                return;
            }

            // Check illegal file names (outside of the public directory, invisible files, windows path, etc.)
            string path = request.PathLower;
            if (path.Contains("..") || path.StartsWith(".") || path.Contains("/.") || path.Contains("//") || path.Contains("\\"))
            {
                await context.SendResponseAsync("Invalid Request: File name is invalid", 400);
                return;
            }

            var fileCache = GetFile(path, request.Extension);

            if (fileCache == null)
            {
                Log.Debug($"File not found '{context.Request.Path}', referer='{context.Request.Referer}'");
                await context.SendResponseAsync("File not found: " + path, 404);
                return;
            }
            if (MimeTypes.TryGetValue(fileCache.Extension, out string contentType))
                context.Response.ContentType = contentType;

            if (fileCache.Cached != null)
            {
                bool gzipped = fileCache.CachedGzip != null && context.Request.AcceptEncoding.Contains("gzip");
                if (gzipped)
                    context.Response.ContentEncoding = "gzip";
                await context.SendResponseAsync(gzipped ? fileCache.CachedGzip : fileCache.Cached);
                Log.Debug($"Serving '{context.Request.Path}' (cache, {(gzipped ? "compressed" : "uncompressed")})");
                return;
            }

            if (!File.Exists(fileCache.PathOs))
            {
                Log.Debug($"File not found '{context.Request.Path}', referer='{context.Request.Referer}'");
                await context.SendResponseAsync("File not found: " + path, 404);
                return;
            }

            Log.Debug($"Serving '{context.Request.Path}' (disk)");
            using (var stream = File.OpenRead(fileCache.PathOs))
                await context.GetWriter(stream.Length).WriteAsync(stream);
        }

        FileCache GetFile(string path, string extension)
        {
            var pathLower = path.ToLower();
            var isTemplate = false;
            var isCompressable = false;
            FileCache fileCache;
            lock (mLock)
            {
                // Not found, or cache hit
                if (!mFileCache.TryGetValue(pathLower, out fileCache))
                    return null;
                if (fileCache.UpdatedTime != new DateTime())
                    return fileCache;

                isTemplate = mTemplateFileExtensionsDict.ContainsKey(fileCache.Extension);
                isCompressable = mCompressedFileExtensionsDict.ContainsKey(fileCache.Extension);
            }

            if (!File.Exists(fileCache.PathOs))
                return null;
            var fi = new FileInfo(fileCache.PathOs);
            if (!isTemplate && !isCompressable && fi.Length > MaxCacheSize)
            {
                lock (mLock)
                    fileCache.UpdatedTime = DateTime.UtcNow;
                Log.Info($"Load '{Path.GetFileName(path)}' - No cache");
                return fileCache;
            }

            var startTime = DateTime.UtcNow;
            var uncompressedFile = File.ReadAllBytes(fileCache.PathOs);
            var readTime = DateTime.UtcNow;
            if (isTemplate)
                uncompressedFile = ProcessTemplate(fileCache.PathOs, uncompressedFile);
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

            // Log file compression info
            var compressTime = DateTime.UtcNow;
            var compressRatio = "";
            if (compressedFile != null && uncompressedFile.Length != 0)
                compressRatio = $" ({(uncompressedFile.Length-compressedFile.Length) * 100 / uncompressedFile.Length}%)";
            Log.Info($"Load '{Path.GetFileName(path)}', {uncompressedFile.Length/1000 } kb in {(int)(readTime - startTime).TotalMilliseconds} ms"
                    + (isTemplate ? $", templated in {(int)(templateTime - readTime).TotalMilliseconds} ms" : "")
                    + (isCompressable ? $", compressed{compressRatio} in {(int)(compressTime-templateTime).TotalMilliseconds} ms" : ""));

            lock (mLock)
            {
                fileCache.Cached = uncompressedFile;
                fileCache.CachedGzip = compressedFile;
                fileCache.LastWriteTimeUtc = fi.LastWriteTime;
                fileCache.UpdatedTime = DateTime.Now;
            }
            return fileCache;
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
