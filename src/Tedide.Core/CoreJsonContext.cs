using System.Text.Json.Serialization;

namespace Tedide.Core;

/// <summary>Source-generated JSON for projects, solutions and their sidecar files - see <see cref="JsonFile"/>. Indented, with enums as
/// names, like every file Tedide writes.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TedideProject))]
[JsonSerializable(typeof(TedideSolution))]
[JsonSerializable(typeof(BreakpointsFile))]
[JsonSerializable(typeof(SessionStateFile))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
