using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Provider adapter used after NativeS1 provisioning. Endpoint selection is
    /// driven by Verilic runtimeTransport metadata. Model names are never used as
    /// endpoint switches. OpenAI "auto" negotiates Responses first and falls back
    /// to Chat Completions only when OpenAI explicitly reports that the requested
    /// API family is unsupported.
    /// </summary>
    internal static class JarvisDirectAiTransport
    {
        private const string OpenAiAuto = "auto";
        private const string OpenAiResponses = "responses";
        private const string OpenAiChatCompletions = "chat_completions";
        private const string AnthropicMessages = "messages";
        private const string GoogleGenerateContent = "generate_content";

        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan ResponsesTimeout = TimeSpan.FromMinutes(5);

        static JarvisDirectAiTransport()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            // HttpClient defaults to a 100-second global timeout. That global
            // timeout was silently overriding the explicit per-transport CTS
            // values below (ResponsesTimeout is 5 minutes), producing false
            // provider_timeout failures at ~100 seconds. Per-request linked CTS
            // instances are the single timeout authority for this adapter.
            Http.Timeout = Timeout.InfiniteTimeSpan;
        }

        internal static async Task<AgentProxyResponse> SendAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string providerRequestJson,
            CancellationToken cancellationToken)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Provider) ||
                string.IsNullOrWhiteSpace(target.Model) || !target.HasApiKey)
                return Failure("provider_credential_unavailable", agentName, target);

            string provider = NormalizeProvider(target.Provider);
            string transport = NormalizeTransport(provider, target.RuntimeTransport);
            try
            {
                DebugLog.Log("[AI-DIRECT] dispatch agent=" + Safe(agentName) +
                    " provider=" + Safe(provider) + " model=" + Safe(target.Model) +
                    " transport=" + Safe(transport));

                if (provider == "anthropic" && transport == AnthropicMessages)
                    return await SendAnthropicAsync(agentName, target, providerRequestJson, cancellationToken)
                        .ConfigureAwait(false);

                if (provider == "google" && transport == GoogleGenerateContent)
                    return await SendGoogleAsync(agentName, target, providerRequestJson, cancellationToken)
                        .ConfigureAwait(false);

                if (provider == "openai")
                {
                    if (transport == OpenAiResponses)
                        return await SendOpenAiResponsesAsync(agentName, target, providerRequestJson, cancellationToken, false)
                            .ConfigureAwait(false);
                    if (transport == OpenAiChatCompletions)
                        return await SendOpenAiChatAsync(agentName, target, providerRequestJson, cancellationToken)
                            .ConfigureAwait(false);
                    if (transport == OpenAiAuto)
                        return await SendOpenAiResponsesAsync(agentName, target, providerRequestJson, cancellationToken, true)
                            .ConfigureAwait(false);
                }

                return Failure("provider_transport_unavailable", agentName, target);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                return Failure("provider_timeout", agentName, target);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[AI-DIRECT] transport failed agent=" + Safe(agentName) +
                    " provider=" + Safe(target.Provider) + " error=" + Safe(ex.Message));
                return Failure("provider_upstream_error", agentName, target);
            }
            finally
            {
                target.Dispose();
            }
        }

        private static async Task<AgentProxyResponse> SendAnthropicAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken)
        {
            JObject request = JObject.Parse(requestJson ?? "{}");
            request["model"] = target.Model;
            // output_config is Jarvis-neutral metadata. Do not leak it onto the
            // Anthropic wire contract where unknown fields are rejected.
            request.Remove("output_config");

            // cache_control is optional optimization metadata, not request semantics.
            // Jarvis can compose policy/system/tool fragments from several sources and
            // may legitimately exceed Anthropic's maximum number of cache breakpoints.
            // Strip the metadata at the provider boundary rather than letting an
            // otherwise valid request fail with HTTP 400. This keeps the common Jarvis
            // request provider-neutral and makes Anthropic Messages resilient to future
            // context/tool composition changes.
            RemovePropertyRecursive(request, "cache_control");

            using (var message = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
            using (var timeout = new CancellationTokenSource(DefaultTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = JsonContent(request);
                message.Headers.TryAddWithoutValidation("x-api-key", target.GetApiKey());
                message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                message.Headers.TryAddWithoutValidation("anthropic-beta", "prompt-caching-2024-07-31");
                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);
                    JObject parsed = JObject.Parse(raw);
                    return Success(agentName, target, raw,
                        FirstText(parsed["content"] as JArray),
                        ReadNestedInt(parsed, "usage", "input_tokens"),
                        ReadNestedInt(parsed, "usage", "output_tokens"));
                }
            }
        }

        private static void RemovePropertyRecursive(JToken token, string propertyName)
        {
            if (token == null || string.IsNullOrWhiteSpace(propertyName)) return;

            JObject obj = token as JObject;
            if (obj != null)
            {
                obj.Remove(propertyName);
                foreach (JProperty property in obj.Properties().ToList())
                    RemovePropertyRecursive(property.Value, propertyName);
                return;
            }

            JArray array = token as JArray;
            if (array == null) return;
            foreach (JToken item in array)
                RemovePropertyRecursive(item, propertyName);
        }

        private static async Task<AgentProxyResponse> SendOpenAiResponsesAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken,
            bool allowChatFallback)
        {
            JObject request = BuildResponsesRequest(JObject.Parse(requestJson ?? "{}"), target.Model);
            using (var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses"))
            using (var timeout = new CancellationTokenSource(ResponsesTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = JsonContent(request);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.GetApiKey());
                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (allowChatFallback && ExplicitlyRequestsChatCompletions(raw))
                        {
                            DebugLog.Log("[AI-DIRECT] OpenAI negotiated responses->chat_completions; model=" + Safe(target.Model));
                            return await SendOpenAiChatAsync(agentName, target, requestJson, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);
                    }

                    JObject parsed = JObject.Parse(raw);
                    JObject normalized = NormalizeResponses(parsed);
                    return Success(agentName, target, normalized.ToString(Formatting.None),
                        FirstText(normalized["content"] as JArray),
                        ReadNestedInt(parsed, "usage", "input_tokens"),
                        ReadNestedInt(parsed, "usage", "output_tokens"));
                }
            }
        }

        private static JObject BuildResponsesRequest(JObject source, string model)
        {
            var request = new JObject { ["model"] = model };
            string system = ReadSystemText(source["system"]);
            if (!string.IsNullOrWhiteSpace(system)) request["instructions"] = system;

            var input = new JArray();
            foreach (JObject message in (source["messages"] as JArray ?? new JArray()).OfType<JObject>())
                AppendResponsesInput(input, message);
            request["input"] = input;

            JArray tools = BuildResponsesTools(source["tools"] as JArray);
            if (tools.Count > 0)
            {
                request["tools"] = tools;
                ApplyResponsesToolChoice(request, source["tool_choice"]);
            }

            int maxTokens = ReadInt(source["max_tokens"]);
            if (maxTokens > 0) request["max_output_tokens"] = maxTokens;

            // Do not translate the neutral output_config/effort blindly. Reasoning
            // effort support differs per model (for example some Pro models only
            // accept one effort). Omitting it lets the selected model use its own
            // documented default without any model-name logic in the DLL.
            return request;
        }

        private static JArray BuildResponsesTools(JArray sourceTools)
        {
            var tools = new JArray();
            if (sourceTools == null) return tools;
            foreach (JObject tool in sourceTools.OfType<JObject>())
            {
                string name = (string)tool["name"];
                if (string.IsNullOrWhiteSpace(name)) continue;
                tools.Add(new JObject
                {
                    ["type"] = "function",
                    ["name"] = name,
                    ["description"] = (string)tool["description"] ?? string.Empty,
                    ["parameters"] = (tool["input_schema"] ?? EmptySchema()).DeepClone()
                });
            }
            return tools;
        }

        private static void AppendResponsesInput(JArray input, JObject message)
        {
            string role = ((string)message["role"] ?? "user").Trim().ToLowerInvariant();
            JToken content = message["content"];
            if (content == null) return;
            if (content.Type == JTokenType.String)
            {
                input.Add(new JObject { ["role"] = role, ["content"] = content.ToString() });
                return;
            }

            JArray blocks = content as JArray;
            if (blocks == null)
            {
                input.Add(new JObject { ["role"] = role, ["content"] = content.ToString(Formatting.None) });
                return;
            }

            var messageContent = new JArray();
            foreach (JObject block in blocks.OfType<JObject>())
            {
                string type = ((string)block["type"] ?? string.Empty).ToLowerInvariant();
                if (type == "tool_use")
                {
                    input.Add(new JObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = (string)block["id"] ?? "call_" + Guid.NewGuid().ToString("N"),
                        ["name"] = (string)block["name"] ?? string.Empty,
                        ["arguments"] = (block["input"] ?? new JObject()).ToString(Formatting.None)
                    });
                }
                else if (type == "tool_result")
                {
                    input.Add(new JObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = (string)block["tool_use_id"] ?? string.Empty,
                        ["output"] = ToolResultText(block["content"])
                    });
                }
                else if (type == "text" || type == "thinking")
                {
                    string text = (string)(block["text"] ?? block["thinking"]);
                    if (!string.IsNullOrEmpty(text))
                        messageContent.Add(new JObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = text });
                }
                else if (type == "image")
                {
                    JObject source = block["source"] as JObject;
                    if (IsBase64(source))
                        messageContent.Add(new JObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = DataUrl(source, "image/png")
                        });
                }
                else if (type == "document")
                {
                    JObject source = block["source"] as JObject;
                    if (IsBase64(source))
                        messageContent.Add(new JObject
                        {
                            ["type"] = "input_file",
                            ["filename"] = "attachment.pdf",
                            ["file_data"] = DataUrl(source, "application/pdf")
                        });
                }
            }
            if (messageContent.Count > 0)
                input.Add(new JObject { ["role"] = role, ["content"] = messageContent });
        }

        private static void ApplyResponsesToolChoice(JObject request, JToken choice)
        {
            if (choice == null) return;
            string type = choice.Type == JTokenType.String ? choice.ToString() : (string)choice["type"];
            type = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "tool")
            {
                string name = (string)choice["name"];
                if (!string.IsNullOrWhiteSpace(name))
                    request["tool_choice"] = new JObject { ["type"] = "function", ["name"] = name };
            }
            else if (type == "any" || type == "required") request["tool_choice"] = "required";
            else if (type == "none") request["tool_choice"] = "none";
            else if (type == "auto") request["tool_choice"] = "auto";
        }

        private static JObject NormalizeResponses(JObject response)
        {
            var content = new JArray();
            bool hasTool = false;
            foreach (JObject item in (response["output"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string type = ((string)item["type"] ?? string.Empty).ToLowerInvariant();
                if (type == "function_call")
                {
                    hasTool = true;
                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = (string)item["call_id"] ?? (string)item["id"] ?? "call_" + Guid.NewGuid().ToString("N"),
                        ["name"] = (string)item["name"] ?? string.Empty,
                        ["input"] = ParseArguments((string)item["arguments"])
                    });
                }
                else if (type == "message")
                {
                    foreach (JObject part in (item["content"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string partType = ((string)part["type"] ?? string.Empty).ToLowerInvariant();
                        string text = partType == "refusal" ? (string)part["refusal"] : (string)part["text"];
                        if (!string.IsNullOrEmpty(text))
                            content.Add(new JObject { ["type"] = "text", ["text"] = text });
                    }
                }
            }

            string stop = hasTool ? "tool_use" : "end_turn";
            JObject incompleteDetails = response["incomplete_details"] as JObject;
            string incomplete = incompleteDetails == null ? string.Empty : (string)incompleteDetails["reason"];
            if (!hasTool && string.Equals(incomplete, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
                stop = "max_tokens";

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stop,
                ["usage"] = new JObject
                {
                    ["input_tokens"] = ReadNestedInt(response, "usage", "input_tokens"),
                    ["output_tokens"] = ReadNestedInt(response, "usage", "output_tokens")
                }
            };
        }

        private static async Task<AgentProxyResponse> SendOpenAiChatAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken)
        {
            JObject request = BuildChatRequest(JObject.Parse(requestJson ?? "{}"), target.Model);
            using (var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions"))
            using (var timeout = new CancellationTokenSource(DefaultTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = JsonContent(request);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.GetApiKey());
                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);
                    JObject parsed = JObject.Parse(raw);
                    JObject normalized = NormalizeChat(parsed);
                    return Success(agentName, target, normalized.ToString(Formatting.None),
                        FirstText(normalized["content"] as JArray),
                        ReadNestedInt(parsed, "usage", "prompt_tokens"),
                        ReadNestedInt(parsed, "usage", "completion_tokens"));
                }
            }
        }

        private static JObject BuildChatRequest(JObject source, string model)
        {
            var messages = new JArray();
            string system = ReadSystemText(source["system"]);
            if (!string.IsNullOrWhiteSpace(system)) messages.Add(new JObject { ["role"] = "system", ["content"] = system });
            foreach (JObject message in (source["messages"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string role = ((string)message["role"] ?? "user").ToLowerInvariant();
                if (role == "assistant") messages.Add(ConvertChatAssistant(message["content"]));
                else foreach (JObject converted in ConvertChatUser(message["content"])) messages.Add(converted);
            }
            var result = new JObject { ["model"] = model, ["messages"] = messages };
            int max = ReadInt(source["max_tokens"]);
            if (max > 0) result["max_completion_tokens"] = max;
            JArray sourceTools = source["tools"] as JArray;
            if (sourceTools != null && sourceTools.Count > 0)
            {
                var tools = new JArray();
                foreach (JObject tool in sourceTools.OfType<JObject>())
                {
                    string name = (string)tool["name"];
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    tools.Add(new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = name,
                            ["description"] = (string)tool["description"] ?? string.Empty,
                            ["parameters"] = (tool["input_schema"] ?? EmptySchema()).DeepClone()
                        }
                    });
                }
                result["tools"] = tools;
            }
            return result;
        }

        private static JObject ConvertChatAssistant(JToken content)
        {
            if (content == null) return new JObject { ["role"] = "assistant", ["content"] = JValue.CreateNull() };
            if (content.Type == JTokenType.String) return new JObject { ["role"] = "assistant", ["content"] = content.ToString() };
            var text = new StringBuilder();
            var calls = new JArray();
            foreach (JObject block in (content as JArray ?? new JArray()).OfType<JObject>())
            {
                string type = (string)block["type"];
                if (type == "text" || type == "thinking")
                {
                    string value = (string)(block["text"] ?? block["thinking"]);
                    if (!string.IsNullOrEmpty(value)) { if (text.Length > 0) text.Append('\n'); text.Append(value); }
                }
                else if (type == "tool_use")
                {
                    calls.Add(new JObject
                    {
                        ["id"] = (string)block["id"] ?? "call_" + Guid.NewGuid().ToString("N"),
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = (string)block["name"] ?? string.Empty,
                            ["arguments"] = (block["input"] ?? new JObject()).ToString(Formatting.None)
                        }
                    });
                }
            }
            var result = new JObject { ["role"] = "assistant", ["content"] = text.Length == 0 ? JValue.CreateNull() : new JValue(text.ToString()) };
            if (calls.Count > 0) result["tool_calls"] = calls;
            return result;
        }

        private static IEnumerable<JObject> ConvertChatUser(JToken content)
        {
            if (content == null) yield break;
            if (content.Type == JTokenType.String)
            {
                yield return new JObject { ["role"] = "user", ["content"] = content.ToString() };
                yield break;
            }
            var rich = new JArray();
            foreach (JObject block in (content as JArray ?? new JArray()).OfType<JObject>())
            {
                string type = (string)block["type"];
                if (type == "tool_result")
                    yield return new JObject { ["role"] = "tool", ["tool_call_id"] = (string)block["tool_use_id"] ?? string.Empty, ["content"] = ToolResultText(block["content"]) };
                else if (type == "text")
                    rich.Add(new JObject { ["type"] = "text", ["text"] = (string)block["text"] ?? string.Empty });
                else if (type == "image")
                {
                    JObject source = block["source"] as JObject;
                    if (IsBase64(source)) rich.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = DataUrl(source, "image/png") } });
                }
            }
            if (rich.Count > 0) yield return new JObject { ["role"] = "user", ["content"] = rich };
        }

        private static JObject NormalizeChat(JObject response)
        {
            JObject choice = (response["choices"] as JArray)?.OfType<JObject>().FirstOrDefault();
            JObject message = choice?["message"] as JObject;
            var content = new JArray();
            string text = message?["content"] == null || message["content"].Type == JTokenType.Null ? null : message["content"].ToString();
            if (!string.IsNullOrEmpty(text)) content.Add(new JObject { ["type"] = "text", ["text"] = text });
            JArray calls = message?["tool_calls"] as JArray;
            if (calls != null)
            {
                foreach (JObject call in calls.OfType<JObject>())
                {
                    JObject fn = call["function"] as JObject;
                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = (string)call["id"] ?? "call_" + Guid.NewGuid().ToString("N"),
                        ["name"] = (string)fn?["name"] ?? string.Empty,
                        ["input"] = ParseArguments((string)fn?["arguments"])
                    });
                }
            }
            string finish = (string)choice?["finish_reason"] ?? string.Empty;
            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = calls != null && calls.Count > 0 ? "tool_use" : (finish == "length" ? "max_tokens" : "end_turn"),
                ["usage"] = new JObject
                {
                    ["input_tokens"] = ReadNestedInt(response, "usage", "prompt_tokens"),
                    ["output_tokens"] = ReadNestedInt(response, "usage", "completion_tokens")
                }
            };
        }

        private static async Task<AgentProxyResponse> SendGoogleAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken)
        {
            JObject request = BuildGoogleRequest(JObject.Parse(requestJson ?? "{}"));
            string endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(target.Model) + ":generateContent";
            using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
            using (var timeout = new CancellationTokenSource(DefaultTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = JsonContent(request);
                message.Headers.TryAddWithoutValidation("x-goog-api-key", target.GetApiKey());
                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);
                    JObject parsed = JObject.Parse(raw);
                    JObject normalized = NormalizeGoogle(parsed);
                    return Success(agentName, target, normalized.ToString(Formatting.None),
                        FirstText(normalized["content"] as JArray),
                        ReadNestedInt(parsed, "usageMetadata", "promptTokenCount"),
                        ReadNestedInt(parsed, "usageMetadata", "candidatesTokenCount"));
                }
            }
        }

        private static JObject BuildGoogleRequest(JObject source)
        {
            var result = new JObject();
            string system = ReadSystemText(source["system"]);
            if (!string.IsNullOrWhiteSpace(system)) result["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = system }) };
            var contents = new JArray();
            foreach (JObject message in (source["messages"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string role = ((string)message["role"] ?? "user").ToLowerInvariant();
                var parts = new JArray();
                AppendGoogleParts(parts, message["content"]);
                if (parts.Count > 0) contents.Add(new JObject { ["role"] = role == "assistant" ? "model" : "user", ["parts"] = parts });
            }
            result["contents"] = contents;
            JArray sourceTools = source["tools"] as JArray;
            if (sourceTools != null && sourceTools.Count > 0)
            {
                var declarations = new JArray();
                foreach (JObject tool in sourceTools.OfType<JObject>())
                {
                    string name = (string)tool["name"];
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    declarations.Add(new JObject { ["name"] = name, ["description"] = (string)tool["description"] ?? string.Empty, ["parameters"] = NormalizeGoogleSchema(tool["input_schema"] ?? EmptySchema()) });
                }
                result["tools"] = new JArray(new JObject { ["functionDeclarations"] = declarations });
            }
            int max = ReadInt(source["max_tokens"]);
            if (max > 0) result["generationConfig"] = new JObject { ["maxOutputTokens"] = max };
            return result;
        }

        private static void AppendGoogleParts(JArray parts, JToken content)
        {
            if (content == null) return;
            if (content.Type == JTokenType.String) { parts.Add(new JObject { ["text"] = content.ToString() }); return; }
            foreach (JObject block in (content as JArray ?? new JArray()).OfType<JObject>())
            {
                string type = ((string)block["type"] ?? string.Empty).ToLowerInvariant();
                if (type == "text" || type == "thinking")
                {
                    string text = (string)(block["text"] ?? block["thinking"]);
                    if (!string.IsNullOrEmpty(text)) parts.Add(new JObject { ["text"] = text });
                }
                else if (type == "tool_use")
                    parts.Add(new JObject { ["functionCall"] = new JObject { ["name"] = (string)block["name"] ?? string.Empty, ["args"] = (block["input"] ?? new JObject()).DeepClone() } });
                else if (type == "tool_result")
                    parts.Add(new JObject { ["functionResponse"] = new JObject { ["name"] = "tool", ["response"] = new JObject { ["result"] = ToolResultText(block["content"]) } } });
                else if (type == "image" || type == "document")
                {
                    JObject source = block["source"] as JObject;
                    if (IsBase64(source)) parts.Add(new JObject { ["inlineData"] = new JObject { ["mimeType"] = (string)source["media_type"] ?? (type == "document" ? "application/pdf" : "image/png"), ["data"] = (string)source["data"] ?? string.Empty } });
                }
            }
        }

        private static JToken NormalizeGoogleSchema(JToken schema)
        {
            JObject source = schema as JObject;
            if (source == null) return EmptySchema();
            var result = new JObject();
            string[] allowed = { "type", "description", "format", "nullable", "minimum", "maximum", "minItems", "maxItems", "minLength", "maxLength" };
            foreach (string key in allowed) if (source[key] != null) result[key] = source[key].DeepClone();
            if (source["enum"] != null) result["enum"] = source["enum"].DeepClone();
            JObject props = source["properties"] as JObject;
            if (props != null)
            {
                var normalized = new JObject();
                foreach (JProperty p in props.Properties()) normalized[p.Name] = NormalizeGoogleSchema(p.Value);
                result["properties"] = normalized;
            }
            if (source["required"] != null) result["required"] = source["required"].DeepClone();
            if (source["items"] != null) result["items"] = NormalizeGoogleSchema(source["items"]);
            if (result["type"] == null) result["type"] = result["items"] != null ? "array" : "object";
            return result;
        }

        private static JObject NormalizeGoogle(JObject response)
        {
            var content = new JArray();
            bool hasTool = false;
            JObject candidate = (response["candidates"] as JArray)?.OfType<JObject>().FirstOrDefault();
            JObject candidateContent = candidate?["content"] as JObject;
            JArray parts = candidateContent?["parts"] as JArray;
            if (parts != null)
            {
                foreach (JObject part in parts.OfType<JObject>())
                {
                    string text = (string)part["text"];
                    if (!string.IsNullOrEmpty(text)) content.Add(new JObject { ["type"] = "text", ["text"] = text });
                    JObject call = part["functionCall"] as JObject;
                    if (call != null)
                    {
                        hasTool = true;
                        content.Add(new JObject { ["type"] = "tool_use", ["id"] = "call_" + Guid.NewGuid().ToString("N"), ["name"] = (string)call["name"] ?? string.Empty, ["input"] = (call["args"] as JObject ?? new JObject()).DeepClone() });
                    }
                }
            }
            return new JObject { ["content"] = content, ["stop_reason"] = hasTool ? "tool_use" : "end_turn", ["usage"] = new JObject { ["input_tokens"] = ReadNestedInt(response, "usageMetadata", "promptTokenCount"), ["output_tokens"] = ReadNestedInt(response, "usageMetadata", "candidatesTokenCount") } };
        }

        private static StringContent JsonContent(JObject value)
        {
            return new StringContent(value.ToString(Formatting.None), Encoding.UTF8, "application/json");
        }

        private static JObject EmptySchema()
        {
            return new JObject { ["type"] = "object", ["properties"] = new JObject() };
        }

        private static JObject ParseArguments(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new JObject();
            try { return JObject.Parse(raw); }
            catch { return new JObject { ["value"] = raw }; }
        }

        private static bool IsBase64(JObject source)
        {
            return source != null && string.Equals((string)source["type"], "base64", StringComparison.OrdinalIgnoreCase);
        }

        private static string DataUrl(JObject source, string fallbackMime)
        {
            return "data:" + ((string)source["media_type"] ?? fallbackMime) + ";base64," + ((string)source["data"] ?? string.Empty);
        }

        private static string ToolResultText(JToken content)
        {
            if (content == null) return string.Empty;
            if (content.Type == JTokenType.String) return content.ToString();
            var text = new StringBuilder();
            foreach (JObject block in (content as JArray ?? new JArray()).OfType<JObject>())
            {
                string value = (string)block["text"];
                if (string.IsNullOrEmpty(value)) continue;
                if (text.Length > 0) text.Append('\n');
                text.Append(value);
            }
            return text.Length > 0 ? text.ToString() : content.ToString(Formatting.None);
        }

        private static string ReadSystemText(JToken system)
        {
            if (system == null) return string.Empty;
            if (system.Type == JTokenType.String) return system.ToString();
            var text = new StringBuilder();
            foreach (JObject block in (system as JArray ?? new JArray()).OfType<JObject>())
            {
                string value = (string)block["text"];
                if (string.IsNullOrEmpty(value)) continue;
                if (text.Length > 0) text.Append('\n');
                text.Append(value);
            }
            return text.ToString();
        }

        private static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "gemini" || value == "googleai" || value == "google-ai") return "google";
            if (value == "claude") return "anthropic";
            return value;
        }

        private static string NormalizeTransport(string provider, string transport)
        {
            string value = (transport ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (string.IsNullOrWhiteSpace(value))
            {
                if (provider == "openai") return OpenAiAuto;
                if (provider == "anthropic") return AnthropicMessages;
                if (provider == "google") return GoogleGenerateContent;
                return string.Empty;
            }
            if (value == "response" || value == "response_api" || value == "responses_api") return OpenAiResponses;
            if (value == "chat" || value == "chat_completion" || value == "chat_completions_api") return OpenAiChatCompletions;
            if (value == "automatic" || value == "negotiate" || value == "negotiated") return OpenAiAuto;
            if (value == "anthropic_messages" || value == "messages_api") return AnthropicMessages;
            if (value == "generatecontent" || value == "generate_content_api" || value == "gemini_generate_content") return GoogleGenerateContent;
            return value;
        }

        private static bool ExplicitlyRequestsChatCompletions(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string value = raw.ToLowerInvariant();
            return value.Contains("chat/completions") &&
                (value.Contains("only supported") || value.Contains("not supported") || value.Contains("use"));
        }

        private static AgentProxyResponse ProviderHttpFailure(HttpStatusCode status, string raw, string agentName, JarvisAgentRuntimeTarget target)
        {
            int code = (int)status;
            string reason = code == 401 || code == 403 ? "provider_auth_failed" :
                code == 429 ? (IsCreditsError(raw) ? "provider_credits_exhausted" : "provider_rate_limited") :
                code == 400 || code == 404 || code == 422 ? "provider_model_or_request_invalid" : "provider_upstream_error";
            string detail = ExtractProviderErrorDetail(raw);
            DebugLog.Log("[AI-DIRECT] provider error agent=" + Safe(agentName) + " provider=" + Safe(target == null ? null : target.Provider) + " model=" + Safe(target == null ? null : target.Model) + " transport=" + Safe(target == null ? null : target.RuntimeTransport) + " http=" + code.ToString() + " reason=" + reason + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " detail=" + Safe(detail)));
            return Failure(reason, agentName, target);
        }

        private static string ExtractProviderErrorDetail(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            try
            {
                JObject root = JObject.Parse(raw);
                JObject error = root["error"] as JObject;
                if (error != null) return (string)error["message"] ?? (string)error["status"] ?? string.Empty;
                return (string)root["message"] ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool IsCreditsError(string raw)
        {
            string value = (raw ?? string.Empty).ToLowerInvariant();
            return value.Contains("credit") && (value.Contains("balance") || value.Contains("quota") || value.Contains("billing"));
        }

        private static AgentProxyResponse Success(string agentName, JarvisAgentRuntimeTarget target, string raw, string text, int input, int output)
        {
            return new AgentProxyResponse
            {
                Success = true,
                ResponseText = text,
                RawResponseJson = raw ?? string.Empty,
                UsageInputTokens = input,
                UsageOutputTokens = output,
                RuntimeAgent = agentName,
                RuntimeProvider = target.Provider,
                RuntimeModel = target.Model,
                RuntimeRouting = target.Inherited ? "Inherited" : "Dedicated"
            };
        }

        private static AgentProxyResponse Failure(string reason, string agentName, JarvisAgentRuntimeTarget target)
        {
            return new AgentProxyResponse
            {
                Success = false,
                CreditsExhausted = reason == "provider_credits_exhausted",
                ErrorMessage = SafeError(reason),
                RawResponseJson = string.Empty,
                RuntimeAgent = agentName,
                RuntimeProvider = target == null ? null : target.Provider,
                RuntimeModel = target == null ? null : target.Model,
                RuntimeRouting = target == null ? null : (target.Inherited ? "Inherited" : "Dedicated")
            };
        }

        private static string SafeError(string reason)
        {
            switch (reason)
            {
                case "provider_auth_failed": return "Ο AI provider απέρριψε τα διαπιστευτήρια.";
                case "provider_model_or_request_invalid": return "Το επιλεγμένο AI model ή το αίτημα δεν είναι έγκυρο.";
                case "provider_credits_exhausted": return "Το AI account έχει εξαντλήσει τα credits του.";
                case "provider_rate_limited": return "Ο AI provider έχει προσωρινό όριο κλήσεων. Δοκίμασε ξανά σε λίγο.";
                case "provider_timeout": return "Ο AI provider δεν απάντησε εγκαίρως.";
                case "provider_transport_unavailable": return "Το Verilic δεν έχει συμβατό transport για τον επιλεγμένο provider/model.";
                case "provider_credential_unavailable": return "Το session credential του AI agent δεν είναι διαθέσιμο. Εκτέλεσε HEALTH ή άνοιξε ξανά τον Jarvis.";
                default: return "Η απευθείας κλήση προς τον AI provider απέτυχε (" + reason + ").";
            }
        }

        private static string FirstText(JArray content)
        {
            if (content == null) return null;
            JObject block = content.OfType<JObject>().FirstOrDefault(x => string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase));
            return block == null ? null : (string)block["text"];
        }

        private static int ReadNestedInt(JObject parent, string objectName, string valueName)
        {
            JObject nested = parent == null ? null : parent[objectName] as JObject;
            return nested == null ? 0 : ReadInt(nested[valueName]);
        }

        private static int ReadInt(JToken token)
        {
            int value;
            return token != null && int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return safe.Length > 180 ? safe.Substring(0, 180) : safe;
        }
    }
}
