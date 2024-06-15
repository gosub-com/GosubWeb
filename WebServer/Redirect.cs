using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;

namespace Gosub.Web
{
    /// <summary>
    /// Pass the context through this class to redirect URL.
    /// When it returns TRUE, the request was handled as a redirect.
    /// </summary>
    public class Redirect
    {
        object mLock = new object();

        /// <summary>
        /// When true, requests to port 80 (HTTP) are redirected to HTTPS
        /// </summary>
        public bool UpgradeInsecure;

        Dictionary<string, string> mRedirects = new Dictionary<string, string>();

        /// <summary>
        /// Add a redirect.  The source URL should not start or end with '/',
        /// but the destination URL should start with '/'
        /// </summary>
        public void Add(string source, string dest)
        {
            // TBD: Add to validation
            if (source.Length != 0)
            {
                if (source[0] == '/' || source[source.Length - 1] == '/')
                    throw new Exception($"Invalid source redirect: '{source}'");
            }
            if (dest.Length == 0 || dest[0] != '/')
                throw new Exception($"Invalid destination redirect: {dest}");
            lock (mLock)
                mRedirects[source.ToLower()] = dest;
        }

        public void Remove(string source)
        {
            lock (mLock)
                mRedirects.Remove(source.ToLower());
        }

        public void Add(Dictionary<string, string> redirects)
        {
            foreach (var redirect in redirects)
                Add(redirect.Key, redirect.Value);
        }

        /// <summary>
        /// Returns TRUE if the request was redirected
        /// </summary>
        public async ValueTask<bool> RedirectRequest(HttpContext context)
        {
            // Uparade to TLS
            if (UpgradeInsecure && context.LocalEndPoint is IPEndPoint ip && ip.Port == 80)
            {
                context.Response.Headers["location"] = "https://" + context.Request.HostNoPort + "/" + context.Request.Path;
                await context.SendResponseAsync("", 301);
                return true;
            }

            string redirect;
            lock (mLock)
                if (!mRedirects.TryGetValue(context.Request.PathLower, out redirect))
                    return false;

            context.Response.Headers["location"] = redirect;
            await context.SendResponseAsync("", 301);
            return true;
        }
    }
}
