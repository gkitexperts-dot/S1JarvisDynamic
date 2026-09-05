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
    /// Direct provider transport after NativeS1 boot provisioning.
    /// Provider, model, runtime transport and credential are supplied by the
    /// Verilic session registry. No model name is used to choose an endpoint.
    /// </summary>
    internal static class JarvisDirectAiTransport
    {
        private const string OpenAiAuto = "auto";
        private const string OpenAiResponses = "responses";
        private const string OpenAiChatCompletions = "chat_completions";
        private const string AnthropicMessages = "messages";
        private const string GoogleGenerateContent = "generate_content";

        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

        static JarvisDirectAiTransport()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
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
                    " provider=" + Safe(provider) +
                    " model=" + Safe(target.Model) +
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

            using (var message = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = new StringContent(request.ToString(Formatting.None), Encoding.UTF8, "application/json");
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                message.Headers.TryAddWithoutValidation("anthropic-beta", "prompt-caching-2024-07-31");

                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
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

        private static async Task<AgentProxyResponse> SendOpenAiResponsesAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken,
            bool allowChatFallback)
        {
            JObject neutral = JObject.Parse(requestJson ?? "{}");
            JObject request = BuildOpenAiResponsesRequest(neutral, target.Model);
            string apiKey = target.GetApiKey();

            using (var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses"))
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = new StringContent(request.ToString(Formatting.None), Encoding.UTF8, "application/json");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (allowChatFallback && ProviderRequestsChatCompletions(raw))
                        {
                            DebugLog.Log("[AI-DIRECT] OpenAI transport negotiation responses->chat_completions; model=" + Safe(target.Model));
                            return await SendOpenAiChatAsync(agentName, target, requestJson, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);
                    }

                    JObject parsed = JObject.Parse(raw);
                    JObject normalized = NormalizeOpenAiResponsesResponse(parsed);
                    int input = ReadInt(parsed["usage"]?["input_tokens"]);
                    int output = ReadInt(parsed["usage"]?["output_tokens"]);
                    string text = FirstAnthropicText(normalized["content"] as JArray);
                    return Success(agentName, target, normalized.ToString(Formatting.None), text, input, output);
                }
            }
        }

        private static async Task<AgentProxyResponse> SendOpenAiChatAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken)
        {
            JObject neutral = JObject.Parse(requestJson ?? "{}");
            JObject request = BuildOpenAiChatRequest(neutral, target.Model);
            string apiKey = target.GetApiKey();

            using (var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions"))
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = new StringContent(request.ToString(Formatting.None), Encoding.UTF8, "application/json");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);

                    JObject parsed = JObject.Parse(raw);
                    JObject normalized = NormalizeOpenAiChatResponse(parsed);
                    int input = ReadInt(parsed["usage"]?["prompt_tokens"]);
                    int output = ReadInt(parsed["usage"]?["completion_tokens"]);
                    string text = FirstAnthropicText(normalized["content"] as JArray);
                    return Success(agentName, target, normalized.ToString(Formatting.None), text, input, output);
                }
            }
        }

        private static JObject BuildOpenAiResponsesRequest(JObject source, string model)
        {
            var result = new JObject { ["model"] = model };
            string system = ReadSystemText(source["system"]);
            if (!string.IsNullOrWhiteSpace(system))
                result["instructions"] = system;

            var input = new JArray();
            JArray messages = source["messages"] as JArray ?? new JArray();
            foreach (JObject message in messages.OfType<JObject>())
                AppendResponsesInput(input, message);
            result["input"] = input;

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
                        ["name"] = name,
                        ["description"] = (string)tool["description"] ?? string.Empty,
                        ["parameters"] = (tool["input_schema"] ?? EmptyObjectSchema()).DeepClone()
                    });
                }
                if (tools.Count > 0)
                {
                    result["tools"] = tools;
                    ApplyResponsesToolChoice(result, source["tool_choice"]);
                }
            }

            int maxTokens = ReadInt(source["max_tokens"]);
            if (maxTokens > 0) result["max_output_tokens"] = maxTokens;

            JObject outputConfig = source["output_config"] as JObject;
            string effort = (string)outputConfig?["effort"];
            if (!string.IsNullOrWhiteSpace(effort))
                result["reasoning"] = new JObject { ["effort"] = effort.Trim().ToLowerInvariant() };

            return result;
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
                        ["call_id"] = (string)block["id"] ?? ("call_" + Guid.NewGuid().ToString("N")),
                        ["name"] = (string)block["name"] ?? string.Empty,
                        ["arguments"] = (block["input"] ?? new JObject()).ToString(Formatting.None)
                    });
                    continue;
                }
                if (type == "tool_result")
                {
                    input.Add(new JObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = (string)block["tool_use_id"] ?? string.Empty,
                        ["output"] = ToolResultText(block["content"])
                    });
                    continue;
                }
                if (type == "text" || type == "thinking")
                {
                    string text = (string)(block["text"] ?? block["thinking"]);
                    if (!string.IsNullOrEmpty(text))
                        messageContent.Add(new JObject { ["type"] = "input_text", ["text"] = text });
                    continue;
                }
                if (type == "image")
                {
                    JObject src = block["source"] as JObject;
                    if (src != null && string.Equals((string)src["type"], "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        string mime = (string)src["media_type"] ?? "image/png";
                        string data = (string)src["data"] ?? string.Empty;
                        messageContent.Add(new JObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = "data:" + mime + ";base64," + data
                        });
                    }
                    continue;
                }
                if (type == "document")
                {
                    JObject src = block["source"] as JObject;
                    if (src != null && string.Equals((string)src["type"], "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        string mime = (string)src["media_type"] ?? "application/pdf";
                        string data = (string)src["data"] ?? string.Empty;
                        messageContent.Add(new JObject
                        {
                            ["type"] = "input_file",
                            ["filename"] = "attachment.pdf",
                            ["file_data"] = "data:" + mime + ";base64," + data
                        });
                    }
                }
            }

            if (messageContent.Count > 0)
                input.Add(new JObject { ["role"] = role, ["content"] = messageContent });
        }

        private static void ApplyResponsesToolChoice(JObject result, JToken neutralChoice)
        {
            if (neutralChoice == null) return;
            string type = neutralChoice.Type == JTokenType.String
                ? neutralChoice.ToString()
                : (string)neutralChoice["type"];
            type = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "tool")
            {
                string name = (string)neutralChoice["name"];
                if (!string.IsNullOrWhiteSpace(name))
                    result["tool_choice"] = new JObject { ["type"] = "function", ["name"] = name };
            }
            else if (type == "any" || type == "required") result["tool_choice"] = "required";
            else if (type == "none") result["tool_choice"] = "none";
            else if (type == "auto") result["tool_choice"] = "auto";
        }

        private static JObject NormalizeOpenAiResponsesResponse(JObject response)
        {
            var content = new JArray();
            bool hasTool = false;
            JArray output = response["output"] as JArray ?? new JArray();
            foreach (JObject item in output.OfType<JObject>())
            {
                string type = ((string)item["type"] ?? string.Empty).ToLowerInvariant();
                if (type == "function_call")
                {
                    hasTool = true;
                    JObject args = new JObject();
                    string rawArgs = (string)item["arguments"];
                    if (!string.IsNullOrWhiteSpace(rawArgs))
                    {
                        try { args = JObject.Parse(rawArgs); }
                        catch { args = new JObject { ["value"] = rawArgs }; }
                    }
                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = (string)item["call_id"] ?? (string)item["id"] ?? ("call_" + Guid.NewGuid().ToString("N")),
                        ["name"] = (string)item["name"] ?? string.Empty,
                        ["input"] = args
                    });
                    continue;
                }
                if (type != "message") continue;
                JArray parts = item["content"] as JArray;
                if (parts == null) continue;
                foreach (JObject part in parts.OfType<JObject>())
                {
                    string partType = ((string)part["type"] ?? string.Empty).ToLowerInvariant();
                    string text = partType == "refusal" ? (string)part["refusal"] : (string)part["text"];
                    if (!string.IsNullOrEmpty(text))
                        content.Add(new JObject { ["type"] = "text", ["text"] = text });
                }
            }

            string stop = hasTool ? "tool_use" : "end_turn";
            JObject incomplete = response["incomplete_details"] as JObject;
            string incompleteReason = (string)incomplete?["reason"];
            if (!hasTool && string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
                stop = "max_tokens";

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stop,
                ["usage"] = new JObject
                {
                    ["input_tokens"] = ReadInt(response["usage"]?["input_tokens"]),
                    ["output_tokens"] = ReadInt(response["usage"]?["output_tokens"])
                }
            };
        }

        private static JObject BuildOpenAiChatRequest(JObject source, string model)
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
                    if (assistant != null) messages.Add(assistant);
                }
                else
                {
                    foreach (JObject converted in ConvertOpenAiUserMessages(content))
                        messages.Add(converted);
                }
            }

            var result = new JObject { ["model"] = model, ["messages"] = messages };
            int maxTokens = ReadInt(source["max_tokens"]);
            if (maxTokens > 0) result["max_completion_tokens"] = maxTokens;
            if (source["temperature"] != null) result["temperature"] = source["temperature"].DeepClone();

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
                            ["parameters"] = (tool["input_schema"] ?? EmptyObjectSchema()).DeepClone()
                        }
                    });
                }
                if (tools.Count > 0)
                {
                    result["tools"] = tools;
                    ApplyChatToolChoice(result, source["tool_choice"]);
                }
            }
            return result;
        }

        private static JObject ConvertOpenAiAssistantMessage(JToken content, IDictionary<string, string> toolNames)
        {
            if (content == null) return null;
            if (content.Type == JTokenType.String)
                return new JObject { ["role"] = "assistant", ["content"] = content.ToString() };
            JArray blocks = content as JArray;
            if (blocks == null)
                return new JObject { ["role"] = "assistant", ["content"] = content.ToString(Formatting.None) };

            var text = new StringBuilder();
            var calls = new JArray();
            foreach (JObject block in blocks.OfType<JObject>())
            {
                string type = (string)block["type"];
                if (type == "text" || type == "thinking")
                {
                    string value = (string)(block["text"] ?? block["thinking"]);
                    if (!string.IsNullOrEmpty(value)) { if (text.Length > 0) text.Append('\n'); text.Append(value); }
                }
                else if (type == "tool_use")
                {
                    string id = (string)block["id"] ?? ("call_" + Guid.NewGuid().ToString("N"));
                    string name = (string)block["name"] ?? string.Empty;
                    toolNames[id] = name;
                    calls.Add(new JObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = name,
                            ["arguments"] = (block["input"] ?? new JObject()).ToString(Formatting.None)
                        }
                    });
                }
            }
            var result = new JObject { ["role"] = "assistant", ["content"] = text.Length == 0 ? JValue.CreateNull() : new JValue(text.ToString()) };
            if (calls.Count > 0) result["tool_calls"] = calls;
            return result;
        }

        private static IEnumerable<JObject> ConvertOpenAiUserMessages(JToken content)
        {
            if (content == null) yield break;
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
                if (type == "tool_result")
                {
                    yield return new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = (string)block["tool_use_id"] ?? string.Empty,
                        ["content"] = ToolResultText(block["content"])
                    };
                }
                else if (type == "text")
                {
                    textBlocks.Add(new JObject { ["type"] = "text", ["text"] = (string)block["text"] ?? string.Empty });
                }
                else if (type == "image")
                {
                    JObject src = block["source"] as JObject;
                    if (src != null && string.Equals((string)src["type"], "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        string mime = (string)src["media_type"] ?? "image/png";
                        string data = (string)src["data"] ?? string.Empty;
                        textBlocks.Add(new JObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JObject { ["url"] = "data:" + mime + ";base64," + data }
                        });
                    }
                }
            }
            if (textBlocks.Count > 0)
                yield return new JObject { ["role"] = "user", ["content"] = textBlocks };
        }

        private static void ApplyChatToolChoice(JObject result, JToken choice)
        {
            if (choice == null) return;
            string type = choice.Type == JTokenType.String ? choice.ToString() : (string)choice["type"];
            type = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "tool")
            {
                string name = (string)choice["name"];
                if (!string.IsNullOrWhiteSpace(name))
                    result["tool_choice"] = new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = name } };
            }
            else if (type == "any" || type == "required") result["tool_choice"] = "required";
            else if (type == "none") result["tool_choice"] = "none";
            else if (type == "auto") result["tool_choice"] = "auto";
        }

        private static JObject NormalizeOpenAiChatResponse(JObject response)
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
                    JObject function = call["function"] as JObject;
                    JObject args = new JObject();
                    string rawArgs = (string)function?["arguments"];
                    if (!string.IsNullOrWhiteSpace(rawArgs)) { try { args = JObject.Parse(rawArgs); } catch { args = new JObject { ["value"] = rawArgs }; } }
                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = (string)call["id"] ?? ("call_" + Guid.NewGuid().ToString("N")),
                        ["name"] = (string)function?["name"] ?? string.Empty,
                        ["input"] = args
                    });
                }
            }
            string finish = (string)choice?["finish_reason"] ?? string.Empty;
            string stop = calls != null && calls.Count > 0 ? "tool_use" :
                string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) ? "max_tokens" : "end_turn";
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

        private static async Task<AgentProxyResponse> SendGoogleAsync(
            string agentName,
            JarvisAgentRuntimeTarget target,
            string requestJson,
            CancellationToken cancellationToken)
        {
            JObject neutral = JObject.Parse(requestJson ?? "{}");
            JObject request = BuildGoogleRequest(neutral);
            string endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" +
                Uri.EscapeDataString(target.Model) + ":generateContent";
            string apiKey = target.GetApiKey();

            using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                message.Content = new StringContent(request.ToString(Formatting.None), Encoding.UTF8, "application/json");
                message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                using (HttpResponseMessage response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ProviderHttpFailure(response.StatusCode, raw, agentName, target);
                    JObject parsed = JObject.Parse(raw);
                    JObject normalized = NormalizeGoogleResponse(parsed);
                    int input = ReadInt(parsed["usageMetadata"]?["promptTokenCount"]);
                    int output = ReadInt(parsed["usageMetadata"]?["candidatesTokenCount"]);
                    return Success(agentName, target, normalized.ToString(Formatting.None),
                        FirstAnthropicText(normalized["content"] as JArray), input, output);
                }
            }
        }

        private static JObject BuildGoogleRequest(JObject source)
        {
            var result = new JObject();
            string system = ReadSystemText(source["system"]);
            if (!string.IsNullOrWhiteSpace(system))
                result["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = system }) };

            var contents = new JArray();
            JArray messages = source["messages"] as JArray ?? new JArray();
            foreach (JObject message in messages.OfType<JObject>())
            {
                string role = ((string)message["role"] ?? "user").ToLowerInvariant();
                var parts = new JArray();
                JToken content = message["content"];
                if (content != null && content.Type == JTokenType.String)
                    parts.Add(new JObject { ["text"] = content.ToString() });
                else
                {
                    JArray blocks = content as JArray;
                    if (blocks != null)
                    {
                        foreach (JObject block in blocks.OfType<JObject>())
                        {
                            string type = ((string)block["type"] ?? string.Empty).ToLowerInvariant();
                            if (type == "text" || type == "thinking")
                            {
                                string text = (string)(block["text"] ?? block["thinking"]);
                                if (!string.IsNullOrEmpty(text)) parts.Add(new JObject { ["text"] = text });
                            }
                            else if (type == "tool_use")
                            {
                                parts.Add(new JObject
                                {
                                    ["functionCall"] = new JObject
                                    {
                                        ["name"] = (string)block["name"] ?? string.Empty,
                                        ["args"] = (block["input"] ?? new JObject()).DeepClone()
                                    }
                                });
                            }
                            else if (type == "tool_result")
                            {
                                parts.Add(new JObject
                                {
                                    ["functionResponse"] = new JObject
                                    {
                                        ["name"] = "tool",
                                        ["response"] = new JObject { ["result"] = ToolResultText(block["content"]) }
                                    }
                                });
                            }
                            else if (type == "image" || type == "document")
                            {
                                JObject src = block["source"] as JObject;
                                if (src != null && string.Equals((string)src["type"], "base64", StringComparison.OrdinalIgnoreCase))
                                {
                                    parts.Add(new JObject
                                    {
                                        ["inlineData"] = new JObject
                                        {
                                            ["mimeType"] = (string)src["media_type"] ?? (type == "document" ? "application/pdf" : "image/png"),
                                            ["data"] = (string)src["data"] ?? string.Empty
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                if (parts.Count > 0)
                    contents.Add(new JObject { ["role"] = role == "assistant" ? "model" : "user", ["parts"] = parts });
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
                    declarations.Add(new JObject
                    {
                        ["name"] = name,
                        ["description"] = (string)tool["description"] ?? string.Empty,
                        ["parameters"] = NormalizeGoogleSchema(tool["input_schema"] ?? EmptyObjectSchema())
                    });
                }
                if (declarations.Count > 0)
                    result["tools"] = new JArray(new JObject { ["functionDeclarations"] = declarations });
            }

            int maxTokens = ReadInt(source["max_tokens"]);
            if (maxTokens > 0)
                result["generationConfig"] = new JObject { ["maxOutputTokens"] = maxTokens };
            return result;
        }

        private static JToken NormalizeGoogleSchema(JToken schema)
        {
            JObject source = schema as JObject;
            if (source == null) return new JObject { ["type"] = "object" };
            var result = new JObject();
            string[] scalar = { "type", "description", "format", "nullable", "minimum", "maximum", "minItems", "maxItems", "minLength", "maxLength" };
            foreach (string name in scalar) if (source[name] != null) result[name] = source[name].DeepClone();
            if (source["enum"] is JArray) result["enum"] = source["enum"].DeepClone();
            JObject properties = source["properties"] as JObject;
            if (properties != null)
            {
                var normalized = new JObject();
                foreach (JProperty p in properties.Properties()) normalized[p.Name] = NormalizeGoogleSchema(p.Value);
                result["properties"] = normalized;
            }
            if (source["required"] is JArray) result["required"] = source["required"].DeepClone();
            if (source["items"] != null) result["items"] = NormalizeGoogleSchema(source["items"]);
            if (result["type"] == null)
                result["type"] = result["items"] != null ? "array" : "object";
            return result;
        }

        private static JObject NormalizeGoogleResponse(JObject response)
        {
            var content = new JArray();
            bool hasTool = false;
            JObject candidate = (response["candidates"] as JArray)?.OfType<JObject>().FirstOrDefault();
            JObject googleContent = candidate?["content"] as JObject;
            JArray parts = googleContent?["parts"] as JArray;
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
                        content.Add(new JObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = "call_" + Guid.NewGuid().ToString("N"),
                            ["name"] = (string)call["name"] ?? string.Empty,
                            ["input"] = (call["args"] as JObject ?? new JObject()).DeepClone()
                        });
                    }
                }
            }
            string finish = (string)candidate?["finishReason"] ?? string.Empty;
            string stop = hasTool ? "tool_use" :
                string.Equals(finish, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase) ? "max_tokens" : "end_turn";
            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stop,
                ["usage"] = new JObject
                {
                    ["input_tokens"] = ReadInt(response["usageMetadata"]?["promptTokenCount"]),
                    ["output_tokens"] = ReadInt(response["usageMetadata"]?["candidatesTokenCount"])
                }
            };
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

        private static bool ProviderRequestsChatCompletions(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string value = raw.ToLowerInvariant();
            return value.Contains("chat/completions") &&
                   (value.Contains("only supported") || value.Contains("not supported") || value.Contains("use"));
        }

        private static JObject EmptyObjectSchema()
        {
            return new JObject { ["type"] = "object", ["properties"] = new JObject() };
        }

        private static string ToolResultText(JToken content)
        {
            if (content == null) return string.Empty;
            if (content.Type == JTokenType.String) return content.ToString();
            JArray blocks = content as JArray;
            if (blocks == null) return content.ToString(Formatting.None);
            var text = new StringBuilder();
            foreach (JObject block in blocks.OfType<JObject>())
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
            var builder = new StringBuilder();
            JArray blocks = system as JArray;
            if (blocks == null) return system.ToString();
            foreach (JObject block in blocks.OfType<JObject>())
            {
                string text = (string)block["text"];
                if (string.IsNullOrEmpty(text)) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(text);
            }
            return builder.ToString();
        }

        private static AgentProxyResponse ProviderHttpFailure(
            HttpStatusCode status, string raw, string agentName, JarvisAgentRuntimeTarget target)
        {
            int code = (int)status;
            string reason;
            if (code == 401 || code == 403) reason = "provider_auth_failed";
            else if (code == 429) reason = IsCreditsError(raw) ? "provider_credits_exhausted" : "provider_rate_limited";
            else if (code == 400 || code == 404 || code == 422) reason = "provider_model_or_request_invalid";
            else reason = "provider_upstream_error";

            string detail = ExtractProviderErrorDetail(raw);
            DebugLog.Log("[AI-DIRECT] provider error agent=" + Safe(agentName) +
                " provider=" + Safe(target == null ? null : target.Provider) +
                " model=" + Safe(target == null ? null : target.Model) +
                " transport=" + Safe(target == null ? null : target.RuntimeTransport) +
                " http=" + code.ToString() + " reason=" + reason +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : " detail=" + Safe(detail)));
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
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string value = raw.ToLowerInvariant();
            return value.Contains("credit") && (value.Contains("balance") || value.Contains("quota") || value.Contains("billing"));
        }

        private static AgentProxyResponse Success(
            string agentName, JarvisAgentRuntimeTarget target, string raw, string text, int input, int output)
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
                CreditsExhausted = string.Equals(reason, "provider_credits_exhausted", StringComparison.Ordinal),
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

        private static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "gemini" || value == "googleai" || value == "google-ai") return "google";
            if (value == "claude") return "anthropic";
            return value;
        }

        private static string FirstAnthropicText(JArray content)
        {
            if (content == null) return null;
            JObject block = content.OfType<JObject>().FirstOrDefault(x =>
                string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase));
            return block == null ? null : (string)block["text"];
        }

        private static int ReadInt(JToken token)
        {
            if (token == null) return 0;
            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return safe.Length > 180 ? safe.Substring(0, 180) : safe;
        }
    }
}
