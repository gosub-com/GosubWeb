using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Gosub.Web;

namespace GosubAdmin
{
    class Program
    {
        static string VERSION = "0.0.5";

        static readonly string PUBLIC_DIRECTORY = Path.Combine(AppContext.BaseDirectory, "www");
        static readonly string REDIRECTS_FILE_NAME = Path.Combine(AppContext.BaseDirectory, "redirects.txt");
        static readonly string PEM_FILE_NAME = Path.Combine(AppContext.BaseDirectory, "fullchain.pem");
        static readonly string KEY_FILE_NAME = Path.Combine(AppContext.BaseDirectory, "privatekey.pem");
        const int ADMIN_PORT = 8059;

        static async Task Main(string[] args)
        {
            Log.Info($"Starting Gosub Web Server, version {VERSION}");
            bool startBrowser = false;
            foreach (var v in args)
                if (v == "--start-browser")
                    startBrowser = true;

            var staticFileServer = new StaticFileServer();
            staticFileServer.SetRoot(PUBLIC_DIRECTORY, "");

            var redirect = new Redirect();
            AddRedirectsFile(redirect);

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
                    await context.SendResponseAsync(JsonSerializer.Serialize(mHttpServer.Stats));
                    return;
                }
                if (context.Request.Path == "admin/api/files")
                {
                    await context.SendResponseAsync(staticFileServer.GetLog());
                    return;
                }

                // Allow godot games to use shared buffer array
                context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";

                // Pass everything else to static file server
                await staticFileServer.SendFile(context);
            };

            // Start HTTP ports
            Start(mHttpServer, new TcpListener(IPAddress.Any, ADMIN_PORT));
            Start(mHttpServer, new TcpListener(IPAddress.Any, 80));

            // Start HTTPS ports
            try
            {
                if (!File.Exists(PEM_FILE_NAME) || !File.Exists(KEY_FILE_NAME))
                    throw new Exception($"Certificate files not found.  Expecting to find '{PEM_FILE_NAME}' and '{KEY_FILE_NAME}'");

                var pem = File.ReadAllText(PEM_FILE_NAME);
                var key = File.ReadAllText(KEY_FILE_NAME);
                var tlsCertificate = X509Certificate2.CreateFromPem(pem, key);
                Start(mHttpServer, new TcpListener(IPAddress.Any, 8058), tlsCertificate);
                Start(mHttpServer, new TcpListener(IPAddress.Any, 443), tlsCertificate);

                // Send all HTTP requests to HTTPS
                redirect.UpgradeInsecure = true;
            }
            catch (Exception ex)
            {
                Log.Error($"Can't start HTTPS servers: {ex.Message}");
            }

            if (startBrowser)
                Process.Start(new ProcessStartInfo($"http://localhost:{ADMIN_PORT}") { UseShellExecute = true });

            await Task.Delay(Timeout.Infinite);
        }

        private static void AddRedirectsFile(Redirect redirect)
        {
            try
            {
                foreach (var line in File.ReadAllLines(REDIRECTS_FILE_NAME))
                {
                    var fileNames = line.Split(" ");
                    if (fileNames.Length == 2)
                        redirect.Add(fileNames[0], fileNames[1]);
                    else
                        Console.WriteLine($"Error processing redirect file line: '{line}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR processing redirects:{ex.Message}");
            }
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
