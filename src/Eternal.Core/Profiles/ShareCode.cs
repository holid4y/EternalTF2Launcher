using System.IO.Compression;
using System.Text;

namespace Eternal.Core.Profiles;

public static class ShareCode
{
    public const string Prefix = "ETERNAL1:";
    private const int MaxChars = 256 * 1024;
    private const int MaxJsonBytes = 1024 * 1024;

    public static string Encode(Profile profile)
    {
        var json = Encoding.UTF8.GetBytes(ProfileStore.ToJson(profile));
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);
        return Prefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ProfileImport Decode(string code)
    {
        var text = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("That isn't an Eternal setup code. Codes start with " + Prefix);
        if (text.Length > MaxChars)
            throw new InvalidDataException("That code is far longer than any setup.");

        var body = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');
        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(body);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("That code is incomplete or was changed along the way.", ex);
        }

        byte[]? json;
        try
        {
            json = Inflate(packed);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("That code is incomplete or was changed along the way.", ex);
        }
        if (json is null)
            throw new InvalidDataException("That code unpacks to far more than any setup.");
        try
        {
            return ProfileStore.FromText(Encoding.UTF8.GetString(json), "Shared setup");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("That code doesn't hold a setup.", ex);
        }
    }

    private static byte[]? Inflate(byte[] packed)
    {
        using var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = input.Read(buffer)) > 0)
        {
            if (output.Length + n > MaxJsonBytes)
                return null;
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
}
