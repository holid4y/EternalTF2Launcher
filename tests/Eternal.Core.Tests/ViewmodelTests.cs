using System.Buffers.Binary;
using System.Text;
using Eternal.Core.Settings;

namespace Eternal.Core.Tests;

public sealed class ViewmodelTests : IDisposable
{
    private readonly string _tf = Directory.CreateTempSubdirectory("eternal-tf-").FullName;

    public ViewmodelTests() => File.WriteAllText(Path.Combine(_tf, "gameinfo.txt"), "\"GameInfo\" {}");

    public void Dispose() => Directory.Delete(_tf, recursive: true);

    [Fact]
    public void Vpk_files_are_read_from_the_directory_and_from_numbered_archives()
    {
        var dir = Path.Combine(_tf, "pack_dir.vpk");
        WriteVpk(dir, ("scripts/hudlayout.res", "\"Resource/HudLayout\" {}"u8.ToArray(), InArchive: false),
                      ("materials/a.vmt", "LightmappedGeneric {}"u8.ToArray(), InArchive: true));

        Assert.Equal("\"Resource/HudLayout\" {}", Encoding.UTF8.GetString(Vpk.Read(dir, "scripts/hudlayout.res")!));
        Assert.Equal("LightmappedGeneric {}", Encoding.UTF8.GetString(Vpk.Read(dir, "materials/a.vmt")!));
        Assert.Null(Vpk.Read(dir, "scripts/missing.res"));
    }

    [Fact]
    public void A_custom_hud_wins_over_the_game_layout_in_name_order()
    {
        WriteVpk(Path.Combine(_tf, "tf2_misc_dir.vpk"), ("scripts/hudlayout.res", "game"u8.ToArray(), false));
        Assert.Equal("the game's own HUD", TransparentViewmodels.EffectiveLayout(_tf)!.Value.From);

        var hud = Path.Combine(_tf, "custom", "zeeshud", "scripts");
        Directory.CreateDirectory(hud);
        File.WriteAllText(Path.Combine(hud, "hudlayout.res"), "zees");
        WriteVpk(Path.Combine(_tf, "custom", "budhud.vpk"), ("scripts/hudlayout.res", "bud"u8.ToArray(), false));

        var effective = TransparentViewmodels.EffectiveLayout(_tf)!.Value;
        Assert.Equal("budhud.vpk", effective.From);
        Assert.Equal("bud", Encoding.UTF8.GetString(effective.Bytes));
    }

    [Fact]
    public void Turning_it_on_adds_the_launcher_folder_and_off_removes_it()
    {
        var hud = Path.Combine(_tf, "custom", "rayshud", "scripts");
        Directory.CreateDirectory(hud);
        File.WriteAllText(Path.Combine(hud, "hudlayout.res"), "\"Resource/HudLayout\" { \"HudHealth\" {} }");
        File.WriteAllText(Path.Combine(_tf, "custom", "rayshud", "info.vdf"), "\"rayshud\" { \"ui_version\" \"3\" }");

        var result = TransparentViewmodels.Sync(_tf, enabled: true);
        Assert.Equal(ViewmodelStatus.Applied, result.Status);
        Assert.Equal("rayshud", result.From);
        var folder = Path.Combine(_tf, "custom", TransparentViewmodels.FolderName);
        var layout = File.ReadAllText(Path.Combine(folder, "scripts", "hudlayout.res"));
        Assert.StartsWith("#base \"eternal_hudlayout_base.res\"", layout);
        Assert.Contains("\"TransparentViewmodel\"", layout);
        Assert.Contains("HudHealth", File.ReadAllText(Path.Combine(folder, "scripts", "eternal_hudlayout_base.res")));
        Assert.True(File.Exists(Path.Combine(folder, "materials", "vgui", "replay", "thumbnails", "eternal_viewmodel_refract_normal.vtf")));
        Assert.Equal("3", Eternal.Core.Steam.Vdf.Parse(File.ReadAllText(Path.Combine(folder, "info.vdf"))).Section("Eternal see-through weapons")?.Value("ui_version"));
        Assert.Equal("rayshud", TransparentViewmodels.Sync(_tf, enabled: true).From);

        Assert.Equal(ViewmodelStatus.Off, TransparentViewmodels.Sync(_tf, enabled: false).Status);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(hud, "hudlayout.res")));
    }

    [Fact]
    public void The_normal_map_is_a_valid_vtf_header()
    {
        var vtf = TransparentViewmodels.FlatNormalMap();
        Assert.Equal("VTF\0"u8.ToArray(), vtf[..4]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(vtf.AsSpan(4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(vtf.AsSpan(8)));
        Assert.Equal(80, BinaryPrimitives.ReadInt32LittleEndian(vtf.AsSpan(12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(vtf.AsSpan(52)));
        Assert.Equal(0x2300u, BinaryPrimitives.ReadUInt32LittleEndian(vtf.AsSpan(20)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(vtf.AsSpan(57)));
        Assert.Equal(80 + 4 * 4 * 4, vtf.Length);
        Assert.Equal(new byte[] { 100, 248, 127, 125 }, vtf[80..84]);
    }

    [Fact]
    public void Its_cvars_come_last_so_a_motion_blur_setting_cannot_undo_them()
    {
        var settings = new GameSettings(new GameConfig());
        settings.Set(SettingsCatalog.ById["MotionBlur"], System.Text.Json.Nodes.JsonValue.Create(false));
        settings.Set(SettingsCatalog.ById[SettingsCatalog.TransparentViewmodelsId], System.Text.Json.Nodes.JsonValue.Create(true));
        var lines = ManagedBlock.Build(settings);

        Assert.True(lines.IndexOf("mat_motion_blur_enabled 1") > lines.IndexOf("mat_motion_blur_enabled 0"));
        Assert.Equal(TransparentViewmodels.Cvars, lines[^TransparentViewmodels.Cvars.Length..]);
    }

    private static void WriteVpk(string path, params (string Path, byte[] Data, bool InArchive)[] files)
    {
        var tree = new MemoryStream();
        var inline = new MemoryStream();
        var archive = new MemoryStream();
        void Str(string s) => tree.Write([.. Encoding.UTF8.GetBytes(s), 0]);
        foreach (var group in files.GroupBy(f => Path.GetExtension(f.Path)[1..]))
        {
            Str(group.Key);
            foreach (var folder in group.GroupBy(f => Path.GetDirectoryName(f.Path)!.Replace('\\', '/')))
            {
                Str(folder.Key);
                foreach (var file in folder)
                {
                    Str(Path.GetFileNameWithoutExtension(file.Path));
                    var target = file.InArchive ? archive : inline;
                    var entry = new byte[18];
                    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), (ushort)(file.InArchive ? 0 : 0x7FFF));
                    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)target.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), (uint)file.Data.Length);
                    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(16), 0xFFFF);
                    tree.Write(entry);
                    target.Write(file.Data);
                }
                Str("");
            }
            Str("");
        }
        Str("");

        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x55AA1234);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), (int)tree.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. header, .. tree.ToArray(), .. inline.ToArray()]);
        if (archive.Length > 0)
            File.WriteAllBytes(path.Replace("_dir.vpk", "_000.vpk"), archive.ToArray());
    }
}
