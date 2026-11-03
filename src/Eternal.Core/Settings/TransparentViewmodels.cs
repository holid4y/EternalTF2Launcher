using System.Buffers.Binary;
using System.Text;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;

namespace Eternal.Core.Settings;

public enum ViewmodelStatus { Off, Applied, NoGameFolder, NoLayout, Failed }

public sealed record ViewmodelResult(ViewmodelStatus Status, string? From = null, string? Error = null);

/// <summary>
/// See-through weapons, the way HUDs that support it do it: a full-screen
/// panel in hudlayout.res shows a Refract material over the view model, and
/// that only works with motion blur on (at zero strength), HDR off and MSAA off.
///
/// Rather than editing the player's HUD, the launcher writes its own folder
/// that sorts first in tf/custom. Its hudlayout.res #bases a copy of whichever
/// layout was in effect (a custom HUD's or the game's own) and adds the panel,
/// so it works with any HUD and turning it off just removes the folder. Its
/// info.vdf matters: without one the game ignores a folder's HUD files.
/// </summary>
public static class TransparentViewmodels
{
    public const string FolderName = "!eternal-transparent-viewmodels";
    private const string BaseLayout = "eternal_hudlayout_base.res";
    private const string Material = "eternal_viewmodel_refract";
    private const int CurrentUiVersion = 3;

    /// <summary>Written at the very end of the managed block so nothing before it can undo them.</summary>
    public static readonly string[] Cvars =
    [
        "mat_motion_blur_enabled 1",
        "mat_motion_blur_strength 0",
        "mat_hdr_level 0",
        "mat_antialias 1",
        "mat_alphacoverage 0",
    ];

    private const string Layout = $$"""
        #base "{{BaseLayout}}"

        "Resource/HudLayout"
        {
        	"TransparentViewmodel"
        	{
        		"ControlName"	"ImagePanel"
        		"fieldName"		"TransparentViewmodel"
        		"xpos"			"0"
        		"ypos"			"0"
        		"zpos"			"-100"
        		"wide"			"f0"
        		"tall"			"480"
        		"visible"		"1"
        		"enabled"		"1"
        		"image"			"replay/thumbnails/{{Material}}"
        		"scaleImage"	"1"
        	}
        }

        """;

    private const string RefractMaterial = $$"""
        "Refract"
        {
        	"%keywords"		"tf"
        	"$refractamount"	"0"
        	"$refracttint"	"{255 255 255}"
        	"$refractblur"	"0"
        	"$scale"		"[1 1]"
        	"$normalmap"	"vgui/replay/thumbnails/{{Material}}_normal"
        }

        """;

    private const string Readme = """
        Eternal TF2 Launcher made this folder for Config > HUD & Menus > See-through weapons.
        Turning that option off removes it. Don't edit it: the launcher rewrites it when you press Play.

        """;

    public static ViewmodelResult Sync(string? tfPath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(tfPath) || !File.Exists(Path.Combine(tfPath, "gameinfo.txt")))
            return new ViewmodelResult(ViewmodelStatus.NoGameFolder);
        var folder = Path.Combine(tfPath, "custom", FolderName);
        try
        {
            if (!enabled)
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
                return new ViewmodelResult(ViewmodelStatus.Off);
            }

            if (EffectiveLayout(tfPath) is not { } effective)
                return new ViewmodelResult(ViewmodelStatus.NoLayout, Error: "TF2's own HUD layout couldn't be found in tf2_misc_dir.vpk.");

            Write(Path.Combine(folder, "scripts", BaseLayout), effective.Bytes);
            Write(Path.Combine(folder, "scripts", "hudlayout.res"), Encoding.UTF8.GetBytes(Layout));
            var materials = Path.Combine(folder, "materials", "vgui", "replay", "thumbnails");
            Write(Path.Combine(materials, Material + ".vmt"), Encoding.UTF8.GetBytes(RefractMaterial));
            Write(Path.Combine(materials, Material + "_normal.vtf"), FlatNormalMap());
            Write(Path.Combine(folder, "info.vdf"), Encoding.UTF8.GetBytes(Info(UiVersion(tfPath, effective.From))));
            Write(Path.Combine(folder, "readme.txt"), Encoding.UTF8.GetBytes(Readme));
            return new ViewmodelResult(ViewmodelStatus.Applied, effective.From);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"Transparent viewmodels not updated: {ex.Message}");
            return new ViewmodelResult(ViewmodelStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// The hudlayout.res the game would load without our folder: tf/custom in
    /// name order (folders and VPKs alike, the first one that has the file wins),
    /// then the game's own tf2_misc VPK.
    /// </summary>
    internal static (string From, byte[] Bytes)? EffectiveLayout(string tfPath)
    {
        var custom = Path.Combine(tfPath, "custom");
        if (Directory.Exists(custom))
        {
            var entries = Directory.EnumerateFileSystemEntries(custom)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name != FolderName)
                .Order(StringComparer.OrdinalIgnoreCase);
            foreach (var name in entries)
            {
                var full = Path.Combine(custom, name);
                if (Directory.Exists(full))
                {
                    var file = Path.Combine(full, "scripts", "hudlayout.res");
                    if (File.Exists(file))
                        return (name, File.ReadAllBytes(file));
                }
                else if (IsMountedVpk(name) && TryVpk(full) is { } bytes)
                {
                    return (name, bytes);
                }
            }
        }
        var misc = Path.Combine(tfPath, "tf2_misc_dir.vpk");
        return File.Exists(misc) && TryVpk(misc) is { } own ? ("the game's own HUD", own) : null;
    }

    // TF2 drops HUD files from a custom folder unless its info.vdf carries the HUD version the game expects.
    private static string Info(int uiVersion) => $"\"Eternal see-through weapons\"\n{{\n\t\"ui_version\"\t\"{uiVersion}\"\n}}\n";

    /// <summary>The version the HUD in effect declares, which the game has accepted; 3 when there's no custom HUD.</summary>
    private static int UiVersion(string tfPath, string from)
    {
        var info = Path.Combine(tfPath, "custom", from, "info.vdf");
        if (!File.Exists(info))
            return CurrentUiVersion;
        try
        {
            var root = Steam.Vdf.Parse(File.ReadAllText(info));
            var version = root.Children.Select(c => (c.Value as Steam.VdfNode)?.Value("ui_version")).FirstOrDefault(v => v is not null);
            return int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is > 0 and < 100 ? n : CurrentUiVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CurrentUiVersion;
        }
    }

    // Parts of a split VPK (name_000.vpk) are data, not something the game mounts on its own.
    private static bool IsMountedVpk(string name) =>
        name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
        && !(name.Length > 8 && name[^8] == '_' && name[^7..^4].All(char.IsAsciiDigit));

    private static byte[]? TryVpk(string path)
    {
        try
        {
            return Vpk.Read(path, "scripts/hudlayout.res");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Debug($"Skipping {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, bytes, keepBackup: false);
    }

    /// <summary>
    /// A 4×4 normal map pointing straight out. Its alpha (100 of 255) is how much
    /// of the scene behind the weapon the Refract material lets through; HUDs that
    /// ship this effect use the same values. VTF 7.2, ABGR8888, no mipmaps or thumbnail.
    /// </summary>
    internal static byte[] FlatNormalMap()
    {
        const int width = 4, height = 4, headerSize = 80;
        var vtf = new byte[headerSize + width * height * 4];
        var h = vtf.AsSpan();
        "VTF\0"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 7);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[16..], width);
        BinaryPrimitives.WriteUInt16LittleEndian(h[18..], height);
        // No mipmaps, no level of detail, eight-bit alpha.
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 0x0100 | 0x0200 | 0x2000);
        BinaryPrimitives.WriteUInt16LittleEndian(h[24..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], 0);
        BinaryPrimitives.WriteSingleLittleEndian(h[32..], 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(h[36..], 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(h[40..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(h[48..], 1f);
        BinaryPrimitives.WriteInt32LittleEndian(h[52..], 1);
        h[56] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(h[57..], -1);
        h[61] = 0;
        h[62] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(h[63..], 1);
        for (var i = headerSize; i < vtf.Length; i += 4)
        {
            vtf[i] = 100;
            vtf[i + 1] = 248;
            vtf[i + 2] = 127;
            vtf[i + 3] = 125;
        }
        return vtf;
    }
}
