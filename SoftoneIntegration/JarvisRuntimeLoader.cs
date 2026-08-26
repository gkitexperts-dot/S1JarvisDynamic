using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using Softone;

namespace S1Jarvis.SoftoneIntegration
{
    internal static class JarvisRuntimeLoader
    {
        private const string RuntimeAssemblyName = "S1Jarvis.Runtime";
        private const string RuntimeBridgeTypeName = "S1Jarvis.Runtime.JarvisRuntimeBridge";
        private const string PrivateResourcePrefix = "S1Jarvis.Private.";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Assembly> Loaded =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static bool _resolverInstalled;
        private static Assembly _runtimeAssembly;

        internal static FrameworkElement CreateShell(XSupport xSupport)
        {
            return InvokeFrameworkElement("CreateShell", new object[] { xSupport });
        }

        internal static FrameworkElement CreateVerilicMaintenanceShell()
        {
            return InvokeFrameworkElement("CreateVerilicMaintenanceShell", null);
        }

        private static FrameworkElement InvokeFrameworkElement(string methodName, object[] arguments)
        {
            Type bridge = GetBridgeType();
            MethodInfo method = bridge.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new MissingMethodException(RuntimeBridgeTypeName, methodName);

            object result = method.Invoke(null, arguments);
            FrameworkElement element = result as FrameworkElement;
            if (element == null)
                throw new InvalidOperationException("Embedded Jarvis runtime returned an invalid element for " + methodName + ".");
            return element;
        }

        internal static string InvokeString(string methodName)
        {
            Type bridge = GetBridgeType();
            MethodInfo method = bridge.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            if (method == null)
                throw new MissingMethodException(RuntimeBridgeTypeName, methodName);

            object result = method.Invoke(null, null);
            return result == null ? string.Empty : Convert.ToString(result);
        }

        private static Type GetBridgeType()
        {
            Assembly runtime = EnsureRuntimeLoaded();
            Type bridge = runtime.GetType(RuntimeBridgeTypeName, false, false);
            if (bridge == null)
                throw new TypeLoadException("Embedded Jarvis runtime bridge was not found.");
            return bridge;
        }

        private static Assembly EnsureRuntimeLoaded()
        {
            lock (Sync)
            {
                InstallResolver();
                if (_runtimeAssembly != null)
                    return _runtimeAssembly;

                _runtimeAssembly = LoadEmbeddedAssembly(RuntimeAssemblyName);
                if (_runtimeAssembly == null)
                    throw new FileNotFoundException("Embedded S1Jarvis.Runtime.dll was not found in S1Jarvis.dll.");
                return _runtimeAssembly;
            }
        }

        private static void InstallResolver()
        {
            if (_resolverInstalled)
                return;

            AppDomain.CurrentDomain.AssemblyResolve += ResolvePrivateAssembly;
            _resolverInstalled = true;
        }

        private static Assembly ResolvePrivateAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                string simpleName = requested.Name;
                if (string.IsNullOrWhiteSpace(simpleName))
                    return null;

                Assembly existing = FindLoadedAssembly(simpleName);
                if (existing != null)
                    return existing;

                lock (Sync)
                {
                    Assembly cached;
                    if (Loaded.TryGetValue(simpleName, out cached))
                        return cached;

                    if (IsWebView2Assembly(simpleName))
                    {
                        Assembly hostAssembly = TryLoadHostAssembly(simpleName);
                        if (hostAssembly != null)
                        {
                            Loaded[simpleName] = hostAssembly;
                            return hostAssembly;
                        }
                    }

                    return LoadEmbeddedAssembly(simpleName);
                }
            }
            catch
            {
                return null;
            }
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            foreach (Assembly existing in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName existingName;
                try { existingName = existing.GetName(); }
                catch { continue; }

                if (string.Equals(existingName.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }

            return null;
        }

        private static bool IsWebView2Assembly(string simpleName)
        {
            return string.Equals(simpleName, "Microsoft.Web.WebView2.Core", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(simpleName, "Microsoft.Web.WebView2.Wpf", StringComparison.OrdinalIgnoreCase);
        }

        private static Assembly TryLoadHostAssembly(string simpleName)
        {
            string fileName = simpleName + ".dll";
            string[] candidateDirectories =
            {
                GetOuterAssemblyDirectory(),
                AppDomain.CurrentDomain.BaseDirectory
            };

            for (int i = 0; i < candidateDirectories.Length; i++)
            {
                string directory = candidateDirectories[i];
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string path;
                try { path = Path.Combine(directory, fileName); }
                catch { continue; }

                if (!File.Exists(path))
                    continue;

                try { return Assembly.LoadFrom(path); }
                catch { }
            }

            return null;
        }

        private static string GetOuterAssemblyDirectory()
        {
            try
            {
                string location = typeof(JarvisRuntimeLoader).Assembly.Location;
                return string.IsNullOrWhiteSpace(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }

        private static Assembly LoadEmbeddedAssembly(string simpleName)
        {
            Assembly owner = typeof(JarvisRuntimeLoader).Assembly;
            string resourceName = PrivateResourcePrefix + simpleName + ".dll";
            using (Stream stream = owner.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    return null;

                byte[] bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }

                if (offset != bytes.Length)
                    throw new EndOfStreamException("Could not read embedded assembly " + resourceName + ".");

                Assembly assembly = Assembly.Load(bytes);
                Loaded[simpleName] = assembly;
                return assembly;
            }
        }
    }
}
