using System.Text.Json.Serialization;

namespace Eternal.Core.Profiles;

[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Profile))]
internal sealed partial class ProfileJson : JsonSerializerContext;
