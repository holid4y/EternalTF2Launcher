import hashlib
import io
import json
import os
import shutil
import struct
import sys
import tempfile
import unittest
import zipfile
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

ENGINE_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ENGINE_DIR))

from eternal_engine import bootstrap, build, cli, compat, library, precache  # noqa: E402
from eternal_engine.journal import Journal  # noqa: E402

DATA = Path(tempfile.mkdtemp(prefix="eternal-engine-test-"))
bootstrap.configure(DATA / "data", DATA / "cache")


def run_cli(*args: str) -> dict:
    out = io.StringIO()
    with redirect_stdout(out):
        cli.main(["--json", "--data-dir", str(DATA / "data"), *args])
    lines = [json.loads(line) for line in out.getvalue().splitlines() if line.strip()]
    return next(line for line in lines if line["event"] == "result")


def make_mod(root: Path, name: str, files: dict[str, bytes]) -> Path:
    for rel, content in files.items():
        target = root / name / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
    return root / name


def make_vpk(target: Path, files: dict[str, bytes]) -> Path:
    from valve_parsers import VPKFile

    with tempfile.TemporaryDirectory() as source:
        make_mod(Path(source), "content", files)
        if not VPKFile.create(Path(source) / "content", target):
            raise RuntimeError("could not build test VPK")
    return target


def library_dirs():
    from core.config import config

    return config.addons_dir, config.particles_dir


class NamesTest(unittest.TestCase):
    def test_rejects_traversal(self):
        for bad in ("", ".", "..", "../settings", "a/b", "a\\b", "/abs"):
            with self.assertRaises(library.LibraryError):
                library.check_name(bad)

    def test_accepts_plain_names(self):
        self.assertEqual(library.check_name("Cool Mod 2"), "Cool Mod 2")


class NormalizeTest(unittest.TestCase):
    def test_multipart_sets_collapse_to_dir_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for name in ("mod_dir.vpk", "mod_000.vpk", "mod_001.vpk"):
                (root / name).write_bytes(b"x")
            result = library.normalize_inputs([root / "mod_000.vpk", root / "mod_001.vpk", root / "mod_dir.vpk"])
            self.assertEqual([p.name for p in result], ["mod_dir.vpk"])

    def test_same_name_in_different_folders_is_kept(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "a").mkdir()
            (root / "b").mkdir()
            (root / "a" / "mod.vpk").write_bytes(b"x")
            (root / "b" / "mod.vpk").write_bytes(b"x")
            self.assertEqual(len(library.normalize_inputs([root / "a" / "mod.vpk", root / "b" / "mod.vpk"])), 2)

    def test_single_vpks_ending_in_digits_are_not_merged(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for name in ("pack_100.vpk", "pack_200.vpk"):
                (root / name).write_bytes(b"x")
            result = library.normalize_inputs([root / "pack_100.vpk", root / "pack_200.vpk"])
            self.assertEqual(sorted(p.name for p in result), ["pack_100.vpk", "pack_200.vpk"])

    def test_short_numeric_names_do_not_crash(self):
        self.assertIsNone(library._vpk_base("123"))
        self.assertIsNone(library._vpk_base("12"))
        self.assertEqual(library._vpk_base("pak01_dir"), "pak01")


class ZipTest(unittest.TestCase):
    def test_refuses_path_traversal(self):
        with tempfile.TemporaryDirectory() as tmp:
            archive = Path(tmp) / "evil.zip"
            with zipfile.ZipFile(archive, "w") as z:
                z.writestr("../../escape.txt", "x")
            with self.assertRaises(library.LibraryError):
                library.extract_zip(archive, Path(tmp) / "out")
            self.assertFalse((Path(tmp).parent / "escape.txt").exists())

    def test_imports_mod_at_archive_root(self):
        with tempfile.TemporaryDirectory() as tmp:
            archive = Path(tmp) / "rootmod.zip"
            with zipfile.ZipFile(archive, "w") as z:
                z.writestr("sound/ui/hitsound.wav", "x")
            result = run_cli("import", str(archive))
            self.assertTrue(result["success"], result)
            self.assertEqual(result["data"]["results"][0]["mods"][0]["name"], "rootmod")

    def test_finds_nested_mod_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            unpacked = Path(tmp) / "wrapped"
            make_mod(unpacked / "tf" / "custom", "Nested Mod", {"materials/a.vmt": b"x"})
            result = run_cli("import", "--extracted", str(unpacked))
            self.assertEqual(result["data"]["results"][0]["mods"][0]["name"], "Nested Mod")


class ImportAndConflictTest(unittest.TestCase):
    def test_partial_import_reports_each_item(self):
        with tempfile.TemporaryDirectory() as tmp:
            good = make_mod(Path(tmp), "Good", {"sound/x.wav": b"1"})
            result = run_cli("import", str(good), str(Path(tmp) / "missing.vpk"))
            self.assertTrue(result["success"])
            oks = [r["ok"] for r in result["data"]["results"]]
            self.assertEqual(sorted(oks), [False, True])
            self.assertEqual(len(result["errors"]), 1)

    def test_first_in_order_wins_conflicts(self):
        with tempfile.TemporaryDirectory() as tmp:
            first = make_mod(Path(tmp), "First", {"sound/ui/hit.wav": b"1"})
            second = make_mod(Path(tmp), "Second", {"sound/ui/hit.wav": b"2", "sound/ui/other.wav": b"3"})
            run_cli("import", str(first), str(second))
            result = run_cli("conflicts", "--addon=First", "--addon=Second")
            overlaps = result["data"]["addons"]
            self.assertEqual(overlaps, [{"winner": "First", "loser": "Second", "files": ["sound/ui/hit.wav"]}])

    def test_remove_rejects_unsafe_names(self):
        result = run_cli("remove", "--addon=../config")
        self.assertFalse(result["success"])

    def test_removing_a_particle_mod_keeps_the_addon_with_the_same_name(self):
        addons, particles = library_dirs()
        make_mod(addons, "Twin", {"sound/ui/hit.wav": b"1"})
        make_mod(particles, "Twin", {"particles/x.pcf": b"1"})

        result = run_cli("remove", "--particle=Twin")

        self.assertTrue(result["success"], result["errors"])
        self.assertTrue((addons / "Twin").is_dir())
        self.assertFalse((particles / "Twin").exists())
        shutil.rmtree(addons / "Twin")


class SafeImportTest(unittest.TestCase):
    def test_vpk_paths_that_climb_out_of_the_library_are_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            vpk = make_vpk(Path(tmp) / "climber.vpk", {"xx/yy/escaped.txt": b"x", "materials/a.vmt": b"y"})
            vpk.write_bytes(vpk.read_bytes().replace(b"xx/yy\0", b"../..\0"))

            result = run_cli("import", str(vpk))

        self.assertFalse(result["data"]["results"][0]["ok"])
        self.assertIn("unsafe path", result["data"]["results"][0]["message"])
        self.assertEqual(list(DATA.rglob("escaped.txt")), [])

    def test_failed_reimport_keeps_the_mod_it_would_replace(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            run_cli("import", str(make_mod(Path(tmp) / "v1", "Keeper", {"sound/ui/hit.wav": b"old"})))
            update = make_mod(Path(tmp) / "v2", "Keeper", {"sound/ui/hit.wav": b"new"})
            with mock.patch("core.services.importer.copytree", side_effect=OSError("disk full")):
                result = run_cli("import", str(update))

        self.assertFalse(result["data"]["results"][0]["ok"])
        self.assertEqual((addons / "Keeper" / "sound" / "ui" / "hit.wav").read_bytes(), b"old")
        self.assertEqual([p.name for p in addons.iterdir() if p.name.startswith(".")], [])
        shutil.rmtree(addons / "Keeper")

    def test_successful_reimport_replaces_the_mod(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            run_cli("import", str(make_mod(Path(tmp) / "v1", "Fresh", {"sound/ui/hit.wav": b"old", "sound/ui/gone.wav": b"x"})))
            run_cli("import", str(make_mod(Path(tmp) / "v2", "Fresh", {"sound/ui/hit.wav": b"new"})))

        self.assertEqual((addons / "Fresh" / "sound" / "ui" / "hit.wav").read_bytes(), b"new")
        self.assertFalse((addons / "Fresh" / "sound" / "ui" / "gone.wav").exists())
        self.assertEqual([p.name for p in addons.iterdir() if p.name.startswith(".")], [])
        shutil.rmtree(addons / "Fresh")


    def test_a_particle_mod_imports_before_any_build(self):
        from core.config import config

        _, particles = library_dirs()
        shutil.rmtree(config.temp_to_be_referenced_dir, ignore_errors=True)
        vanilla = ENGINE_DIR / "vendor" / "casual-pre-loader" / "backup" / "particles" / "item_fx.pcf"
        with tempfile.TemporaryDirectory() as tmp:
            mod = Path(tmp) / "Halloween Unusuals"
            (mod / "particles").mkdir(parents=True)
            shutil.copy2(vanilla, mod / "particles" / "item_fx.pcf")

            result = run_cli("import", str(mod))

        self.assertTrue(result["data"]["results"][0]["ok"], result["data"]["results"][0]["message"])
        self.assertTrue((particles / "Halloween Unusuals").is_dir())
        shutil.rmtree(particles / "Halloween Unusuals")


class HudConversionTest(unittest.TestCase):
    MENU = b'"Resource/UI/MainMenuOverride.res"\n{\n}\n'

    def test_hud_vpk_becomes_a_patched_folder(self):
        from core.operations import vgui_preload

        with tempfile.TemporaryDirectory() as tmp:
            custom = Path(tmp) / "custom"
            vpk = make_vpk(custom / "myhud.vpk", {"resource/ui/mainmenuoverride.res": self.MENU,
                                                  "resource/ui/hudlayout.res": b"layout"})
            vgui_preload._process_vpk(vpk)

            self.assertFalse(vpk.exists())
            menu = (custom / "myhud" / "resource" / "ui" / "mainmenuoverride.res").read_text(encoding="utf-8")
            self.assertIn("vguipreload.res", menu)
            self.assertEqual((custom / "myhud" / "resource" / "ui" / "hudlayout.res").read_bytes(), b"layout")

    def test_damaged_hud_vpk_is_left_alone(self):
        from core.operations import vgui_preload

        with tempfile.TemporaryDirectory() as tmp:
            custom = Path(tmp) / "custom"
            vpk = make_vpk(custom / "myhud.vpk", {"resource/ui/mainmenuoverride.res": self.MENU,
                                                  "resource/ui/hudlayout.res": b"HUD-LAYOUT-PAYLOAD"})
            vpk.write_bytes(vpk.read_bytes().replace(b"HUD-LAYOUT-PAYLOAD", b"HUD-LAYOUT-PAYLOAX"))

            vgui_preload._process_vpk(vpk)

            self.assertTrue(vpk.exists())
            self.assertEqual(sorted(p.name for p in custom.iterdir()), ["myhud.vpk"])


class StudioMdlTest(unittest.TestCase):
    def compiler(self):
        from core.quickprecache.studio_mdl import StudioMDL, StudioMDLVersion

        compiler = StudioMDL.__new__(StudioMDL)
        compiler.game_path = Path("/games/$(touch pwned)/Team Fortress 2")
        compiler.bundled_studiomdl_path = Path("/sdk/studiomdl.exe")
        compiler.studio_mdl_version = StudioMDLVersion.STUDIOMDL32
        return compiler

    def run_compile(self, returncode):
        process = mock.MagicMock()
        process.stdout = iter(["compiling\n"])
        process.wait.return_value = returncode
        with mock.patch("subprocess.Popen", return_value=process) as popen:
            ok = self.compiler().make_model(Path("/tmp/a b/`id`.qc"))
        return ok, popen.call_args

    def test_paths_reach_the_compiler_as_arguments(self):
        ok, call = self.run_compile(0)

        self.assertTrue(ok)
        self.assertIsInstance(call.args[0], list)
        self.assertFalse(call.kwargs.get("shell", False))
        qc = str(Path("/tmp/a b/`id`.qc"))
        self.assertIn(qc if sys.platform == "win32" else f"Z:{qc}", call.args[0])

    def test_failed_compiles_become_a_build_warning(self):
        precache.reset()
        ok, _ = self.run_compile(1)

        self.assertFalse(ok)
        self.assertTrue(any("QuickPrecache" in w for w in precache.warnings()))


class MetadataTest(unittest.TestCase):
    def test_metadata_is_written_whole_or_not_at_all(self):
        from core.config import config
        from core.settings import AddonMetadata

        metadata = AddonMetadata()
        metadata["Whole"] = {"files": ["a"]}
        metadata.save()
        before = config.addon_metadata_file.read_text(encoding="utf-8")
        metadata["Cut"] = {"files": ["b"]}
        with mock.patch("json.dump", side_effect=OSError("killed")), self.assertRaises(OSError):
            metadata.save()

        self.assertEqual(config.addon_metadata_file.read_text(encoding="utf-8"), before)

    def test_a_cut_off_metadata_file_is_set_aside(self):
        from core.config import config
        from core.settings import AddonMetadata

        config.addon_metadata_file.parent.mkdir(parents=True, exist_ok=True)
        config.addon_metadata_file.write_text('{"Mod": {"files": [', encoding="utf-8")

        metadata = AddonMetadata()

        self.assertEqual(dict(metadata), {})
        self.assertTrue(config.addon_metadata_file.with_name("addon_metadata.json.bad").is_file())


class JournalTest(unittest.TestCase):
    def test_dead_owner_is_reported_as_interrupted(self):
        with tempfile.TemporaryDirectory() as tmp:
            journal = Journal(Path(tmp) / "journal.json")
            journal.begin("install", Path("/tf"))
            self.assertIsNone(journal.interrupted())  # we are the owner and still alive

            entry = json.loads(journal.path.read_text())
            entry["pid"] = 2 ** 22 + 12345
            journal.path.write_text(json.dumps(entry))
            self.assertEqual(journal.interrupted()["operation"], "install")

            journal.finish("done")
            self.assertIsNone(journal.interrupted())


class CompatTest(unittest.TestCase):
    @staticmethod
    def mdl(numikchains: int, includes: tuple[str, ...] = ()) -> bytes:
        header = bytearray(1024)
        header[0:4] = b"IDST"
        struct.pack_into("<i", header, 4, 48)
        struct.pack_into("<i", header, 284, numikchains)
        struct.pack_into("<i", header, 336, len(includes))
        struct.pack_into("<i", header, 340, 400)
        cursor = 600
        for k, name in enumerate(includes):
            base = 400 + k * 8
            struct.pack_into("<i", header, base + 4, cursor - base)
            encoded = name.encode() + b"\0"
            header[cursor:cursor + len(encoded)] = encoded
            cursor += len(encoded)
        return bytes(header)

    def test_detects_ik_chain_mismatch_case_insensitively(self):
        with tempfile.TemporaryDirectory() as tmp:
            addons = Path(tmp)
            make_mod(addons, "Lucy", {
                "models/player/pyro.mdl": self.mdl(2, ("Models/Player/Lucy/Pyro_Animations.mdl",)),
                "models/player/lucy/pyro_animations.mdl": self.mdl(4),
            })
            issues = compat.scan(addons, ["Lucy"])
            self.assertEqual([i.code for i in issues], ["ik_chain_mismatch"])

    def test_consistent_model_is_clean(self):
        with tempfile.TemporaryDirectory() as tmp:
            addons = Path(tmp)
            make_mod(addons, "Fine", {
                "models/player/pyro.mdl": self.mdl(4, ("models/player/fine/anims.mdl",)),
                "models/player/fine/anims.mdl": self.mdl(4),
            })
            self.assertEqual(compat.scan(addons, ["Fine"]), [])


def mdl(bones: int = 20, textures: int = 4, magic: bytes = b"IDST") -> bytes:
    header = bytearray(512)
    header[:4] = magic
    struct.pack_into("<i", header, 4, 48)
    struct.pack_into("<i", header, 156, bones)
    struct.pack_into("<i", header, 204, textures)
    return bytes(header)


class PrecacheGuardTest(unittest.TestCase):
    def test_only_provably_broken_headers_are_rejected(self):
        self.assertFalse(precache.header_is_broken(mdl()))
        self.assertTrue(precache.header_is_broken(mdl(bones=-7)))
        self.assertTrue(precache.header_is_broken(mdl(textures=1 << 24)))
        self.assertTrue(precache.header_is_broken(mdl(magic=b"MDLZ")))
        self.assertFalse(precache.header_is_broken(b"IDST"))

    def test_broken_models_stay_out_of_quickprecache(self):
        from core.quickprecache import precache_list

        with tempfile.TemporaryDirectory() as tmp:
            game = Path(tmp)
            make_mod(game / "tf" / "custom", "bots", {
                "models/Bots/Soldier/bot_soldier.mdl": mdl(bones=-1),
                "models/bots/pyro/bot_pyro.mdl": mdl(),
                "models/props_farm/barrel.mdl": mdl(),
            })
            precache.install()
            precache.skipped.clear()

            models = precache_list.make_precache_list(game)

        self.assertNotIn("models/bots/soldier/bot_soldier.mdl", models)
        self.assertIn("models/bots/pyro/bot_pyro.mdl", models)
        self.assertIn("models/props_farm/barrel.mdl", models)
        self.assertEqual(precache.skipped, {"models/bots/soldier/bot_soldier.mdl": "bots"})

    def test_broken_models_inside_a_vpk_are_left_out(self):
        from core.quickprecache import precache_list
        from valve_parsers import VPKFile

        with tempfile.TemporaryDirectory() as tmp:
            game = Path(tmp)
            source = make_mod(Path(tmp), "src", {
                "models/bots/heavy/bot_heavy.mdl": mdl(magic=b"\0\0\0\0"),
                "models/props_gameplay/cap_point_base.mdl": mdl(),
            })
            (game / "tf" / "custom").mkdir(parents=True)
            self.assertTrue(VPKFile.create(source, game / "tf" / "custom" / "preload.vpk"))
            precache.install()
            precache.skipped.clear()

            models = precache_list.make_precache_list(game)

        self.assertEqual(models, {"props_gameplay/cap_point_base.mdl"})
        self.assertEqual(precache.skipped, {"models/bots/heavy/bot_heavy.mdl": "preload.vpk"})

    def test_guard_is_installed_once(self):
        from core.quickprecache import precache_list

        precache.install()
        first = precache_list.manage_vpk
        precache.install()
        self.assertIs(precache_list.manage_vpk, first)


class ManifestTest(unittest.TestCase):
    def test_lists_only_what_the_install_wrote(self):
        with tempfile.TemporaryDirectory() as tmp:
            custom = Path(tmp) / "custom"
            make_mod(custom, "my_hud", {"resource/ui/hudlayout.res": b"untouched"})
            before = build._snapshot(custom)
            (custom / "_casual_preloader_dir.vpk").write_bytes(b"vpk")
            target = Path(tmp) / "manifest.json"

            build.write_manifest(target, custom, before, ["Mod A"], [])
            manifest = json.loads(target.read_text(encoding="utf-8"))

        self.assertEqual([f["path"] for f in manifest["files"]], ["custom/_casual_preloader_dir.vpk"])
        self.assertEqual(manifest["files"][0]["sha256"], hashlib.sha256(b"vpk").hexdigest())
        self.assertEqual(manifest["addons"], ["Mod A"])


class StdoutTest(unittest.TestCase):
    def test_output_is_utf8_whatever_the_console_encoding(self):
        # Windows pipes default to the ANSI code page; mod names can be anything.
        import subprocess

        with tempfile.TemporaryDirectory() as tmp:
            mod = make_mod(Path(tmp), "\u30e2\u30c3\u30c9 \u00e9", {"materials/vgui/a.vmt": b"x"})
            env = dict(os.environ, PYTHONIOENCODING="cp1252")
            done = subprocess.run(
                [sys.executable, str(ENGINE_DIR / "run.py"), "--json", "--data-dir", str(Path(tmp) / "data"), "import", str(mod)],
                capture_output=True, env=env, timeout=120,
            )
        lines = [json.loads(line) for line in done.stdout.decode("utf-8").splitlines() if line.strip()]
        result = next(line for line in lines if line["event"] == "result")
        self.assertIn("\u30e2\u30c3\u30c9 \u00e9", json.dumps(result, ensure_ascii=False))

    def test_only_json_reaches_stdout(self):
        out = io.StringIO()
        with redirect_stdout(out):
            cli.main(["--json", "--data-dir", str(DATA / "data"), "version"])
        for line in out.getvalue().splitlines():
            json.loads(line)


if __name__ == "__main__":
    os.environ.setdefault("PYTHONDONTWRITEBYTECODE", "1")
    unittest.main()
