using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisTaskStatus
    {
        Pending,
        Ready,
        WaitingForInput,
        WaitingForConfirmation,
        Running,
        Succeeded,
        Failed,
        Blocked,
        Skipped
    }

    internal enum JarvisPlanStatus
    {
        Draft,
        WaitingForInput,
        WaitingForConfirmation,
        Ready,
        Running,
        Completed,
        Failed,
        PartiallyCompleted
    }

    internal sealed class JarvisTaskInputBinding
    {
        public string Name { get; set; }
        public object Value { get; set; }
        public string SourceTaskId { get; set; }
        public string SourceOutputName { get; set; }

        public bool IsResolved
        {
            get
            {
                return Value != null ||
                    (!string.IsNullOrWhiteSpace(SourceTaskId) &&
                     !string.IsNullOrWhiteSpace(SourceOutputName));
            }
        }
    }

    internal sealed class JarvisPlannedTask
    {
        public JarvisPlannedTask()
        {
            TaskId = Guid.NewGuid().ToString("N");
            DependsOnTaskIds = new List<string>();
            Inputs = new List<JarvisTaskInputBinding>();
            Status = JarvisTaskStatus.Pending;
        }

        public string TaskId { get; set; }
        public string TaskType { get; set; }
        public string Capability { get; set; }
        public string OwnerAgent { get; set; }
        public string UserIntentFragment { get; set; }
        public bool RequiresConfirmation { get; set; }
        public JarvisTaskExecutionPolicy ExecutionPolicy { get; set; }
        public JarvisTaskStatus Status { get; set; }
        public List<string> DependsOnTaskIds { get; private set; }
        public List<JarvisTaskInputBinding> Inputs { get; private set; }

        internal JarvisTaskDescriptor Descriptor
        {
            get { return JarvisTaskRegistry.Find(TaskType); }
        }

        internal string[] GetMissingRequiredInputs()
        {
            JarvisTaskDescriptor descriptor = Descriptor;
            if (descriptor == null)
                return new[] { "__unknown_task_type__" };

            var resolved = new HashSet<string>(
                Inputs.Where(x => x != null && x.IsResolved && !string.IsNullOrWhiteSpace(x.Name))
                    .Select(x => x.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);

            return descriptor.RequiredInputs
                .Where(x => !resolved.Contains(x))
                .ToArray();
        }

        internal bool DependenciesSatisfied(IReadOnlyDictionary<string, JarvisTaskResult> results)
        {
            if (DependsOnTaskIds == null || DependsOnTaskIds.Count == 0)
                return true;
            if (results == null)
                return false;

            foreach (string dependencyId in DependsOnTaskIds)
            {
                JarvisTaskResult result;
                if (string.IsNullOrWhiteSpace(dependencyId) ||
                    !results.TryGetValue(dependencyId, out result) ||
                    result == null ||
                    result.Status != JarvisTaskStatus.Succeeded)
                    return false;
            }

            return true;
        }
    }

    internal sealed class JarvisTaskResult
    {
        public JarvisTaskResult()
        {
            Outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            ProducedArtifacts = new List<string>();
            MissingInputs = new List<string>();
        }

        public string TaskId { get; set; }
        public string TaskType { get; set; }
        public string OwnerAgent { get; set; }
        public JarvisTaskStatus Status { get; set; }
        public string UserMessage { get; set; }
        public string ErrorCode { get; set; }
        public Dictionary<string, object> Outputs { get; private set; }
        public List<string> ProducedArtifacts { get; private set; }
        public List<string> MissingInputs { get; private set; }

        internal static JarvisTaskResult Success(JarvisPlannedTask task)
        {
            return FromTask(task, JarvisTaskStatus.Succeeded);
        }

        internal static JarvisTaskResult NeedsInput(JarvisPlannedTask task, IEnumerable<string> missingInputs)
        {
            JarvisTaskResult result = FromTask(task, JarvisTaskStatus.WaitingForInput);
            if (missingInputs != null)
                result.MissingInputs.AddRange(missingInputs.Where(x => !string.IsNullOrWhiteSpace(x)));
            return result;
        }

        internal static JarvisTaskResult Failure(JarvisPlannedTask task, string errorCode, string userMessage)
        {
            JarvisTaskResult result = FromTask(task, JarvisTaskStatus.Failed);
            result.ErrorCode = errorCode;
            result.UserMessage = userMessage;
            return result;
        }

        private static JarvisTaskResult FromTask(JarvisPlannedTask task, JarvisTaskStatus status)
        {
            return new JarvisTaskResult
            {
                TaskId = task == null ? null : task.TaskId,
                TaskType = task == null ? null : task.TaskType,
                OwnerAgent = task == null ? null : task.OwnerAgent,
                Status = status
            };
        }
    }

    internal sealed class JarvisPlan
    {
        public JarvisPlan()
        {
            PlanId = Guid.NewGuid().ToString("N");
            Tasks = new List<JarvisPlannedTask>();
            Results = new Dictionary<string, JarvisTaskResult>(StringComparer.OrdinalIgnoreCase);
            Status = JarvisPlanStatus.Draft;
        }

        public string PlanId { get; set; }
        public string OriginalPrompt { get; set; }
        public JarvisPlanStatus Status { get; set; }
        public List<JarvisPlannedTask> Tasks { get; private set; }
        public Dictionary<string, JarvisTaskResult> Results { get; private set; }

        internal JarvisPlannedTask FindTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                return null;

            return Tasks.FirstOrDefault(x => string.Equals(
                x.TaskId,
                taskId,
                StringComparison.OrdinalIgnoreCase));
        }

        internal IEnumerable<JarvisPlannedTask> GetReadyTasks()
        {
            foreach (JarvisPlannedTask task in Tasks)
            {
                if (task == null ||
                    task.Status == JarvisTaskStatus.Succeeded ||
                    task.Status == JarvisTaskStatus.Failed ||
                    task.Status == JarvisTaskStatus.Blocked ||
                    task.Status == JarvisTaskStatus.Skipped ||
                    task.Status == JarvisTaskStatus.Running)
                    continue;

                if (!task.DependenciesSatisfied(Results))
                    continue;

                string[] missing = task.GetMissingRequiredInputs();
                if (missing.Length > 0)
                {
                    task.Status = JarvisTaskStatus.WaitingForInput;
                    continue;
                }

                if (task.RequiresConfirmation)
                {
                    task.Status = JarvisTaskStatus.WaitingForConfirmation;
                    continue;
                }

                task.Status = JarvisTaskStatus.Ready;
                yield return task;
            }
        }

        internal string[] Validate()
        {
            var issues = new List<string>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JarvisPlannedTask task in Tasks)
            {
                if (task == null)
                {
                    issues.Add("Plan contains null task.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(task.TaskId))
                    issues.Add("Task without TaskId.");
                else if (!ids.Add(task.TaskId))
                    issues.Add("Duplicate TaskId: " + task.TaskId);

                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(task.TaskType);
                if (descriptor == null)
                {
                    issues.Add("Unknown TaskType: " + (task.TaskType ?? "<null>"));
                    continue;
                }

                if (!string.Equals(task.Capability, descriptor.Capability, StringComparison.OrdinalIgnoreCase))
                    issues.Add("Capability mismatch for " + task.TaskType);
                if (!string.Equals(task.OwnerAgent, descriptor.OwnerAgent, StringComparison.OrdinalIgnoreCase))
                    issues.Add("OwnerAgent mismatch for " + task.TaskType);

                foreach (string dependencyId in task.DependsOnTaskIds)
                {
                    if (string.IsNullOrWhiteSpace(dependencyId))
                        issues.Add("Empty dependency on task " + task.TaskId);
                }
            }

            foreach (JarvisPlannedTask task in Tasks)
            {
                foreach (string dependencyId in task.DependsOnTaskIds)
                {
                    if (!ids.Contains(dependencyId))
                        issues.Add("Missing dependency " + dependencyId + " for task " + task.TaskId);
                    if (string.Equals(dependencyId, task.TaskId, StringComparison.OrdinalIgnoreCase))
                        issues.Add("Task depends on itself: " + task.TaskId);
                }
            }

            if (HasDependencyCycle())
                issues.Add("Plan contains a dependency cycle.");

            return issues.ToArray();
        }

        private bool HasDependencyCycle()
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JarvisPlannedTask task in Tasks)
            {
                if (task == null || string.IsNullOrWhiteSpace(task.TaskId))
                    continue;
                if (Visit(task.TaskId, visiting, visited))
                    return true;
            }

            return false;
        }

        private bool Visit(
            string taskId,
            HashSet<string> visiting,
            HashSet<string> visited)
        {
            if (visited.Contains(taskId))
                return false;
            if (!visiting.Add(taskId))
                return true;

            JarvisPlannedTask task = FindTask(taskId);
            if (task != null)
            {
                foreach (string dependencyId in task.DependsOnTaskIds)
                {
                    if (!string.IsNullOrWhiteSpace(dependencyId) &&
                        Visit(dependencyId, visiting, visited))
                        return true;
                }
            }

            visiting.Remove(taskId);
            visited.Add(taskId);
            return false;
        }
    }

    internal static class JarvisPlanFactory
    {
        internal static JarvisPlannedTask CreateTask(string taskType, string intentFragment)
        {
            JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(taskType);
            if (descriptor == null)
                return null;

            return new JarvisPlannedTask
            {
                TaskType = descriptor.TaskType,
                Capability = descriptor.Capability,
                OwnerAgent = descriptor.OwnerAgent,
                RequiresConfirmation = descriptor.RequiresConfirmation,
                ExecutionPolicy = descriptor.ExecutionPolicy,
                UserIntentFragment = intentFragment ?? string.Empty
            };
        }
    }
}
