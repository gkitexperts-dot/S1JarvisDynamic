using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Provider-neutral semantic planning protocol.
    ///
    /// The model is allowed to select only registered atomic TaskTypes and to
    /// describe dependencies/input bindings. Capability, owner agent and tools
    /// are always resolved locally from JarvisTaskRegistry; they are never
    /// trusted from model output.
    ///
    /// This layer is side-effect free and is not wired into Main Chat yet.
    /// </summary>
    internal static class JarvisSemanticPlanning
    {
        internal static string BuildPlannerSystemPrompt()
        {
            return
                "Είσαι ο εσωτερικός planner του Jarvis. Ανάλυσε το αίτημα του χειριστή σε atomic business tasks. " +
                "Χρησιμοποίησε ΜΟΝΟ taskType που υπάρχουν στο TASK_CATALOG. Μην επινοείς agents, tools ή task types. " +
                "Μην εκτελείς την εργασία και μην απαντάς στον χειριστή. Επέστρεψε ΜΟΝΟ έγκυρο JSON. " +
                "Κάθε task πρέπει να έχει μοναδικό id, taskType, intentFragment, inputs και dependsOn. " +
                "Το dependsOn περιέχει ids άλλων tasks του ίδιου plan. " +
                "Βάλε input μόνο όταν προκύπτει καθαρά από το αίτημα. Μην μαντεύεις missing business data. " +
                "Μην ενώνεις ανεξάρτητο read και write intent στο ίδιο task. Για απλό αίτημα χρησιμοποίησε ένα task. " +
                "Για σύνθετο αίτημα χρησιμοποίησε όσα atomic tasks χρειάζονται, χωρίς καρτεσιανούς συνδυασμούς. " +
                "Όπου το δεύτερο task χρειάζεται αποτέλεσμα του πρώτου, βάλε dependsOn. Ανεξάρτητα tasks δεν πρέπει να έχουν ψεύτικη dependency. " +
                "Schema: {\"tasks\":[{\"id\":\"t1\",\"taskType\":\"...\",\"intentFragment\":\"...\",\"inputs\":{\"name\":value},\"dependsOn\":[\"t0\"]}]}";
        }

        internal static string BuildTaskCatalogJson()
        {
            string[] auditIssues = JarvisTaskRegistryAudit.Validate();
            if (auditIssues.Length > 0)
            {
                throw new InvalidOperationException(
                    "Jarvis orchestration task registry is not planner-ready: " +
                    string.Join(" | ", auditIssues));
            }

            JArray tasks = new JArray();
            foreach (JarvisTaskDescriptor descriptor in JarvisTaskRegistry.AllTasks)
            {
                tasks.Add(new JObject
                {
                    ["taskType"] = descriptor.TaskType,
                    ["capability"] = descriptor.Capability,
                    ["operation"] = descriptor.Operation.ToString(),
                    ["executionPolicy"] = descriptor.ExecutionPolicy.ToString(),
                    ["description"] = descriptor.Description,
                    ["requiredInputs"] = new JArray(descriptor.RequiredInputs),
                    ["optionalInputs"] = new JArray(descriptor.OptionalInputs),
                    ["produces"] = new JArray(descriptor.Produces),
                    ["dependencyCapabilities"] = new JArray(descriptor.DependencyCapabilities),
                    ["requiresConfirmation"] = descriptor.RequiresConfirmation
                });
            }

            return new JObject { ["TASK_CATALOG"] = tasks }.ToString(Formatting.None);
        }

        internal static string BuildPlannerUserPayload(string userPrompt)
        {
            return new JObject
            {
                ["userPrompt"] = userPrompt ?? string.Empty,
                ["catalog"] = JObject.Parse(BuildTaskCatalogJson())["TASK_CATALOG"]
            }.ToString(Formatting.None);
        }

        internal static bool TryParsePlan(
            string responseJson,
            string originalPrompt,
            out JarvisPlan plan,
            out string[] issues)
        {
            plan = new JarvisPlan { OriginalPrompt = originalPrompt ?? string.Empty };
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(responseJson))
            {
                errors.Add("Semantic planner returned an empty response.");
                issues = errors.ToArray();
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(responseJson);
            }
            catch (JsonException ex)
            {
                errors.Add("Semantic planner returned invalid JSON: " + ex.Message);
                issues = errors.ToArray();
                return false;
            }

            JArray taskArray = root["tasks"] as JArray;
            if (taskArray == null)
            {
                errors.Add("Semantic planner response is missing tasks array.");
                issues = errors.ToArray();
                return false;
            }

            var externalToInternal = new Dictionary<string, JarvisPlannedTask>(StringComparer.OrdinalIgnoreCase);
            var pendingDependencies = new Dictionary<JarvisPlannedTask, string[]>();

            foreach (JObject item in taskArray.OfType<JObject>())
            {
                string externalId = item["id"] == null ? null : item["id"].ToString().Trim();
                string taskType = item["taskType"] == null ? null : item["taskType"].ToString().Trim();
                string intentFragment = item["intentFragment"] == null
                    ? string.Empty
                    : item["intentFragment"].ToString();

                if (string.IsNullOrWhiteSpace(externalId))
                {
                    errors.Add("Planner task without id.");
                    continue;
                }
                if (externalToInternal.ContainsKey(externalId))
                {
                    errors.Add("Duplicate planner task id: " + externalId);
                    continue;
                }

                JarvisPlannedTask task = JarvisPlanFactory.CreateTask(taskType, intentFragment);
                if (task == null)
                {
                    errors.Add("Unknown taskType: " + (taskType ?? "<null>"));
                    continue;
                }

                BindLiteralInputs(task, item["inputs"] as JObject, errors);
                plan.Tasks.Add(task);
                externalToInternal[externalId] = task;

                JArray dependencyArray = item["dependsOn"] as JArray;
                pendingDependencies[task] = dependencyArray == null
                    ? new string[0]
                    : dependencyArray
                        .Where(x => x != null && x.Type == JTokenType.String)
                        .Select(x => x.ToString().Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToArray();
            }

            foreach (KeyValuePair<JarvisPlannedTask, string[]> entry in pendingDependencies)
            {
                foreach (string externalDependencyId in entry.Value)
                {
                    JarvisPlannedTask dependency;
                    if (!externalToInternal.TryGetValue(externalDependencyId, out dependency))
                    {
                        errors.Add("Unknown dependsOn id: " + externalDependencyId + " for " + entry.Key.TaskType);
                        continue;
                    }

                    if (!entry.Key.DependsOnTaskIds.Contains(dependency.TaskId, StringComparer.OrdinalIgnoreCase))
                        entry.Key.DependsOnTaskIds.Add(dependency.TaskId);
                }
            }

            errors.AddRange(plan.Validate());
            issues = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return issues.Length == 0;
        }

        private static void BindLiteralInputs(
            JarvisPlannedTask task,
            JObject inputObject,
            List<string> errors)
        {
            if (task == null || inputObject == null)
                return;

            JarvisTaskDescriptor descriptor = task.Descriptor;
            if (descriptor == null)
                return;

            var allowed = new HashSet<string>(
                descriptor.RequiredInputs.Concat(descriptor.OptionalInputs),
                StringComparer.OrdinalIgnoreCase);

            foreach (JProperty property in inputObject.Properties())
            {
                string name = property.Name == null ? string.Empty : property.Name.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!allowed.Contains(name))
                {
                    errors.Add("Unknown input '" + name + "' for task " + task.TaskType);
                    continue;
                }

                object value = ConvertLiteral(property.Value);
                if (value == null)
                    continue;

                task.Inputs.Add(new JarvisTaskInputBinding
                {
                    Name = name,
                    Value = value
                });
            }
        }

        private static object ConvertLiteral(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return null;

            switch (token.Type)
            {
                case JTokenType.String:
                    return token.ToString();
                case JTokenType.Integer:
                    return token.Value<long>();
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                default:
                    return token.DeepClone();
            }
        }
    }
}
