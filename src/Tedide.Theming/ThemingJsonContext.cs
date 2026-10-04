using System.Text.Json.Serialization;

namespace Tedide.Theming;

/// <summary>Source-generated JSON for the theme setting - see <see cref="Tedide.Core.JsonFile"/>. Indented, with enums as
/// names, like every file Tedide writes.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ThemeSettings))]
internal sealed partial class ThemingJsonContext : JsonSerializerContext;
