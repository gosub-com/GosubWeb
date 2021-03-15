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
        static readonly string CERTIFICATE_PEM_FILE = "certificate.pem";
        static readonly string CERTIFICATE_KEY_FILE = "certificate.key";
        const int ADMIN_PORT = 8059;

        static async Task Main(string[] args)
        {
            Log.Info("Starting application");
            bool startBrowser = false;
            foreach (var v in args)
                if (v == "--start-browser")
                    startBrowser = true;

            var staticFileServer = new StaticFileServer();
            staticFileServer.SetRoot(PUBLIC_DIRECTORY, "");
            var adminApi = new AdminApi();

            var redirect = new Redirect();
            //redirect.UpgradeInsecure = true;

            // Setup server
            var mHttpServer = new HttpServer();
            mHttpServer.HttpHandler += async (context) =>
            {
                if (await redirect.RedirectRequest(context))
                    return;

                // Do some json API's (these should probably require login with admin privilege)
                if (context.Request.Path == "admin/api/log")
                {
                    await context.SendResponseAsync(Log.GetAsString(200));
                    return;
                }
                if (context.Request.Path == "admin/api/stats")
                {
                    await context.SendResponseAsync(JsonConvert.SerializeObject(mHttpServer.Stats));
                    return;
                }

                // Pass everything else to static file server
                await staticFileServer.SendStaticFile(context);
            };

            // Start HTTP ports
            Start(mHttpServer, new TcpListener(IPAddress.Any, ADMIN_PORT));
            Start(mHttpServer, new TcpListener(IPAddress.Any, 80));

            // Start HTTPS ports
            try
            {
                var pem = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, CERTIFICATE_PEM_FILE));
                var key = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, CERTIFICATE_KEY_FILE));
                var tlsCertificate = X509Certificate2.CreateFromPem(pem, key);
                Start(mHttpServer, new TcpListener(IPAddress.Any, 8058), tlsCertificate);
                Start(mHttpServer, new TcpListener(IPAddress.Any, 443), tlsCertificate);
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading TLS certificate, will not start HTTPS servers: {ex.Message}");
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
