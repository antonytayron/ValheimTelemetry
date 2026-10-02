using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace ValheimTelemetry
{
    internal sealed class RestServer
    {
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _apiKey;

        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        internal RestServer(
            string bindAddress,
            int port,
            string apiKey)
        {
            _bindAddress = bindAddress;
            _port = port;
            _apiKey = apiKey ?? string.Empty;
        }

        internal void Start()
        {
            if (_running)
                return;

            _listener = new HttpListener();

            string prefix;

            if (string.IsNullOrWhiteSpace(_bindAddress) ||
                _bindAddress == "0.0.0.0" ||
                _bindAddress == "*")
            {
                prefix = $"http://+:{_port}/";
            }
            else
            {
                prefix = $"http://{_bindAddress}:{_port}/";
            }

            _listener.Prefixes.Add(prefix);

            Plugin.Log.LogInfo(
                $"Starting REST API listener on {prefix}");

            _listener.Start();
            _running = true;

            _thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "ValheimTelemetry-HTTP"
            };

            _thread.Start();

            Plugin.Log.LogInfo(
                $"REST API listener started successfully on {prefix}");
        }

        internal void Stop()
        {
            _running = false;

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                    _listener = null;
                }
            }
            catch
            {
            }

            try
            {
                if (_thread != null && _thread.IsAlive)
                    _thread.Join(1000);
            }
            catch
            {
            }

            _thread = null;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext context = null;

                try
                {
                    context = _listener.GetContext();

                    ThreadPool.QueueUserWorkItem(
                        _ => Handle(context));
                }
                catch (HttpListenerException)
                {
                    if (_running)
                        Plugin.Log.LogError(
                            "HTTP listener stopped unexpectedly.");

                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        Plugin.Log.LogError(
                            $"HTTP listener error: {ex}");
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.Headers["Access-Control-Allow-Headers"] =
                    "Content-Type, X-API-Key, Authorization";
                response.Headers["Access-Control-Allow-Methods"] =
                    "GET, OPTIONS";

                if (request.HttpMethod.Equals(
                    "OPTIONS",
                    StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                if (!Authenticate(request))
                {
                    WriteText(
                        response,
                        401,
                        "application/json; charset=utf-8",
                        "{\"error\":\"unauthorized\"}");

                    return;
                }

                string path = request.Url.AbsolutePath;

                if (string.IsNullOrEmpty(path))
                    path = "/";

                path = path.TrimEnd('/');

                if (string.IsNullOrEmpty(path))
                    path = "/";

                if (path.Equals(
                    "/health",
                    StringComparison.OrdinalIgnoreCase))
                {
                    WriteText(
                        response,
                        200,
                        "application/json; charset=utf-8",
                        JsonUtil.Health());

                    return;
                }

                if (path.Equals(
                    "/metrics",
                    StringComparison.OrdinalIgnoreCase))
                {
                    WriteText(
                        response,
                        200,
                        "text/plain; version=0.0.4; charset=utf-8",
                        MetricsService.ToPrometheus());

                    return;
                }

                if (path.Equals(
                    "/api/v1/players",
                    StringComparison.OrdinalIgnoreCase))
                {
                    WriteText(
                        response,
                        200,
                        "application/json; charset=utf-8",
                        JsonUtil.Players());

                    return;
                }

                const string prefix = "/api/v1/players/";

                if (path.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    string id = path.Substring(prefix.Length);

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        WriteText(
                            response,
                            400,
                            "application/json; charset=utf-8",
                            "{\"error\":\"player id is required\"}");

                        return;
                    }

                    try
                    {
                        id = Uri.UnescapeDataString(id);
                    }
                    catch
                    {
                    }

                    PlayerSnapshot snapshot;

                    if (!PlayerService.TryGetPlayer(
                        id,
                        out snapshot))
                    {
                        WriteText(
                            response,
                            404,
                            "application/json; charset=utf-8",
                            "{\"error\":\"player not found\"}");

                        return;
                    }

                    WriteText(
                        response,
                        200,
                        "application/json; charset=utf-8",
                        JsonUtil.Player(snapshot));

                    return;
                }

                WriteText(
                    response,
                    404,
                    "application/json; charset=utf-8",
                    "{\"error\":\"not found\"}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"HTTP request error: {ex}");

                try
                {
                    WriteText(
                        context.Response,
                        500,
                        "application/json; charset=utf-8",
                        "{\"error\":\"internal server error\"}");
                }
                catch
                {
                }
            }
        }

        private bool Authenticate(HttpListenerRequest request)
        {
            if (string.IsNullOrEmpty(_apiKey))
                return true;

            string supplied =
                request.Headers["X-API-Key"];

            if (string.IsNullOrEmpty(supplied))
            {
                string authorization =
                    request.Headers["Authorization"];

                if (!string.IsNullOrEmpty(authorization) &&
                    authorization.StartsWith(
                        "Bearer ",
                        StringComparison.OrdinalIgnoreCase))
                {
                    supplied =
                        authorization.Substring(
                            "Bearer ".Length).Trim();
                }
            }

            return !string.IsNullOrEmpty(supplied) &&
                   string.Equals(
                       supplied,
                       _apiKey,
                       StringComparison.Ordinal);
        }

        private static void WriteText(
            HttpListenerResponse response,
            int statusCode,
            string contentType,
            string text)
        {
            byte[] bytes =
                Encoding.UTF8.GetBytes(text ?? string.Empty);

            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            response.KeepAlive = false;

            using (Stream output = response.OutputStream)
            {
                output.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
