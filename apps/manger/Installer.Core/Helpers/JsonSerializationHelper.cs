using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Installer.Core.Helpers;

/// <summary>
/// JSON serialization utilities with consistent configuration
/// </summary>
public static class JsonSerializationHelper
{
    private static readonly JsonSerializerOptions _snakeCaseOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions _camelCaseOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = null, // Keep as-is or use default camelCase
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Serialize to JSON with snake_case property naming
    /// </summary>
    public static string ToJsonSnakeCase(object? obj) =>
        JsonSerializer.Serialize(obj, _snakeCaseOptions);

    /// <summary>
    /// Deserialize from JSON assuming snake_case properties
    /// </summary>
    public static T? FromJsonSnakeCase<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, _snakeCaseOptions);

    /// <summary>
    /// Serialize to JSON with standard casing
    /// </summary>
    public static string ToJsonStandard(object obj) =>
        JsonSerializer.Serialize(obj, _camelCaseOptions);

    /// <summary>
    /// Deserialize from JSON assuming standard properties
    /// </summary>
    public static T? FromJsonStandard<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, _camelCaseOptions);

    /// <summary>
    /// Clone object via deep JSON serialization/deserialization
    /// </summary>
    public static T Clone<T>(T obj) where T : class
    {
        if (obj is null)
            return default!;

        var json = ToJsonSnakeCase(obj);
        return FromJsonSnakeCase<T>(json)!;
    }
    /// <summary>
    /// Load JSON file and deserialize
    /// </summary>
    public static T? LoadFromFile<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath))
            return null;

        var content = File.ReadAllText(filePath);
        return FromJsonSnakeCase<T>(content);
    }

    /// <summary>
    /// Save object to JSON file
    /// </summary>
    public static void SaveToFile<T>(string filePath, T obj)
    {
        var json = ToJsonSnakeCase(obj);
        
        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        
        File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Read and parse JSON bytes
    /// </summary>
    public static async Task<string> ReadJsonAsStringAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Parse JSON response stream
    /// </summary>
    public static async Task<T?> ParseJsonStreamAsync<T>(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        return FromJsonSnakeCase<T>(content);
    }
}
