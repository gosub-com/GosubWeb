using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Gosub.Web;
using Newtonsoft.Json;
using System.Security.Cryptography.X509Certificates;

namespace GosubAdmin
{
    class Program
    {
        static readonly string PUBLIC_DIRECTORY = Path.Combine(AppContext.BaseDirectory, "www");
        static readonly string CERTIFICATE_PEM_FILE = "your.pem";
        static readonly string CERTIFICATE_KEY_FILE = "your.key";
        const int ADMIN_PORT = 8059;

        static async Task Main(string[] args)
        {
            Log.Info("Starting application");
            bool startBrowser = false;
            foreach (var v in args)
                if (v == "--start-browser")
                    startBrowser = true;

            var staticFileServer = new StaticFileServer(PUBLIC_DIRECTORY, "");
            var adminApi = new AdminApi();

            // Read TLS certificates
            X509Certificate2 tlsCertificate = null;
            try
            {
                var pem = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, CERTIFICATE_PEM_FILE));
                var key = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, CERTIFICATE_KEY_FILE));
                tlsCertificate = X509Certificate2.CreateFromPem(pem, key);
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading TLS certificate, will not start HTTPS servers: {ex.Message}");
            }


            // Setup server
            var mHttpServer = new HttpServer();
            mHttpServer.HttpHandler += (context) =>
            {
                // Uparade to SSL (TBD: This will be moved to redirect module)
                if (!context.IsSecure && context.LocalEndPoint is IPEndPoint ip && ip.Port == 80)
                {
                    context.Response.Headers["location"] = "https://" + context.Request.HostNoPort + "/" + context.Request.Path;
                    return context.SendResponseAsync("", 301);
                }

                // Do some json API's (these should probably require login with admin privilege)
                if (context.Request.Path == "admin/api/log")
                    return context.SendResponseAsync(Log.GetAsString(200));
                if (context.Request.Path == "admin/api/stats")
                    return context.SendResponseAsync(JsonConvert.SerializeObject(mHttpServer.Stats));

                // Pass everything else to static file server
                return staticFileServer.SendStaticFile(context);
            };

            Start(mHttpServer, new TcpListener(IPAddress.Any, ADMIN_PORT));
            Start(mHttpServer, new TcpListener(IPAddress.Any, 80));
            if (tlsCertificate != null)
            {
                Start(mHttpServer, new TcpListener(IPAddress.Any, 8058), tlsCertificate);
                Start(mHttpServer, new TcpListener(IPAddress.Any, 443), tlsCertificate);
            }

            if (startBrowser)
                Process.Start(new ProcessStartInfo($"http://localhost:{ADMIN_PORT}") { UseShellExecute = true });

            await Task.Delay(Timeout.Infinite);
        }

        static async void Start(HttpServer server, TcpListener listener, X509Certificate2 cert = null)
        {
            try
            {
                await server.Start(listener, cert);
            }
            catch (Exception ex)
            {
                Log.Error($"Error starting server on port {listener.LocalEndpoint}: {ex.Message}");
            }
        }


    }
}
