using System.Text.Json.Serialization;

namespace Tedide.App;

/// <summary>Source-generated JSON for the IDE's per-user settings - see <see cref="Tedide.Core.JsonFile"/>. Indented, with enums as
/// names, like every file Tedide writes.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EditorSettings))]
[JsonSerializable(typeof(LayoutSettings))]
[JsonSerializable(typeof(RecentProjectsSettings))]
[JsonSerializable(typeof(ToolchainSettings))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
