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

namespace GosubAdmin
{
    class Program
    {
        static readonly string PUBLIC_DIRECTORY = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "www");
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

            // Setup server
            var mHttpServer = new HttpServer();
            mHttpServer.HttpHandler += (context) =>
            {
                // This lambda called for each request, returns a new task
                var ep = context.LocalEndPoint as IPEndPoint;
                if (context.Request.Path == "/admin/log")
                    return context.SendResponseAsync(Log.GetAsString(200));


                if (context.Request.Path == "/admin/api/stats")
                {
                    return context.SendResponseAsync(JsonConvert.SerializeObject(mHttpServer.Stats));
                }

                return staticFileServer.SendStaticFile(context);
            };

            mHttpServer.Start(new TcpListener(IPAddress.Any, ADMIN_PORT));
            if (startBrowser)
                Process.Start(new ProcessStartInfo($"http://localhost:{ADMIN_PORT}") { UseShellExecute = true });

            await Task.Delay(Timeout.Infinite);
        }

    }
}
