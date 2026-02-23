using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RevitChat.Services
{
    public class McpServer : IDisposable
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private readonly Func<string, Dictionary<string, object>, string> _toolExecutor;
        private readonly int _port;

        public bool IsRunning { get; private set; }

        public McpServer(int port, Func<string, Dictionary<string, object>, string> toolExecutor)
        {
            _port = port;
            _toolExecutor = toolExecutor;
        }

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            IsRunning = true;
            Task.Run(() => ListenLoop(_cts.Token));
        }

        public void Stop()
        {
            IsRunning = false;
            _cts?.Cancel();
            try { _listener?.Stop(); _listener?.Close(); } catch { }
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[McpServer] ListenLoop: {ex.Message}"); }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/health")
                {
                    WriteResponse(response, 200, JsonSerializer.Serialize(new { status = "ok", port = _port }));
                    return;
                }

                if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/tools/call")
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                    {
                        WriteResponse(response, 400, JsonSerializer.Serialize(new { error = "Missing or invalid 'name' property." }));
                        return;
                    }

                    string toolName = nameEl.GetString();
                    if (string.IsNullOrWhiteSpace(toolName))
                    {
                        WriteResponse(response, 400, JsonSerializer.Serialize(new { error = "Tool name cannot be empty." }));
                        return;
                    }

                    var argsDict = new Dictionary<string, object>();
                    if (root.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in argsEl.EnumerateObject())
                            argsDict[prop.Name] = prop.Value;
                    }

                    var result = _toolExecutor(toolName, argsDict);
                    WriteResponse(response, 200, result);
                    return;
                }

                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/tools/list")
                {
                    WriteResponse(response, 200, JsonSerializer.Serialize(new { message = "POST to /tools/execute with {\"tool\":\"name\",\"args\":{}} to execute a tool." }));
                    return;
                }

                WriteResponse(response, 404, JsonSerializer.Serialize(new { error = "Not found" }));
            }
            catch (Exception ex)
            {
                try
                {
                    WriteResponse(context.Response, 500, JsonSerializer.Serialize(new { error = ex.Message }));
                }
                catch (Exception innerEx) { System.Diagnostics.Debug.WriteLine($"[McpServer] HandleRequest: {innerEx.Message}"); }
            }
        }

        private static void WriteResponse(HttpListenerResponse response, int statusCode, string body)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            var buffer = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
