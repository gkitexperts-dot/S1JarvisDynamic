using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace S1Jarvis.Access.Verilic
{
    internal enum VerilicInstallationProtectionScope
    {
        CurrentUser = 0,
        LocalMachine = 1
    }

    /// <summary>
    /// Local installation state for one Verilic product. Key material and
    /// activation retry state are protected together with DPAPI before disk.
    /// </summary>
    internal sealed class VerilicInstallationState
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("productCode")]
        public string ProductCode { get; set; }

        [JsonProperty("installationId")]
        public string InstallationId { get; set; }

        [JsonProperty("keyAlgorithm")]
        public string KeyAlgorithm { get; set; }

        [JsonProperty("privateKeyMaterial")]
        public byte[] PrivateKeyMaterial { get; set; }

        [JsonProperty("activationCompleted")]
        public bool ActivationCompleted { get; set; }

        [JsonProperty("activationIdempotencyKey")]
        public string ActivationIdempotencyKey { get; set; }
    }

    /// <summary>
    /// .NET Framework 4.8 compatible DPAPI store for Verilic installation
    /// identity and private proof material. The caller explicitly chooses the
    /// DPAPI scope and storage directory.
    ///
    /// Missing state may create a provisional local installation id. Corrupt or
    /// undecryptable state never auto-regenerates. After successful activation,
    /// the provisional id must be replaced by the authoritative server id.
    /// </summary>
    internal sealed class VerilicInstallationStateStore
    {
        private const string EntropyPurpose =
            "S1Jarvis.Verilic.InstallationState.v1";

        private readonly string _baseDirectory;
        private readonly DataProtectionScope _scope;

        public VerilicInstallationStateStore(
            string baseDirectory,
            VerilicInstallationProtectionScope protectionScope)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new ArgumentException(
                    "A licensing state directory is required.",
                    nameof(baseDirectory));

            _baseDirectory = Path.GetFullPath(baseDirectory);
            _scope = protectionScope == VerilicInstallationProtectionScope.LocalMachine
                ? DataProtectionScope.LocalMachine
                : DataProtectionScope.CurrentUser;
        }

        public VerilicInstallationState Load(string productCode)
        {
            string normalizedProduct = RequireProductCode(productCode);
            string path = GetStatePath(normalizedProduct);
            if (!File.Exists(path))
                return null;

            try
            {
                byte[] protectedBytes = File.ReadAllBytes(path);
                byte[] clearBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    CreateEntropy(normalizedProduct),
                    _scope);

                try
                {
                    string json = Encoding.UTF8.GetString(clearBytes);
                    VerilicInstallationState state =
                        JsonConvert.DeserializeObject<VerilicInstallationState>(json);

                    ValidateLoadedState(state, normalizedProduct);
                    return state;
                }
                finally
                {
                    Array.Clear(clearBytes, 0, clearBytes.Length);
                }
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "Verilic installation state cannot be decrypted.", ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "Verilic installation state is invalid.", ex);
            }
        }

        public VerilicInstallationState GetOrCreateIdentity(string productCode)
        {
            string normalizedProduct = RequireProductCode(productCode);
            VerilicInstallationState existing = Load(normalizedProduct);
            if (existing != null)
                return existing;

            var created = new VerilicInstallationState
            {
                Version = 1,
                ProductCode = normalizedProduct,
                InstallationId = CreateInstallationId(),
                KeyAlgorithm = null,
                PrivateKeyMaterial = null,
                ActivationCompleted = false,
                ActivationIdempotencyKey = null
            };

            Save(created);
            return created;
        }

        public void Save(VerilicInstallationState state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            string normalizedProduct = RequireProductCode(state.ProductCode);
            if (state.Version != 1)
                throw new InvalidDataException(
                    "Unsupported Verilic installation state version.");
            RequireIdentifier(state.InstallationId, "installationId");

            if (!string.IsNullOrEmpty(state.ActivationIdempotencyKey))
                RequireIdentifier(state.ActivationIdempotencyKey, "activationIdempotencyKey");

            if (state.ActivationCompleted &&
                (!string.Equals(state.KeyAlgorithm, "ES256", StringComparison.Ordinal) ||
                 state.PrivateKeyMaterial == null ||
                 state.PrivateKeyMaterial.Length == 0))
                throw new InvalidDataException(
                    "Completed Verilic activation requires an ES256 installation key.");

            Directory.CreateDirectory(_baseDirectory);
            string path = GetStatePath(normalizedProduct);
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");

            byte[] clearBytes = null;
            byte[] protectedBytes = null;
            try
            {
                string json = JsonConvert.SerializeObject(state);
                clearBytes = Encoding.UTF8.GetBytes(json);
                protectedBytes = ProtectedData.Protect(
                    clearBytes,
                    CreateEntropy(normalizedProduct),
                    _scope);

                File.WriteAllBytes(temporaryPath, protectedBytes);

                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                if (clearBytes != null)
                    Array.Clear(clearBytes, 0, clearBytes.Length);
                if (protectedBytes != null)
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        private string GetStatePath(string productCode)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(productCode));

            string fileName = "installation-" + ToHex(hash) + ".bin";
            return Path.Combine(_baseDirectory, fileName);
        }

        private static byte[] CreateEntropy(string productCode)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(Encoding.UTF8.GetBytes(
                    EntropyPurpose + "\n" + productCode));
            }
        }

        private static string CreateInstallationId()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            return "pending_" + ToHex(bytes);
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
                builder.Append(bytes[index].ToString("x2"));
            return builder.ToString();
        }

        private static string RequireProductCode(string value)
        {
            string normalized = RequireIdentifier(value, "productCode");
            if (!string.Equals(normalized, JarvisProducts.Jarvis, StringComparison.Ordinal) &&
                !string.Equals(normalized, JarvisProducts.JarvisCourier, StringComparison.Ordinal) &&
                !string.Equals(normalized, JarvisProducts.JarvisDocReader, StringComparison.Ordinal))
                throw new ArgumentException(
                    "Unknown Jarvis Verilic product code.", nameof(value));

            return normalized;
        }

        private static string RequireIdentifier(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
                throw new ArgumentException(
                    "A valid licensing identifier is required.", name);

            string normalized = value.Trim();
            for (int index = 0; index < normalized.Length; index++)
            {
                char character = normalized[index];
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                    throw new ArgumentException(
                        "Licensing identifiers cannot contain whitespace or control characters.",
                        name);
            }

            return normalized;
        }

        private static void ValidateLoadedState(
            VerilicInstallationState state,
            string expectedProductCode)
        {
            if (state == null || state.Version != 1)
                throw new InvalidDataException(
                    "Unsupported or missing Verilic installation state.");

            if (!string.Equals(
                    state.ProductCode,
                    expectedProductCode,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Verilic installation state product binding mismatch.");

            RequireIdentifier(state.InstallationId, "installationId");

            if (!string.IsNullOrEmpty(state.ActivationIdempotencyKey))
                RequireIdentifier(state.ActivationIdempotencyKey, "activationIdempotencyKey");

            if (state.ActivationCompleted &&
                (!string.Equals(state.KeyAlgorithm, "ES256", StringComparison.Ordinal) ||
                 state.PrivateKeyMaterial == null ||
                 state.PrivateKeyMaterial.Length == 0))
                throw new InvalidDataException(
                    "Completed Verilic activation state is missing its ES256 key.");
        }
    }
}
