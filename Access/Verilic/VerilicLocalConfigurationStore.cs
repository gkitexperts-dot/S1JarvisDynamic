using System;
using System.IO;
using Newtonsoft.Json;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Non-secret local composition for the Verilic client. This store is
    /// deliberately separate from the DPAPI-protected installation state.
    /// It contains only deployment identifiers and runtime settings that are
    /// safe to persist as ordinary per-user configuration.
    /// </summary>
    internal sealed class VerilicLocalConfiguration
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("mode")]
        public string Mode { get; set; }

        [JsonProperty("origin")]
        public string Origin { get; set; }

        [JsonProperty("stateDirectory")]
        public string StateDirectory { get; set; }

        [JsonProperty("dpapiScope")]
        public string DpapiScope { get; set; }

        [JsonProperty("vendorId")]
        public string VendorId { get; set; }

        [JsonProperty("jarvisProductId")]
        public string JarvisProductId { get; set; }

        [JsonProperty("jarvisLicenceId")]
        public string JarvisLicenceId { get; set; }

        [JsonProperty("courierProductId")]
        public string CourierProductId { get; set; }

        [JsonProperty("courierLicenceId")]
        public string CourierLicenceId { get; set; }

        [JsonProperty("docReaderProductId")]
        public string DocReaderProductId { get; set; }

        [JsonProperty("docReaderLicenceId")]
        public string DocReaderLicenceId { get; set; }
    }

    internal static class VerilicLocalConfigurationStore
    {
        private const string FileName = "config.json";

        public static string GetDefaultDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "S1Jarvis",
                "Verilic");
        }

        public static string GetConfigurationPath()
        {
            return Path.Combine(GetDefaultDirectory(), FileName);
        }

        public static VerilicLocalConfiguration Load()
        {
            string path = GetConfigurationPath();
            if (!File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path);
                VerilicLocalConfiguration configuration =
                    JsonConvert.DeserializeObject<VerilicLocalConfiguration>(json);

                if (configuration == null || configuration.Version != 1)
                    throw new InvalidDataException(
                        "Unsupported or missing Verilic local configuration.");

                return configuration;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "Verilic local configuration is invalid.", ex);
            }
        }

        public static void Save(VerilicLocalConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            if (configuration.Version != 1)
                throw new InvalidDataException(
                    "Unsupported Verilic local configuration version.");

            string directory = GetDefaultDirectory();
            Directory.CreateDirectory(directory);

            string path = GetConfigurationPath();
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                string json = JsonConvert.SerializeObject(
                    configuration,
                    Formatting.Indented);
                File.WriteAllText(temporaryPath, json);

                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        public static VerilicLocalConfiguration ReadWindowsUserEnvironment()
        {
            return new VerilicLocalConfiguration
            {
                Version = 1,
                Mode = ReadUserVariable("S1JARVIS_VERILIC_MODE"),
                Origin = ReadUserVariable("S1JARVIS_VERILIC_ORIGIN"),
                StateDirectory = ReadUserVariable("S1JARVIS_VERILIC_STATE_DIR"),
                DpapiScope = ReadUserVariable("S1JARVIS_VERILIC_DPAPI_SCOPE"),
                VendorId = ReadUserVariable("S1JARVIS_VERILIC_VENDOR_ID"),
                JarvisProductId = ReadUserVariable("S1JARVIS_VERILIC_PRODUCT_ID"),
                JarvisLicenceId = ReadUserVariable("S1JARVIS_VERILIC_LICENCE_ID"),
                CourierProductId = ReadUserVariable("S1JARVISCOURIER_VERILIC_PRODUCT_ID"),
                CourierLicenceId = ReadUserVariable("S1JARVISCOURIER_VERILIC_LICENCE_ID"),
                DocReaderProductId = ReadUserVariable("S1JARVISDOCREADER_VERILIC_PRODUCT_ID"),
                DocReaderLicenceId = ReadUserVariable("S1JARVISDOCREADER_VERILIC_LICENCE_ID")
            };
        }

        private static string ReadUserVariable(string name)
        {
            return Environment.GetEnvironmentVariable(
                name,
                EnvironmentVariableTarget.User);
        }
    }
}
