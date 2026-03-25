using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;
using McpHost.Core;

namespace McpHost.Server
{
    class StdioMcpServer
    {
        readonly McpToolHandlers _handlers;
        readonly string _root;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public StdioMcpServer(McpToolHandlers handlers, string root)
        {
            _handlers = handlers;
            _root = root;
        }

        public void Run()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = Encoding.UTF8;

            // Logs go to stderr only
            Console.Error.WriteLine("MCP server started. Waiting for JSON-RPC on stdin...");

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                string response = ProcessMessage(line);
                if (response != null)
                {
                    Console.WriteLine(response);
                    Console.Out.Flush();
                }
            }

            Console.Error.WriteLine("MCP server: stdin closed, shutting down.");
        }

        string ProcessMessage(string json)
        {
            try
            {
                var msg = _json.Deserialize<Dictionary<string, object>>(json);
                if (msg == null) return MakeError(null, -32700, "Parse error");

                string method = msg.ContainsKey("method") ? msg["method"] as string : null;
                bool hasId = msg.ContainsKey("id");

                // Notifications (no "id" key at all) - don't respond per JSON-RPC 2.0 spec.
                // Note: {"id": null} IS a request (malformed), not a notification.
                if (!hasId)
                {
                    Console.Error.WriteLine("MCP notification: " + (method ?? "(null)"));
                    return null;
                }

                object id = msg["id"];

                if (string.IsNullOrEmpty(method))
                    return MakeError(id, -32600, "Invalid Request: missing method");

                switch (method)
                {
                    case "initialize":
                        return HandleInitialize(id);
                    case "ping":
                        return HandlePing(id);
                    case "tools/list":
                        return HandleToolsList(id);
                    case "tools/call":
                        return HandleToolsCall(id, msg);
                    case "resources/list":
                        return HandleResourcesList(id);
                    case "resources/read":
                        return HandleResourcesRead(id, msg);
                    default:
                        return MakeError(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MCP error processing message: " + ex.Message);
                McpErrorLogger.LogError("process_message_exception", null, ex.Message, ex, null, null, null, _root);
                return MakeError(null, -32700, "Parse error: " + ex.Message);
            }
        }

        string HandleInitialize(object id)
        {
            var result = new Dictionary<string, object>
            {
                { "protocolVersion", "2024-11-05" },
                { "capabilities", new Dictionary<string, object>
                    {
                        { "tools", new Dictionary<string, object> { { "listChanged", false } } },
                        { "resources", new Dictionary<string, object> { { "listChanged", false } } }
                    }
                },
                { "serverInfo", new Dictionary<string, object>
                    {
                        { "name", "mcp-host" },
                        { "version", "1.0.0" }
                    }
                }
            };

            return MakeResult(id, result);
        }

        string HandlePing(object id)
        {
            return MakeResult(id, new Dictionary<string, object>());
        }

        string HandleToolsList(object id)
        {
            var tools = _handlers.GetToolDefinitions();
            var result = new Dictionary<string, object> { { "tools", tools } };
            return MakeResult(id, result);
        }

        string HandleToolsCall(object id, Dictionary<string, object> msg)
        {
            var parms = msg.ContainsKey("params") ? msg["params"] as Dictionary<string, object> : null;
            if (parms == null)
                return MakeError(id, -32602, "Invalid params");

            string toolName = parms.ContainsKey("name") ? parms["name"] as string : null;
            if (string.IsNullOrEmpty(toolName))
                return MakeError(id, -32602, "Missing tool name");

            var arguments = parms.ContainsKey("arguments")
                ? parms["arguments"] as Dictionary<string, object>
                : new Dictionary<string, object>();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] mcp start: " + toolName);
            try
            {
                var toolResult = _handlers.CallTool(toolName, arguments);
                Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] mcp done: " + toolName + " (" + sw.ElapsedMilliseconds + "ms)");
                var result = new Dictionary<string, object> { { "content", toolResult.Content } };
                if (toolResult.IsError) result["isError"] = true;
                if (toolResult.IsError && toolResult.ErrorData != null && toolResult.ErrorData.Count > 0)
                    result["error"] = toolResult.ErrorData;

                if (toolResult.IsError)
                {
                    string errorText = ExtractFirstTextContent(toolResult.Content) ?? "Tool returned isError=true";
                    McpErrorLogger.LogError(
                        "tool_result_error",
                        toolName,
                        errorText,
                        null,
                        toolResult.ErrorData,
                        arguments,
                        null,
                        _root);
                }
                return MakeResult(id, result);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] mcp error: " + toolName + " (" + sw.ElapsedMilliseconds + "ms): " + ex.Message);
                McpErrorLogger.LogError("tool_exception", toolName, ex.Message, ex, null, arguments, null, _root);

                var content = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        { "type", "text" },
                        { "text", "Error: " + ex.Message }
                    }
                };

                var result = new Dictionary<string, object>
                {
                    { "content", content },
                    { "isError", true }
                };

                return MakeResult(id, result);
            }
        }

        string HandleResourcesList(object id)
        {
            var resources = _handlers.GetResourceDefinitions();
            var result = new Dictionary<string, object> { { "resources", resources } };
            return MakeResult(id, result);
        }

        string HandleResourcesRead(object id, Dictionary<string, object> msg)
        {
            var parms = msg.ContainsKey("params") ? msg["params"] as Dictionary<string, object> : null;
            if (parms == null)
                return MakeError(id, -32602, "Invalid params");

            string uri = parms.ContainsKey("uri") ? parms["uri"] as string : null;
            if (string.IsNullOrEmpty(uri))
                return MakeError(id, -32602, "Missing resource uri");

            var content = _handlers.ReadResource(uri);
            var result = new Dictionary<string, object> { { "contents", content } };
            return MakeResult(id, result);
        }

        string MakeResult(object id, object result)
        {
            var resp = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            };
            return _json.Serialize(resp);
        }

        string MakeError(object id, int code, string message)
        {
            McpErrorLogger.LogError(
                "jsonrpc_error",
                null,
                message,
                null,
                new Dictionary<string, object> { { "jsonrpc_code", code } },
                null,
                null,
                _root);
                
            var resp = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message }
                    }
                }
            };

            return _json.Serialize(resp);
        }

        static string ExtractFirstTextContent(List<object> content)
        {
            if (content == null) return null;
            foreach (object item in content)
            {
                var obj = item as Dictionary<string, object>;
                if (obj == null) continue;

                object typeObj;
                object textObj;
                if (!obj.TryGetValue("type", out typeObj)) continue;
                if (!obj.TryGetValue("text", out textObj)) continue;

                if ((typeObj as string) == "text") return textObj as string;
            }
            return null;
        }
    }
}
