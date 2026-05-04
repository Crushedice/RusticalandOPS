namespace RusticalandOPS.Api.Infrastructure.Configuration;

public interface IConfigurationProvider
{
    string? GetEnvironmentVariable(string key);
    string GetEnvironmentVariable(string key, string defaultValue);
    string? GetFirstNonEmptyEnvironmentVariable(params string[] keys);
    Dictionary<string, string> ReadEnvFile(string path);
    void WriteEnvFile(string path, Dictionary<string, string> values);
    T? DeserializeJsonFile<T>(string path) where T : class;
    void SerializeJsonFile<T>(string path, T value);
}
