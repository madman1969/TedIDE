using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Tedide.Core;

/// <summary>
/// Reading and writing every JSON file Tedide keeps: projects, solutions, their sidecar files, and
/// the per-user settings under %LocalAppData%\Tedide. All of them are written indented, with enums
/// as names. Each type is serialized through source-generated code - a <c>JsonSerializerContext</c>
/// in its own project, with <c>[JsonSourceGenerationOptions(WriteIndented = true,
/// UseStringEnumConverter = true)]</c> - rather than reflection, so the apps can be trimmed.
/// </summary>
public static class JsonFile
{
    /// <summary>The per-user settings folder, %LocalAppData%\Tedide.</summary>
    public static string UserSettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide");

    /// <summary>A file in <see cref="UserSettingsDirectory"/>.</summary>
    public static string UserSettingsPath(string fileName) => Path.Combine(UserSettingsDirectory, fileName);

    /// <summary>Reads <paramref name="path"/>; a file holding just "null" reads as a new
    /// <typeparamref name="T"/>. Throws if it's missing or isn't valid JSON for the type.</summary>
    public static T Read<T>(string path, JsonTypeInfo<T> type) where T : new() =>
        JsonSerializer.Deserialize(File.ReadAllText(path), type) ?? new T();

    /// <summary>
    /// Reads a settings file, or a new <typeparamref name="T"/> - its defaults - if there isn't one
    /// yet or it can't be read. A missing or corrupt settings file never stops the app starting.
    /// </summary>
    public static T ReadOrDefault<T>(string path, JsonTypeInfo<T> type) where T : new()
    {
        try
        {
            return Read(path, type);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new T();
        }
    }

    /// <summary><paramref name="value"/> as it's written to a file.</summary>
    public static string Serialize<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.Serialize(value, type);

    /// <summary>Writes <paramref name="value"/> to <paramref name="path"/>, creating its folder if needed.</summary>
    public static void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, Serialize(value, type));
    }
}
