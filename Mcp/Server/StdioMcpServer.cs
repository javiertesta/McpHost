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

                string response;
                try
                {
                    response = ProcessMessage(line);
                }
                catch (Exception ex)
                {
                    // ProcessMessage ya atrapa todo; esto es la última red para que el loop no se corte nunca.
                    Console.Error.WriteLine("MCP unexpected error: " + ex.Message);
                    continue;
                }

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
            Dictionary<string, object> msg;
            try
            {
                msg = _json.Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MCP error parsing message: " + ex.Message);
                McpErrorLogger.LogError("process_message_exception", null, ex.Message, ex, null, null, null, _root);
                return MakeError(null, -32700, "Parse error: " + ex.Message);
            }

            if (msg == null) return MakeError(null, -32700, "Parse error");

            string method = msg.ContainsKey("method") ? msg["method"] as string : null;
            bool hasId = msg.ContainsKey("id");
            object id = hasId ? msg["id"] : null;

            try
            {
                // Notifications (no "id" key at all) - don't respond per JSON-RPC 2.0 spec.
                // Note: {"id": null} IS a request (malformed), not a notification.
                if (!hasId)
                {
                    Console.Error.WriteLine("MCP notification: " + (method ?? "(null)"));
                    return null;
                }

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
                    case "resources/templates/list":
                        return HandleResourceTemplatesList(id);
                    // Sondeo de versión (MCP 2026-07-28) que el cliente manda antes de "initialize". Para un server
                    // 2024-11-05 la respuesta esperada es "Method not found" y el cliente sigue con el handshake de
                    // siempre; no se anota en el log porque dejaba una fila de ruido por sesión.
                    case "server/discover":
                        return MakeErrorResponse(id, -32601, "Method not found: " + method);
                    default:
                        return MakeError(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                // El JSON era válido, así que el error es interno: se responde con el mismo id (con id null el cliente
                // no puede asociar la respuesta y la request queda colgada) y con -32603, no con "Parse error".
                Console.Error.WriteLine("MCP error processing message: " + ex.Message);
                McpErrorLogger.LogError("process_message_exception", method, ex.Message, ex, null, null, null, _root);
                return hasId ? MakeError(id, -32603, "Internal error: " + ex.Message) : null;
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
                // Task.Wait y algunos drivers envuelven la excepción real en una AggregateException cuyo mensaje
                // ("Se han producido uno o varios errores.") no dice nada: se muestra la de adentro.
                Exception shown = ex is AggregateException ? ex.GetBaseException() : ex;
                Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] mcp error: " + toolName + " (" + sw.ElapsedMilliseconds + "ms): " + shown.Message);
                McpErrorLogger.LogError("tool_exception", toolName, shown.Message, ex, null, arguments, null, _root);

                string text = "Error: " + shown.Message;
                string unknownArguments = _handlers.DescribeUnknownArguments(toolName, arguments);
                if (unknownArguments != null) text += "\n\n" + unknownArguments;

                var content = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        { "type", "text" },
                        { "text", text }
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

        // No hay templates, pero el cliente los pide porque el server anuncia "resources": sin esta respuesta cada
        // sesión dejaba un "Method not found" en el log (más de mil filas de ruido).
        string HandleResourceTemplatesList(object id)
        {
            var result = new Dictionary<string, object> { { "resourceTemplates", new List<object>() } };
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

            return MakeErrorResponse(id, code, message);
        }

        // Arma la respuesta de error sin anotarla en erroresmcp.log: para los errores que son la respuesta esperada.
        string MakeErrorResponse(object id, int code, string message)
        {
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
