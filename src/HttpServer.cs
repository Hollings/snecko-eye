using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SneckoEye;

public static class HttpServer
{
    private static HttpListener? _listener;
    private static Thread? _thread;
    private static volatile bool _running;
    private const int Port = 9000;

    public static void Start()
    {
        if (_running) return;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _running = true;

        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "SneckoEye-HTTP"
        };
        _thread.Start();

        ModEntry.Log($"HTTP server started on port {Port}");
    }

    public static void Stop()
    {
        _running = false;
        _listener?.Stop();
        _listener?.Close();
        ModEntry.Log("HTTP server stopped");
    }

    private static void ListenLoop()
    {
        while (_running)
        {
            try
            {
                var context = _listener!.GetContext();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException) when (!_running)
            {
                // Expected on shutdown
            }
            catch (ObjectDisposedException) when (!_running)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                ModEntry.Log("HTTP error: " + ex.Message);
            }
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            response.ContentType = "application/json";
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";
            string result;

            switch (path)
            {
                case "/state":
                    if (request.HttpMethod != "GET")
                    {
                        result = Error("Method not allowed. Use GET.", 405);
                        response.StatusCode = 405;
                    }
                    else
                    {
                        result = RunOnMainThread(() => GameStateReader.ReadState());
                    }
                    break;

                case "/action":
                    if (request.HttpMethod != "POST")
                    {
                        result = Error("Method not allowed. Use POST.", 405);
                        response.StatusCode = 405;
                    }
                    else
                    {
                        string body = ReadBody(request);
                        result = RunOnMainThread(() => ActionExecutor.Execute(body));
                    }
                    break;

                case "/log":
                    if (request.HttpMethod == "POST")
                    {
                        string logBody = ReadBody(request);
                        result = HandleLog(logBody);
                    }
                    else if (request.HttpMethod == "DELETE")
                    {
                        EventLog.Clear();
                        result = "{\"success\": true}";
                    }
                    else
                    {
                        result = Error("Method not allowed. Use POST or DELETE.", 405);
                        response.StatusCode = 405;
                    }
                    break;

                case "/help":
                case "/":
                    result = ApiSchema.GetSchema();
                    break;

                default:
                    response.StatusCode = 404;
                    result = Error($"Unknown endpoint: {path}. Use /state, /action, /log, or /help.", 404);
                    break;
            }

            byte[] buffer = Encoding.UTF8.GetBytes(result);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            ModEntry.Log("Request handler error: " + ex.Message);
            try
            {
                response.StatusCode = 500;
                byte[] buffer = Encoding.UTF8.GetBytes(Error(ex.Message, 500));
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch { }
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    /// <summary>
    /// Runs a function on Godot's main thread and blocks until it completes.
    /// Required because game state can only be accessed from the main thread.
    /// </summary>
    private static string RunOnMainThread(Func<string> func)
    {
        if (Thread.CurrentThread == _mainThread)
            return func();

        var tcs = new TaskCompletionSource<string>();

        Godot.Callable.From(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }).CallDeferred();

        // Block HTTP thread until main thread completes the work
        return tcs.Task.GetAwaiter().GetResult();
    }

    private static Thread? _mainThread;

    /// <summary>
    /// Call from ModEntry.Initialize() (which runs on main thread) to capture
    /// the main thread reference.
    /// </summary>
    public static void CaptureMainThread()
    {
        _mainThread = Thread.CurrentThread;
    }

    private static string HandleLog(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            string message = "";
            if (root.TryGetProperty("message", out var msgProp))
                message = msgProp.GetString() ?? "";

            string tag = "";
            if (root.TryGetProperty("tag", out var tagProp))
                tag = tagProp.GetString() ?? "";

            if (string.IsNullOrEmpty(message))
                return "{\"success\": false, \"error\": \"Missing 'message' field\"}";

            if (!string.IsNullOrEmpty(tag))
                EventLog.Log(tag, message);
            else
                EventLog.Log(message);

            return "{\"success\": true}";
        }
        catch (Exception ex)
        {
            return $"{{\"success\": false, \"error\": \"{EscapeJson(ex.Message)}\"}}";
        }
    }

    private static string ReadBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return reader.ReadToEnd();
    }

    private static string Error(string message, int code)
    {
        return $"{{\"error\": \"{EscapeJson(message)}\", \"code\": {code}}}";
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}
