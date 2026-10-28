using System.Text.Json.Serialization;

namespace Eternal.Core.Profiles;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Profile))]
internal sealed partial class ProfileJson : JsonSerializerContext;
