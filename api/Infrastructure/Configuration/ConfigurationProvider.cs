namespace RusticalandOPS.Api.Infrastructure.Configuration;

using System.Text.Json;
using System.Text.Json.Serialization;

public class ConfigurationProvider : IConfigurationProvider
{
    public string? GetEnvironmentVariable(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        return Environment.GetEnvironmentVariable(key);
    }

    public string GetEnvironmentVariable(string key, string defaultValue)
    {
        return GetEnvironmentVariable(key) ?? defaultValue;
    }

    public string? GetFirstNonEmptyEnvironmentVariable(params string[] keys)
    {
        if (keys == null || keys.Length == 0)
            return null;

        foreach (var key in keys)
        {
            var value = GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public Dictionary<string, string> ReadEnvFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return values;

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        catch
        {
            // Return empty dict on error
        }

        return values;
    }

    public void WriteEnvFile(string path, Dictionary<string, string> updates)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            var separator = rawLine.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = rawLine[..separator].Trim();
            if (!updates.TryGetValue(key, out var newValue))
                continue;

            lines[i] = $"{key}={newValue}";
            touched.Add(key);
        }

        foreach (var update in updates.Where(update => !touched.Contains(update.Key)))
            lines.Add($"{update.Key}={update.Value}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    public T? DeserializeJsonFile<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    public void SerializeJsonFile<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Silently fail
        }
    }
}
