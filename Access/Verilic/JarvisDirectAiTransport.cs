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
    /// Runtime AI transport after Jarvis boot provisioning.
    ///
    /// IMPORTANT: this class never contacts Verilic. Provider, model and API key
    /// come exclusively from JarvisAgentRuntimeSnapshot, which is populated at
    /// boot (or explicitly refreshed by HEALTH). The provider response is
    /// normalized to the Anthropic-style content/tool contract already consumed
    /// by the mature Jarvis loop and the orchestration clients.
    /// </summary>
    internal static class JarvisDirectAiTransport
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

        static JarvisDirectAiTransport()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
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
            try
            {
                if (provider == "anthropic")
                    return await SendAnthropicAsync(agentName, target, providerRequestJson, cancellationToken)
                        .ConfigureAwait(false);
                if (provider == "google")
                    return await SendGoogleAsync(agentName, target, providerRequestJson, cancellationToken)
                        .ConfigureAwait(false);
                if (provider == "openai")
                    return await SendOpenAiAsync(agentName, target, providerRequestJson, cancellationToken)
                        .ConfigureAwait(false);

                return Failure("provider_chat_adapter_unavailable", agentName, target);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
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

            string apiKey = target.GetApiKey();
            using (var message = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.anthropic.com/v1/messages"))
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token))
            {
                message.Content = new StringContent(
                    request.ToString(Formatting.None), Encoding.UTF8, "application/json");
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                message.Headers.TryAddWithoutValidation("anthropic-beta", "prompt-caching-2024-07-31");

                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token)
                    .ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);

                    JObject parsed = JObject.Parse(raw);
                    int input = ReadInt(parsed["usage"]?["input_tokens"]);
                    int output = ReadInt(parsed["usage"]?["output_tokens"]);
                    string text = FirstAnthropicText(parsed["content"] as JArray);
                    return Success(agentName, target, raw, text, input, output);
                }
            }
        }

        private static async Task<AgentProxyResponse> SendGoogleAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken)
        {
            JObject anthropic = JObject.Parse(requestJson ?? "{}");
            JObject google = BuildGoogleRequest(anthropic);
            string model = Uri.EscapeDataString(target.Model);
            string endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" +
                model + ":generateContent";
            string apiKey = target.GetApiKey();

            using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token))
            {
                message.Content = new StringContent(
                    google.ToString(Formatting.None), Encoding.UTF8, "application/json");
                message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token)
                    .ConfigureAwait(false))
                {
                    string rawGoogle = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, rawGoogle, agentName, target);

                    JObject parsed = JObject.Parse(rawGoogle);
                    JObject normalized = NormalizeGoogleResponse(parsed);
                    int input = ReadInt(parsed["usageMetadata"]?["promptTokenCount"]);
                    int output = ReadInt(parsed["usageMetadata"]?["candidatesTokenCount"]);
                    string text = FirstAnthropicText(normalized["content"] as JArray);
                    return Success(
                        agentName,
                        target,
                        normalized.ToString(Formatting.None),
                        text,
                        input,
                        output);
                }
            }
        }

        private static async Task<AgentProxyResponse> SendOpenAiAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken)
        {
            JObject anthropic = JObject.Parse(requestJson ?? "{}");
            JObject openAi = BuildOpenAiRequest(anthropic, target.Model);
            string apiKey = target.GetApiKey();

            using (var message = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.openai.com/v1/chat/completions"))
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token))
            {
                message.Content = new StringContent(
                    openAi.ToString(Formatting.None), Encoding.UTF8, "application/json");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token)
                    .ConfigureAwait(false))
                {
                    string rawOpenAi = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, rawOpenAi, agentName, target);

                    JObject parsed = JObject.Parse(rawOpenAi);
                    JObject normalized = NormalizeOpenAiResponse(parsed);
                    int input = ReadInt(parsed["usage"]?["prompt_tokens"]);
                    int output = ReadInt(parsed["usage"]?["completion_tokens"]);
                    string text = FirstAnthropicText(normalized["content"] as JArray);
                    return Success(
                        agentName,
                        target,
                        normalized.ToString(Formatting.None),
                        text,
                        input,
                        output);
                }
            }
        }

        private static JObject BuildGoogleRequest(JObject source)
        {
            var result = new JObject();
            string system = ReadSystemText(source["system"]);
            if (!string.IsNullOrWhiteSpace(system))
            {
                result["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray(new JObject { ["text"] = system })
                };
            }

            var contents = new JArray();
            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            JArray messages = source["messages"] as JArray ?? new JArray();
            foreach (JObject message in messages.OfType<JObject>())
            {
                string role = ((string)message["role"] ?? "user").Trim();
                JToken content = message["content"];

                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = new JArray();
                    AppendGoogleAssistantParts(parts, content, toolNames);
                    if (parts.Count > 0)
                        contents.Add(new JObject { ["role"] = "model", ["parts"] = parts });
                    continue;
                }

                AppendGoogleUserContent(contents, content, toolNames);
            }
            result["contents"] = contents;

            JArray tools = source["tools"] as JArray;
            if (tools != null && tools.Count > 0)
            {
                var declarations = new JArray();
                foreach (JObject tool in tools.OfType<JObject>())
                {
                    string name = (string)tool["name"];
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    declarations.Add(new JObject
                    {
                        ["name"] = name,
                        ["description"] = (string)tool["description"] ?? string.Empty,
                        ["parameters"] = (tool["input_schema"] ?? new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject()
                        }).DeepClone()
                    });
                }
                if (declarations.Count > 0)
                {
                    result["tools"] = new JArray(new JObject
                    {
                        ["functionDeclarations"] = declarations
                    });
                }
            }

            int maxTokens = ReadInt(source["max_tokens"]);
            var generation = new JObject();
            if (maxTokens > 0)
                generation["maxOutputTokens"] = maxTokens;
            if (source["temperature"] != null)
                generation["temperature"] = source["temperature"].DeepClone();
            if (generation.Count > 0)
                result["generationConfig"] = generation;

            return result;
        }

        private static void AppendGoogleAssistantParts(
            JArray parts,
            JToken content,
            IDictionary<string, string> toolNames)
        {
            if (content == null)
                return;
            if (content.Type == JTokenType.String)
            {
                parts.Add(new JObject { ["text"] = content.ToString() });
                return;
            }

            JArray blocks = content as JArray;
            if (blocks == null)
            {
                parts.Add(new JObject { ["text"] = content.ToString(Formatting.None) });
                return;
            }

            foreach (JObject block in blocks.OfType<JObject>())
            {
                string type = (string)block["type"];
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
                {
                    string text = (string)(block["text"] ?? block["thinking"]);
                    if (!string.IsNullOrEmpty(text))
                        parts.Add(new JObject { ["text"] = text });
                    continue;
                }

                if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    string id = (string)block["id"];
                    string name = (string)block["name"];
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        toolNames[id] = name;
                    parts.Add(new JObject
                    {
                        ["functionCall"] = new JObject
                        {
                            ["name"] = name ?? string.Empty,
                            ["args"] = (block["input"] as JObject ?? new JObject()).DeepClone()
                        }
                    });
                }
            }
        }

        private static void AppendGoogleUserContent(
            JArray contents,
            JToken content,
            IDictionary<string, string> toolNames)
        {
            if (content == null)
                return;
            if (content.Type == JTokenType.String)
            {
                contents.Add(new JObject
                {
                    ["role"] = "user",
                    ["parts"] = new JArray(new JObject { ["text"] = content.ToString() })
                });
                return;
            }

            JArray blocks = content as JArray;
            if (blocks == null)
            {
                contents.Add(new JObject
                {
                    ["role"] = "user",
                    ["parts"] = new JArray(new JObject { ["text"] = content.ToString(Formatting.None) })
                });
                return;
            }

            var parts = new JArray();
            foreach (JObject block in blocks.OfType<JObject>())
            {
                string type = (string)block["type"];
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(new JObject { ["text"] = (string)block["text"] ?? string.Empty });
                    continue;
                }

                if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    string id = (string)block["tool_use_id"];
                    string name;
                    if (string.IsNullOrWhiteSpace(id) || !toolNames.TryGetValue(id, out name))
                        name = "tool_result";
                    parts.Add(new JObject
                    {
                        ["functionResponse"] = new JObject
                        {
                            ["name"] = name,
                            ["response"] = new JObject
                            {
                                ["result"] = block["content"] == null
                                    ? string.Empty
                                    : block["content"].ToString()
                            }
                        }
                    });
                    continue;
                }

                if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
                {
                    JObject source = block["source"] as JObject;
                    if (source != null && string.Equals((string)source["type"], "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(new JObject
                        {
                            ["inlineData"] = new JObject
                            {
                                ["mimeType"] = (string)source["media_type"] ?? "image/png",
                                ["data"] = (string)source["data"] ?? string.Empty
                            }
                        });
                    }
                    continue;
                }

                if (string.Equals(type, "document", StringComparison.OrdinalIgnoreCase))
                {
                    JObject source = block["source"] as JObject;
                    if (source != null && string.Equals((string)source["type"], "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(new JObject
                        {
                            ["inlineData"] = new JObject
                            {
                                ["mimeType"] = (string)source["media_type"] ?? "application/pdf",
                                ["data"] = (string)source["data"] ?? string.Empty
                            }
                        });
                    }
                }
            }

            if (parts.Count > 0)
                contents.Add(new JObject { ["role"] = "user", ["parts"] = parts });
        }

        private static JObject NormalizeGoogleResponse(JObject response)
        {
            var content = new JArray();
            JObject candidate = (response["candidates"] as JArray)?.OfType<JObject>().FirstOrDefault();
            JArray parts = candidate?["content"]?["parts"] as JArray;
            bool hasToolUse = false;

            if (parts != null)
            {
                foreach (JObject part in parts.OfType<JObject>())
                {
                    if (part["text"] != null)
                    {
                        content.Add(new JObject
                        {
                            ["type"] = "text",
                            ["text"] = part["text"].ToString()
                        });
                    }

                    JObject call = part["functionCall"] as JObject;
                    if (call != null)
                    {
                        hasToolUse = true;
                        content.Add(new JObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = "gemini_" + Guid.NewGuid().ToString("N"),
                            ["name"] = (string)call["name"] ?? string.Empty,
                            ["input"] = (call["args"] as JObject ?? new JObject()).DeepClone()
                        });
                    }
                }
            }

            string finish = (string)candidate?["finishReason"] ?? string.Empty;
            string stopReason = hasToolUse
                ? "tool_use"
                : string.Equals(finish, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)
                    ? "max_tokens"
                    : string.Equals(finish, "SAFETY", StringComparison.OrdinalIgnoreCase)
                        ? "refusal"
                        : "end_turn";

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stopReason,
                ["usage"] = new JObject
                {
                    ["input_tokens"] = ReadInt(response["usageMetadata"]?["promptTokenCount"]),
                    ["output_tokens"] = ReadInt(response["usageMetadata"]?["candidatesTokenCount"])
                }
            };
        }

        private static JObject BuildOpenAiRequest(JObject source, string model)
        {
            var messages = new JArray();
            string system = ReadSystemText(source["system"]);
            if (!string.IsNullOrWhiteSpace(system))
                messages.Add(new JObject { ["role"] = "system", ["content"] = system });

            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            JArray sourceMessages = source["messages"] as JArray ?? new JArray();
            foreach (JObject message in sourceMessages.OfType<JObject>())
            {
                string role = ((string)message["role"] ?? "user").Trim();
                JToken content = message["content"];
                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    JObject assistant = ConvertOpenAiAssistantMessage(content, toolNames);
                    if (assistant != null)
                        messages.Add(assistant);
                }
                else
                {
                    foreach (JObject converted in ConvertOpenAiUserMessages(content, toolNames))
                        messages.Add(converted);
                }
            }

            var result = new JObject
            {
                ["model"] = model,
                ["messages"] = messages
            };

            int maxTokens = ReadInt(source["max_tokens"]);
            if (maxTokens > 0)
                result["max_completion_tokens"] = maxTokens;
            if (source["temperature"] != null)
                result["temperature"] = source["temperature"].DeepClone();

            JArray sourceTools = source["tools"] as JArray;
            if (sourceTools != null && sourceTools.Count > 0)
            {
                var tools = new JArray();
                foreach (JObject tool in sourceTools.OfType<JObject>())
                {
                    string name = (string)tool["name"];
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    tools.Add(new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = name,
                            ["description"] = (string)tool["description"] ?? string.Empty,
                            ["parameters"] = (tool["input_schema"] ?? new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject()
                            }).DeepClone()
                        }
                    });
                }
                if (tools.Count > 0)
                    result["tools"] = tools;
            }

            return result;
        }

        private static JObject ConvertOpenAiAssistantMessage(
            JToken content,
            IDictionary<string, string> toolNames)
        {
            if (content == null)
                return null;
            if (content.Type == JTokenType.String)
                return new JObject { ["role"] = "assistant", ["content"] = content.ToString() };

            JArray blocks = content as JArray;
            if (blocks == null)
                return new JObject { ["role"] = "assistant", ["content"] = content.ToString(Formatting.None) };

            var text = new StringBuilder();
            var toolCalls = new JArray();
            foreach (JObject block in blocks.OfType<JObject>())
            {
                string type = (string)block["type"];
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
                {
                    string value = (string)(block["text"] ?? block["thinking"]);
                    if (!string.IsNullOrEmpty(value))
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(value);
                    }
                }
                else if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    string id = (string)block["id"] ?? ("call_" + Guid.NewGuid().ToString("N"));
                    string name = (string)block["name"] ?? string.Empty;
                    toolNames[id] = name;
                    toolCalls.Add(new JObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = name,
                            ["arguments"] = (block["input"] as JObject ?? new JObject()).ToString(Formatting.None)
                        }
                    });
                }
            }

            var result = new JObject { ["role"] = "assistant" };
            result["content"] = text.Length == 0 ? JValue.CreateNull() : new JValue(text.ToString());
            if (toolCalls.Count > 0)
                result["tool_calls"] = toolCalls;
            return result;
        }

        private static IEnumerable<JObject> ConvertOpenAiUserMessages(
            JToken content,
            IDictionary<string, string> toolNames)
        {
            if (content == null)
                yield break;
            if (content.Type == JTokenType.String)
            {
                yield return new JObject { ["role"] = "user", ["content"] = content.ToString() };
                yield break;
            }

            JArray blocks = content as JArray;
            if (blocks == null)
            {
                yield return new JObject { ["role"] = "user", ["content"] = content.ToString(Formatting.None) };
                yield break;
            }

            var textBlocks = new JArray();
            foreach (JObject block in blocks.OfType<JObject>())
            {
                string type = (string)block["type"];
                if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = (string)block["tool_use_id"] ?? string.Empty,
                        ["content"] = block["content"] == null ? string.Empty : block["content"].ToString()
                    };
                    continue;
                }

                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    textBlocks.Add(new JObject
                    {
                        ["type"] = "text",
                        ["text"] = (string)block["text"] ?? string.Empty
                    });
                    continue;
                }

                if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
                {
                    JObject src = block["source"] as JObject;
                    if (src != null && string.Equals((string)src["type"], "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        string mime = (string)src["media_type"] ?? "image/png";
                        string data = (string)src["data"] ?? string.Empty;
                        textBlocks.Add(new JObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JObject
                            {
                                ["url"] = "data:" + mime + ";base64," + data
                            }
                        });
                    }
                }
            }

            if (textBlocks.Count > 0)
                yield return new JObject { ["role"] = "user", ["content"] = textBlocks };
        }

        private static JObject NormalizeOpenAiResponse(JObject response)
        {
            JObject choice = (response["choices"] as JArray)?.OfType<JObject>().FirstOrDefault();
            JObject message = choice?["message"] as JObject;
            var content = new JArray();
            string text = message?["content"] == null || message["content"].Type == JTokenType.Null
                ? null
                : message["content"].ToString();
            if (!string.IsNullOrEmpty(text))
                content.Add(new JObject { ["type"] = "text", ["text"] = text });

            JArray toolCalls = message?["tool_calls"] as JArray;
            if (toolCalls != null)
            {
                foreach (JObject call in toolCalls.OfType<JObject>())
                {
                    JObject function = call["function"] as JObject;
                    JObject input = new JObject();
                    string args = (string)function?["arguments"];
                    if (!string.IsNullOrWhiteSpace(args))
                    {
                        try { input = JObject.Parse(args); }
                        catch { input = new JObject { ["value"] = args }; }
                    }
                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = (string)call["id"] ?? ("call_" + Guid.NewGuid().ToString("N")),
                        ["name"] = (string)function?["name"] ?? string.Empty,
                        ["input"] = input
                    });
                }
            }

            string finish = (string)choice?["finish_reason"] ?? string.Empty;
            string stop = toolCalls != null && toolCalls.Count > 0
                ? "tool_use"
                : string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase)
                    ? "max_tokens"
                    : string.Equals(finish, "content_filter", StringComparison.OrdinalIgnoreCase)
                        ? "refusal"
                        : "end_turn";

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stop,
                ["usage"] = new JObject
                {
                    ["input_tokens"] = ReadInt(response["usage"]?["prompt_tokens"]),
                    ["output_tokens"] = ReadInt(response["usage"]?["completion_tokens"])
                }
            };
        }

        private static string ReadSystemText(JToken system)
        {
            if (system == null)
                return string.Empty;
            if (system.Type == JTokenType.String)
                return system.ToString();

            var builder = new StringBuilder();
            JArray blocks = system as JArray;
            if (blocks == null)
                return system.ToString();
            foreach (JObject block in blocks.OfType<JObject>())
            {
                string text = (string)block["text"];
                if (string.IsNullOrEmpty(text))
                    continue;
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(text);
            }
            return builder.ToString();
        }

        private static AgentProxyResponse ProviderHttpFailure(
            HttpStatusCode status,
            string raw,
            string agentName,
            JarvisAgentRuntimeTarget target)
        {
            string reason;
            int code = (int)status;
            if (code == 401 || code == 403)
                reason = "provider_auth_failed";
            else if (code == 429)
            {
                reason = IsCreditsError(raw)
                    ? "provider_credits_exhausted"
                    : "provider_rate_limited";
            }
            else if (code == 400 || code == 404 || code == 422)
                reason = "provider_model_or_request_invalid";
            else
                reason = "provider_upstream_error";

            DebugLog.Log("[AI-DIRECT] provider error agent=" + Safe(agentName) +
                " provider=" + Safe(target == null ? null : target.Provider) +
                " model=" + Safe(target == null ? null : target.Model) +
                " http=" + code.ToString() + " reason=" + reason);
            return Failure(reason, agentName, target);
        }

        private static bool IsCreditsError(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            string value = raw.ToLowerInvariant();
            return value.Contains("credit") &&
                   (value.Contains("balance") || value.Contains("quota") || value.Contains("billing"));
        }

        private static AgentProxyResponse Success(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string raw,
            string text,
            int input,
            int output)
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

        private static AgentProxyResponse Failure(
            string reason,
            string agentName,
            JarvisAgentRuntimeTarget target)
        {
            return new AgentProxyResponse
            {
                Success = false,
                CreditsExhausted = string.Equals(
                    reason, "provider_credits_exhausted", StringComparison.Ordinal),
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
                case "provider_auth_failed":
                    return "Ο AI provider απέρριψε τα διαπιστευτήρια.";
                case "provider_model_or_request_invalid":
                    return "Το επιλεγμένο AI model ή το αίτημα δεν είναι έγκυρο.";
                case "provider_credits_exhausted":
                    return "Το AI account έχει εξαντλήσει τα credits του.";
                case "provider_rate_limited":
                    return "Ο AI provider έχει προσωρινό όριο κλήσεων. Δοκίμασε ξανά σε λίγο.";
                case "provider_timeout":
                    return "Ο AI provider δεν απάντησε εγκαίρως.";
                case "provider_chat_adapter_unavailable":
                    return "Ο συγκεκριμένος AI provider δεν υποστηρίζεται ακόμη από το direct runtime.";
                case "provider_credential_unavailable":
                    return "Το session credential του AI agent δεν είναι διαθέσιμο. Εκτέλεσε HEALTH ή άνοιξε ξανά τον Jarvis.";
                default:
                    return "Η απευθείας κλήση προς τον AI provider απέτυχε (" + reason + ").";
            }
        }

        private static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "gemini" || value == "googleai" || value == "google-ai")
                return "google";
            if (value == "claude")
                return "anthropic";
            return value;
        }

        private static string FirstAnthropicText(JArray content)
        {
            if (content == null)
                return null;
            JObject block = content.OfType<JObject>().FirstOrDefault(x =>
                string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase));
            return block == null ? null : (string)block["text"];
        }

        private static int ReadInt(JToken token)
        {
            if (token == null)
                return 0;
            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return safe.Length > 180 ? safe.Substring(0, 180) : safe;
        }
    }
}
