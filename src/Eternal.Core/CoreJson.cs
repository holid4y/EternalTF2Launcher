using System.Text.Json.Serialization;
using Eternal.Core.Settings;

namespace Eternal.Core;

[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GameConfig))]
[JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class CoreJson : JsonSerializerContext;
