using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Secure credential storage using Windows DPAPI (current-user scope).
/// Keys are encrypted at rest and stored in the user's local app-data directory.
/// </summary>
[SupportedOSPlatform("windows")]
public class SecureKeyStore
{
    private readonly string _storePath;

    public SecureKeyStore(string dataRootPath)
    {
        _storePath = Path.Combine(dataRootPath, ".keys");
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
    }

    /// <summary>
    /// Save a key/secret under the given name, encrypted with DPAPI.
    /// </summary>
    public void SaveKey(string name, string value)
    {
        var store = LoadStore();
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Encoding.UTF8.GetBytes(name),  // entropy = key name
            DataProtectionScope.CurrentUser);
        store[name] = Convert.ToBase64String(encrypted);
        WriteStore(store);
    }

    /// <summary>
    /// Load a previously saved key. Returns null if not found.
    /// </summary>
    public string? LoadKey(string name)
    {
        var store = LoadStore();
        if (!store.TryGetValue(name, out var b64)) return null;
        try
        {
            var encrypted = Convert.FromBase64String(b64);
            var decrypted = ProtectedData.Unprotect(
                encrypted,
                Encoding.UTF8.GetBytes(name),
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null; // Corrupted or different user
        }
    }

    /// <summary>
    /// Delete a stored key.
    /// </summary>
    public void DeleteKey(string name)
    {
        var store = LoadStore();
        if (store.Remove(name))
            WriteStore(store);
    }

    /// <summary>
    /// Check if a key exists in the store.
    /// </summary>
    public bool HasKey(string name) => LoadStore().ContainsKey(name);

    /// <summary>
    /// Resolve the effective API key for a provider, checking direct store 
    /// then environment variable fallback.
    /// </summary>
    public string? ResolveApiKey(string providerName, Configuration.ApiKeySource source, string? envVarName)
    {
        return source switch
        {
            Configuration.ApiKeySource.EnvironmentVariable when !string.IsNullOrEmpty(envVarName)
                => Environment.GetEnvironmentVariable(envVarName),
            _ => LoadKey(providerName)
        };
    }

    private Dictionary<string, string> LoadStore()
    {
        if (!File.Exists(_storePath))
            return new Dictionary<string, string>();
        try
        {
            var json = File.ReadAllText(_storePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private void WriteStore(Dictionary<string, string> store)
    {
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storePath, json);
    }
}
