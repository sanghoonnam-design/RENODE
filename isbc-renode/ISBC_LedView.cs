using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class ISBC_LedView : IGPIOReceiver, IDisposable
    {
        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= levels.Length)
            {
                return;
            }
            lock(sync)
            {
                if(levels[number].HasValue && levels[number].Value != value)
                {
                    changes[number]++;
                }
                levels[number] = value;
            }
        }

        public void Reset()
        {
            lock(sync)
            {
                Array.Clear(levels, 0, levels.Length);
                Array.Clear(changes, 0, changes.Length);
            }
        }

        public void OpenHttp(string directory, int port)
        {
            if(listener != null)
            {
                throw new RecoverableException("The ISBC board viewer is already open.");
            }

            var assets = new Dictionary<string, Asset>();
            foreach(var route in Files)
            {
                var file = Path.Combine(directory, route.Value.Path);
                assets.Add(route.Key, new Asset(File.ReadAllBytes(file), route.Value.ContentType));
            }

            var http = new HttpListener();
            http.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                http.Start();
            }
            catch(HttpListenerException error)
            {
                http.Close();
                throw new RecoverableException($"Could not open the ISBC board viewer on port {port}: {error.Message}");
            }
            listener = http;
            _ = Serve(http, assets);
        }

        public void Dispose()
        {
            listener?.Close();
            listener = null;
        }

        private async Task Serve(HttpListener http, Dictionary<string, Asset> assets)
        {
            while(http.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await http.GetContextAsync();
                }
                catch(HttpListenerException)
                {
                    return;
                }
                catch(ObjectDisposedException)
                {
                    return;
                }

                _ = Reply(context, assets);
            }
        }

        private async Task Reply(HttpListenerContext context, Dictionary<string, Asset> assets)
        {
            using(var response = context.Response)
            {
                if(context.Request.HttpMethod != "GET" && context.Request.HttpMethod != "HEAD")
                {
                    response.StatusCode = 405;
                    return;
                }

                var path = context.Request.Url.AbsolutePath;
                Asset asset;
                if(path == "/state")
                {
                    asset = new Asset(Snapshot(), "application/json");
                    response.Headers["Cache-Control"] = "no-store";
                }
                else if(!assets.TryGetValue(path, out asset))
                {
                    response.StatusCode = 404;
                    return;
                }

                response.ContentType = asset.ContentType;
                response.ContentLength64 = asset.Data.Length;
                if(context.Request.HttpMethod == "GET")
                {
                    try
                    {
                        await response.OutputStream.WriteAsync(asset.Data, 0, asset.Data.Length);
                    }
                    catch(HttpListenerException)
                    {
                        // The visitor closed or refreshed the viewer.
                    }
                    catch(IOException)
                    {
                        // The browser disconnected during the write.
                    }
                }
            }
        }

        private byte[] Snapshot()
        {
            var running = EmulationManager.Instance.CurrentEmulation.IsStarted;
            lock(sync)
            {
                var leds = new object[levels.Length];
                for(var index = 0; index < levels.Length; index++)
                {
                    bool? low = !levels[index];
                    leds[index] = new { id = index, on = low, high = levels[index], changes = changes[index] };
                }
                return JsonSerializer.SerializeToUtf8Bytes(new {
                    running = running,
                    leds = leds
                });
            }
        }

        private sealed record Asset(byte[] Data, string ContentType);

        private readonly object sync = new object();
        private readonly bool?[] levels = new bool?[4];
        private readonly uint[] changes = new uint[4];
        private HttpListener listener;

        private static readonly Dictionary<string, (string Path, string ContentType)> Files
            = new Dictionary<string, (string Path, string ContentType)>
        {
            ["/"] = ("index.html", "text/html; charset=utf-8"),
            ["/board.css"] = ("board.css", "text/css; charset=utf-8"),
            ["/board.js"] = ("board.js", "text/javascript; charset=utf-8"),
            ["/board-top.png"] = ("board-top.png", "image/png"),
            ["/three.module.min.js"] = ("vendor/build/three.module.min.js", "text/javascript; charset=utf-8"),
            ["/OrbitControls.js"] = ("vendor/examples/jsm/controls/OrbitControls.js", "text/javascript; charset=utf-8"),
        };
    }
}
