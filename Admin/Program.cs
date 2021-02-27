// Copyright (C) 2021 by Jeremy Spiller, all rights reserved.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using Gosub.Http;

namespace Gosub.Viewtop
{
    static class Program
    {
        const int ADMIN_PORT = 8059;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        async static Task Main(string[] args)
        {
            Log.Write("Starting application");
            bool startBrowser = false;
            foreach (var v in args)
                if (v == "--start-browser")
                    startBrowser = true;

            // Setup server
            var mHttpServer = new HttpServer();
            mHttpServer.HttpHandler += (context) =>
            {
                // This lambda called for each request, returns a new task
                return context.SendResponseAsync("Hello world!");
            };

            mHttpServer.Start(new TcpListener(IPAddress.Any, ADMIN_PORT));
            if (startBrowser)
                Process.Start(new ProcessStartInfo($"http://localhost:{ADMIN_PORT}") { UseShellExecute = true });

            await Task.Delay(Timeout.Infinite);
        }
    }
}
