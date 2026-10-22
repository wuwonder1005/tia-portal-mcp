using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TiaMcp
{
    /// <summary>
    /// Minimal localhost HTTP/1.1 server speaking MCP streamable-http.
    /// A TcpListener is used instead of HttpListener so that no URL ACL
    /// (netsh http add urlacl) or elevation is required to bind.
    /// Connections are handled on worker threads and support keep-alive,
    /// because MCP clients reuse sockets between calls.
    /// </summary>
    public class HttpMcpServer
    {
        private readonly int port;
        private readonly McpServer mcp = new McpServer();
        private readonly object gate = new object();

        public HttpMcpServer(int port)
        {
            this.port = port;
        }

        public void Run()
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            catch { }

            // A just-killed previous instance can still hold the port for a moment.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                try
                {
                    listener.Start();
                    break;
                }
                catch (SocketException)
                {
                    if (DateTime.UtcNow > deadline) throw;
                    Program.Log("port " + port + " busy, retrying...");
                    System.Threading.Thread.Sleep(1000);
                }
            }
            Program.Log("http mcp listening on http://127.0.0.1:" + port + "/mcp");
            Console.Out.WriteLine("TIA MCP server listening on http://127.0.0.1:" + port + "/mcp");
            Console.Out.WriteLine("Leave this window open. Press Ctrl+C to stop.");
            Console.Out.Flush();

            while (true)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    Program.Log("accept failed: " + ex.Message);
                    continue;
                }

                var accepted = client;
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    var c = (TcpClient)state;
                    try
                    {
                        using (c)
                        {
                            c.ReceiveTimeout = 20000;
                            c.SendTimeout = 20000;
                            c.NoDelay = true;
                            HandleConnection(c);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Log("connection failed: " + Engine.Flatten(ex));
                    }
                }, accepted);
            }
        }

        private void HandleConnection(TcpClient client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, true);

            while (true)
            {
                string requestLine;
                try
                {
                    requestLine = reader.ReadLine();
                }
                catch (IOException)
                {
                    return; // idle keep-alive connection timed out
                }
                if (string.IsNullOrEmpty(requestLine)) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;
                var method = parts[0].ToUpperInvariant();
                var path = parts[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }

                int contentLength = 0;
                string lenHeader;
                if (headers.TryGetValue("Content-Length", out lenHeader))
                    int.TryParse(lenHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);

                var body = string.Empty;
                if (contentLength > 0)
                {
                    body = ReadBody(reader, contentLength);
                }

                Program.Log("http " + method + " " + path + " bodyLen=" + body.Length);

                if (method == "OPTIONS")
                {
                    WriteResponse(stream, 204, "No Content", "text/plain", string.Empty, false);
                    continue;
                }
                if (method == "DELETE")
                {
                    WriteResponse(stream, 200, "OK", "application/json", "{}", true);
                    continue;
                }
                if (method == "GET")
                {
                    // No server-initiated SSE stream; tell the client and close.
                    WriteResponse(stream, 405, "Method Not Allowed", "application/json",
                        "{\"error\":\"SSE stream not supported; use POST\"}", false);
                    return;
                }
                if (method != "POST")
                {
                    WriteResponse(stream, 405, "Method Not Allowed", "text/plain", string.Empty, false);
                    continue;
                }

                object response;
                lock (gate)
                {
                    response = mcp.HandleJson(body);
                }

                if (response == null)
                {
                    // Notification: 202 with no body.
                    WriteResponse(stream, 202, "Accepted", "text/plain", string.Empty, false);
                    continue;
                }

                WriteResponse(stream, 200, "OK", "application/json", Json.Stringify(response), true);
            }
        }

        /// <summary>
        /// Content-Length counts UTF-8 BYTES, but StreamReader yields chars. Reading
        /// contentLength chars over-reads multibyte payloads (Chinese paths) and stalls
        /// the request until the client gives up, so count bytes while decoding.
        /// </summary>
        private static string ReadBody(StreamReader reader, int contentLength)
        {
            var sb = new StringBuilder(contentLength + 16);
            int bytes = 0;
            while (bytes < contentLength)
            {
                int next = reader.Read();
                if (next < 0) break;
                var c = (char)next;
                sb.Append(c);
                if (char.IsHighSurrogate(c))
                {
                    int low = reader.Read();
                    if (low < 0) break;
                    sb.Append((char)low);
                    bytes += 4;
                }
                else if (c < 0x80) bytes += 1;
                else if (c < 0x800) bytes += 2;
                else bytes += 3;
            }
            return sb.ToString();
        }

        private static void WriteResponse(Stream stream, int status, string reason, string contentType,
            string body, bool withSession)
        {
            var bodyBytes = new UTF8Encoding(false).GetBytes(body ?? string.Empty);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            if (withSession) head.Append("Mcp-Session-Id: tia-local\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: keep-alive\r\n\r\n");

            var headBytes = new UTF8Encoding(false).GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (bodyBytes.Length > 0) stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }
    }
}
