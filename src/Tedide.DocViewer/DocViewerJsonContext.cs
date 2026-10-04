using System.Text.Json.Serialization;

namespace Tedide.DocViewer;

/// <summary>Source-generated JSON for the Doc Viewer's per-user settings - see <see cref="Tedide.Core.JsonFile"/>. Indented, with enums as
/// names, like every file Tedide writes.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DocBookmarks))]
[JsonSerializable(typeof(DocViewerLayoutSettings))]
internal sealed partial class DocViewerJsonContext : JsonSerializerContext;
