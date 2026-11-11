import hashlib
import io
import json
import os
import shutil
import struct
import subprocess
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

    def test_a_mod_named_like_a_set_aside_copy_cannot_take_over_another_mod(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            run_cli("import", str(make_mod(Path(tmp) / "v1", "Victim", {"sound/ui/hit.wav": b"mine"})))
            impostor = make_mod(Path(tmp) / "v2", f"{library.SET_ASIDE}Victim", {"sound/ui/hit.wav": b"theirs"})
            result = run_cli("import", str(impostor))
            run_cli("library")

        self.assertFalse(result["data"]["results"][0]["ok"])
        self.assertEqual((addons / "Victim" / "sound" / "ui" / "hit.wav").read_bytes(), b"mine")
        self.assertEqual([p.name for p in addons.iterdir() if p.name.startswith(".")], [])
        shutil.rmtree(addons / "Victim")

    def test_a_cut_off_vpk_does_not_replace_the_mod_it_updates(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            vpk = make_vpk(Path(tmp) / "Sturdy.vpk", {"materials/good.vmt": b"a" * 2000, "materials/bad.vmt": b"b" * 2000})
            self.assertTrue(run_cli("import", str(vpk))["data"]["results"][0]["ok"])
            vpk.write_bytes(vpk.read_bytes()[:-1000])

            result = run_cli("import", str(vpk))["data"]["results"][0]

        self.assertFalse(result["ok"])
        self.assertIn("damaged", result["message"])
        for name in ("good.vmt", "bad.vmt"):
            self.assertEqual((addons / "Sturdy" / "materials" / name).stat().st_size, 2000)
        shutil.rmtree(addons / "Sturdy")

    def test_standalone_numbered_vpks_are_separate_mods(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            first = make_vpk(Path(tmp) / "pack_100.vpk", {"materials/pack_100.vmt": b"x"})
            second = make_vpk(Path(tmp) / "pack_200.vpk", {"materials/pack_200.vmt": b"y"})

            results = run_cli("import", str(first), str(second))["data"]["results"]

        self.assertEqual([r["mods"][0]["name"] for r in results], ["pack_100", "pack_200"])
        self.assertTrue((addons / "pack_100" / "materials" / "pack_100.vmt").is_file())
        self.assertTrue((addons / "pack_200" / "materials" / "pack_200.vmt").is_file())
        self.assertFalse((addons / "pack").exists())
        shutil.rmtree(addons / "pack_100")
        shutil.rmtree(addons / "pack_200")

    def test_a_multipart_set_is_one_mod_named_after_the_set(self):
        from valve_parsers import VPKFile

        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            source = make_mod(Path(tmp), "src", {"materials/skin.vmt": b"x" * 64, "materials/skin2.vmt": b"y" * 64})
            self.assertTrue(VPKFile.create(source, Path(tmp) / "skins", split_size=64))
            parts = sorted(Path(tmp).glob("skins_*.vpk"))

            result = run_cli("import", *map(str, parts))["data"]["results"]

        self.assertEqual([r["mods"][0]["name"] for r in result], ["skins"])
        self.assertEqual((addons / "skins" / "materials" / "skin2.vmt").read_bytes(), b"y" * 64)
        shutil.rmtree(addons / "skins")

    def test_a_folder_holding_a_vpk_imports_what_is_inside(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / "femmepyro_v1"
            folder.mkdir()
            make_vpk(folder / "FemPyro.vpk", {"models/player/pyro.mdl": b"pyro"})

            result = run_cli("import", str(folder))["data"]["results"][0]

        self.assertTrue(result["ok"], result["message"])
        self.assertEqual([m["name"] for m in result["mods"]], ["FemPyro"])
        self.assertEqual((addons / "FemPyro" / "models" / "player" / "pyro.mdl").read_bytes(), b"pyro")
        self.assertEqual(list(addons.rglob("*.vpk")), [])
        shutil.rmtree(addons / "FemPyro")

    def test_a_library_mod_still_holding_a_vpk_is_unpacked_under_its_name(self):
        addons, _ = library_dirs()
        old = addons / "Old Pyro"
        old.mkdir()
        (old / "mod.json").write_text('{"addon_name": "Old Pyro", "type": "Vpk_Container"}', encoding="utf-8")
        with tempfile.TemporaryDirectory() as tmp:
            shutil.move(make_vpk(Path(tmp) / "pyro.vpk", {"models/player/pyro.mdl": b"pyro"}), old / "pyro.vpk")

        problems = library.unpack_vpk_folders(["Old Pyro"], lambda *_: None)

        self.assertEqual(problems, [])
        self.assertEqual((old / "models" / "player" / "pyro.mdl").read_bytes(), b"pyro")
        self.assertEqual(list(old.glob("*.vpk")), [])
        shutil.rmtree(old)

    def test_unpacking_keeps_the_files_beside_the_vpk(self):
        addons, _ = library_dirs()
        mixed = make_mod(addons, "Mixed Pyro", {
            "materials/models/player/pyro/pyro_red.vmt": b"loose",
            "models/player/pyro.vvd": b"loose vvd",
            "mod.json": b'{"type": "model"}',
        })
        with tempfile.TemporaryDirectory() as tmp:
            vpk = make_vpk(Path(tmp) / "pyro.vpk", {"models/player/pyro.mdl": b"pyro", "models/player/pyro.vvd": b"vpk vvd"})
            shutil.move(vpk, mixed / "pyro.vpk")

        problems = library.unpack_vpk_folders(["Mixed Pyro"], lambda *_: None)

        self.assertEqual(problems, [])
        self.assertEqual((mixed / "models" / "player" / "pyro.mdl").read_bytes(), b"pyro")
        self.assertEqual((mixed / "materials" / "models" / "player" / "pyro" / "pyro_red.vmt").read_bytes(), b"loose")
        self.assertEqual((mixed / "models" / "player" / "pyro.vvd").read_bytes(), b"loose vvd")
        self.assertEqual(json.loads((mixed / "mod.json").read_text())["type"], "model")
        self.assertEqual(list(mixed.glob("*.vpk")), [])
        shutil.rmtree(mixed)

    def test_a_vpk_inside_a_mod_folder_imports_once(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            pack = make_mod(Path(tmp), "Pack", {"models/weapons/a.mdl": b"a"})
            make_vpk(pack / "extra.vpk", {"materials/b.vmt": b"b"})

            result = run_cli("import", "--extracted", str(pack))["data"]["results"][0]

        self.assertTrue(result["ok"], result["message"])
        self.assertEqual(sorted(m["name"] for m in result["mods"]), ["Pack", "extra"])
        self.assertEqual(list((addons / "Pack").glob("*.vpk")), [])
        self.assertTrue((addons / "extra" / "materials" / "b.vmt").is_file())
        shutil.rmtree(addons / "Pack")
        shutil.rmtree(addons / "extra")

    def test_textures_zipped_without_materials_land_in_materials(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            skin = make_mod(Path(tmp), "Battle Hardened Soldier", {
                "models/player/soldier/soldier_red.vtf": b"red",
                "models/player/soldier/soldier_red.vmt": b"vmt",
                "screens/01.jpg": b"jpg",
            })

            result = run_cli("import", "--extracted", str(skin))["data"]["results"][0]

        mod = addons / "Battle Hardened Soldier"
        self.assertTrue(result["ok"], result["message"])
        self.assertEqual((mod / "materials" / "models" / "player" / "soldier" / "soldier_red.vtf").read_bytes(), b"red")
        self.assertTrue((mod / "materials" / "models" / "player" / "soldier" / "soldier_red.vmt").is_file())
        self.assertFalse((mod / "models").exists())
        shutil.rmtree(mod)

    def test_a_vpk_packed_one_folder_too_high_is_lifted(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            vpk = make_vpk(Path(tmp) / "Alternate Lines.vpk", {
                "femspy/sound/vo/scout_dominationspy04.wav": b"spy",
                "femheavy/sound/vo/scout_dominationhvy10.wav": b"heavy",
            })

            result = run_cli("import", str(vpk))["data"]["results"][0]

        mod = addons / "Alternate Lines"
        self.assertTrue(result["ok"], result["message"])
        self.assertEqual((mod / "sound" / "vo" / "scout_dominationspy04.wav").read_bytes(), b"spy")
        self.assertEqual((mod / "sound" / "vo" / "scout_dominationhvy10.wav").read_bytes(), b"heavy")
        self.assertFalse((mod / "femspy").exists())
        shutil.rmtree(mod)

    def test_mods_already_in_the_library_are_tidied_too(self):
        addons, _ = library_dirs()
        old = make_mod(addons, "Old Skin", {"models/player/soldier/soldier_red.vtf": b"red", "mod.json": b"{}"})

        library.tidy_library(["Old Skin"])

        self.assertTrue((old / "materials" / "models" / "player" / "soldier" / "soldier_red.vtf").is_file())
        self.assertFalse((old / "models").exists())
        shutil.rmtree(old)

    def test_a_damaged_vpk_inside_a_library_mod_is_left_as_it_was(self):
        addons, _ = library_dirs()
        old = addons / "Cut Pyro"
        old.mkdir()
        with tempfile.TemporaryDirectory() as tmp:
            vpk = make_vpk(Path(tmp) / "pyro.vpk", {"models/player/pyro.mdl": b"p" * 4000})
            vpk.write_bytes(vpk.read_bytes()[:-1000])
            shutil.move(vpk, old / "pyro.vpk")

        problems = library.unpack_vpk_folders(["Cut Pyro"], lambda *_: None)

        self.assertEqual(len(problems), 1)
        self.assertIn("Cut Pyro", problems[0])
        self.assertTrue((old / "pyro.vpk").is_file())
        shutil.rmtree(old)

    def test_a_vpk_named_only_with_digits_imports(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            vpk = make_vpk(Path(tmp) / "100.vpk", {"materials/a.vmt": b"x"})

            result = run_cli("import", str(vpk))["data"]["results"][0]

        self.assertTrue(result["ok"], result["message"])
        self.assertEqual(result["mods"][0]["name"], "100")
        self.assertTrue((addons / "100" / "materials" / "a.vmt").is_file())
        shutil.rmtree(addons / "100")


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

    def test_a_mod_with_particles_keeps_its_models_and_sounds(self):
        addons, particles = library_dirs()
        vanilla = ENGINE_DIR / "vendor" / "casual-pre-loader" / "backup" / "particles" / "item_fx.pcf"
        with tempfile.TemporaryDirectory() as tmp:
            mod = make_mod(Path(tmp), "Whole Pack", {
                "models/weapons/c_models/c_bat.mdl": b"m",
                "sound/weapons/bat_hit.wav": b"s",
                "materials/models/weapons/c_bat.vmt": b"v",
            })
            (mod / "particles").mkdir()
            shutil.copy2(vanilla, mod / "particles" / "item_fx.pcf")

            result = run_cli("import", str(mod))["data"]["results"][0]

        self.assertTrue(result["ok"], result["message"])
        self.assertEqual(sorted((m["kind"], m["name"]) for m in result["mods"]), [("addon", "Whole Pack"), ("particle", "Whole Pack")])
        self.assertTrue((addons / "Whole Pack" / "models" / "weapons" / "c_models" / "c_bat.mdl").is_file())
        self.assertTrue((addons / "Whole Pack" / "sound" / "weapons" / "bat_hit.wav").is_file())
        self.assertTrue((addons / "Whole Pack" / "materials" / "models" / "weapons" / "c_bat.vmt").is_file())
        self.assertFalse((addons / "Whole Pack" / "particles").exists())
        self.assertTrue((particles / "Whole Pack" / "actual_particles").is_dir())
        shutil.rmtree(addons / "Whole Pack")
        shutil.rmtree(particles / "Whole Pack")

    def test_a_pack_of_only_particles_and_their_materials_stays_one_mod(self):
        addons, particles = library_dirs()
        vanilla = ENGINE_DIR / "vendor" / "casual-pre-loader" / "backup" / "particles" / "item_fx.pcf"
        with tempfile.TemporaryDirectory() as tmp:
            mod = make_mod(Path(tmp), "Just Effects", {"materials/effects/spark.vmt": b"v"})
            (mod / "particles").mkdir()
            shutil.copy2(vanilla, mod / "particles" / "item_fx.pcf")

            result = run_cli("import", str(mod))["data"]["results"][0]

        self.assertEqual([(m["kind"], m["name"]) for m in result["mods"]], [("particle", "Just Effects")])
        self.assertFalse((addons / "Just Effects").exists())
        shutil.rmtree(particles / "Just Effects")


class PartsTest(unittest.TestCase):
    def fake_tf(self, root: Path) -> Path:
        tf = root / "tf"
        make_vpk(tf / "tf2_misc_dir.vpk", {
            "materials/models/player/pyro/pyro_lightwarp.vtf": b"x",
            "sound/ui/hitsound.wav": b"x",
            "sound/ui/killsound.wav": b"x",
            "materials/models/player/scout/eyeball_l.vtf": b"x",
            "materials/models/player/pyro/eyeball_l.vtf": b"x",
        })
        return tf

    def test_variants_of_the_same_files_are_listed_as_alternatives(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "Scout FP Overhaul"
            make_mod(root, "54 FOV", {"models/weapons/c_models/c_scout_arms.mdl": b"54"})
            make_mod(root, "70 FOV", {"models/weapons/c_models/c_scout_arms.mdl": b"70"})
            make_mod(root, "Extras", {"materials/hud/extra.vmt": b"x"})

            parts = {p["name"]: p for p in run_cli("inspect", str(root))["data"]["parts"]}

        self.assertEqual(sorted(parts), ["54 FOV", "70 FOV", "Extras"])
        self.assertEqual(parts["54 FOV"]["conflicts"], ["70 FOV"])
        self.assertEqual(parts["Extras"]["conflicts"], [])
        self.assertEqual(parts["Extras"]["kind"], "folder")

    def test_only_the_chosen_part_is_imported(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "Arms"
            make_mod(root, "Arms 54", {"models/weapons/c_models/c_scout_arms.mdl": b"54"})
            make_mod(root, "Arms 70", {"models/weapons/c_models/c_scout_arms.mdl": b"70"})

            result = run_cli("import", "--extracted", str(root), "--part", "Arms 70")["data"]["results"][0]

        self.assertEqual([m["name"] for m in result["mods"]], ["Arms 70"])
        self.assertFalse((addons / "Arms 54").exists())
        shutil.rmtree(addons / "Arms 70")

    def test_loose_files_go_where_the_game_keeps_files_of_that_name(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            tf = self.fake_tf(Path(tmp))
            root = Path(tmp) / "Cinematic Lightwarp"
            root.mkdir()
            (root / "pyro_lightwarp.vtf").write_bytes(b"warp")
            (root / "Readme.txt").write_text("put it in materials")

            result = run_cli("import", "--tf-path", str(tf), "--extracted", str(root))["data"]["results"][0]

        self.assertTrue(result["ok"], result["message"])
        placed = addons / "Cinematic Lightwarp" / "materials" / "models" / "player" / "pyro" / "pyro_lightwarp.vtf"
        self.assertEqual(placed.read_bytes(), b"warp")
        shutil.rmtree(addons / "Cinematic Lightwarp")

    def test_loose_variants_in_their_own_folders_are_separate_parts(self):
        with tempfile.TemporaryDirectory() as tmp:
            tf = self.fake_tf(Path(tmp))
            root = Path(tmp) / "Hitsounds"
            for variant in ("Quake", "Ding"):
                (root / variant).mkdir(parents=True)
                (root / variant / "hitsound.wav").write_bytes(variant.encode())

            parts = run_cli("inspect", "--tf-path", str(tf), str(root))["data"]["parts"]

        self.assertEqual(sorted(p["name"] for p in parts), ["Hitsounds - Ding", "Hitsounds - Quake"])
        self.assertTrue(all(p["kind"] == "loose" and p["conflicts"] for p in parts))

    def test_names_the_game_uses_more_than_once_are_left_out_and_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            tf = self.fake_tf(Path(tmp))
            root = Path(tmp) / "Eyes"
            root.mkdir()
            (root / "eyeball_l.vtf").write_bytes(b"x")
            (root / "killsound.wav").write_bytes(b"x")

            data = run_cli("inspect", "--tf-path", str(tf), str(root))["data"]

        self.assertEqual([p["files"] for p in data["parts"]], [1])
        self.assertEqual(data["skipped"], ["eyeball_l.vtf"])

    def test_a_loose_map_goes_into_maps(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "cp_escaperoom_v3"
            root.mkdir()
            (root / "cp_escaperoom_v3.bsp").write_bytes(b"VBSP")

            parts = run_cli("inspect", str(root))["data"]["parts"]

        self.assertEqual([(p["name"], p["kind"], p["files"]) for p in parts], [("cp_escaperoom_v3", "loose", 1)])

    def test_a_download_with_only_a_readme_says_what_it_holds(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "bot_overhaul_part_1"
            (root / "Bot Overhaul").mkdir(parents=True)
            (root / "Bot Overhaul" / "DOWNLOAD FOR PART 1.txt").write_text("get it elsewhere")

            data = run_cli("inspect", str(root))["data"]
            result = run_cli("import", "--extracted", str(root))["data"]["results"][0]

        self.assertEqual(data["parts"], [])
        self.assertEqual(data["documents"], ["Bot Overhaul/DOWNLOAD FOR PART 1.txt"])
        self.assertIn("DOWNLOAD FOR PART 1.txt", result["message"])

    def test_a_single_part_can_be_named_by_the_caller(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "download"
            make_mod(root / "tf" / "custom", "my_custom_stuff", {"sound/ui/hitsound.wav": b"x"})

            result = run_cli("import", "--name", "Quake III Arena hit indicator", "--extracted", str(root))["data"]["results"][0]

        self.assertEqual([m["name"] for m in result["mods"]], ["Quake III Arena hit indicator"])
        shutil.rmtree(addons / "Quake III Arena hit indicator")

    def test_a_caller_name_with_a_path_in_it_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_mod(Path(tmp), "mod", {"sound/ui/hitsound.wav": b"x"})

            result = run_cli("import", "--name", "../escape", str(root))["data"]["results"][0]

        self.assertFalse(result["ok"])

    def test_a_generic_mod_folder_takes_the_name_above_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "machine_vs_machine"
            make_mod(root / "Robot animations only" / "tf", "models", {"player/scout_animations.mdl": b"a"})
            make_mod(root / "Robots on player animations" / "tf", "models", {"bots/demo/bot_demo_animations.mdl": b"b"})

            parts = library.inspect(root)["parts"]

        self.assertEqual(sorted(p["name"] for p in parts), ["Robot animations only", "Robots on player animations"])

    def test_parts_that_would_share_a_name_get_numbered(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "Pack"
            make_mod(root / "red", "Sniper", {"materials/a.vmt": b"x"})
            make_mod(root / "blu", "Sniper", {"materials/b.vmt": b"x"})

            names = sorted(p["name"] for p in run_cli("inspect", str(root))["data"]["parts"])

        self.assertEqual(names, ["Sniper", "Sniper 2"])

    def test_inspecting_something_that_is_not_a_mod_says_so(self):
        with tempfile.TemporaryDirectory() as tmp:
            notes = Path(tmp) / "notes.exe"
            notes.write_bytes(b"MZ")

            result = run_cli("inspect", str(notes))

        self.assertFalse(result["success"])


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
    def compiler(self, game: Path = Path("/games/$(touch pwned)/Team Fortress 2")):
        from core.quickprecache.studio_mdl import StudioMDL, StudioMDLVersion

        compiler = StudioMDL.__new__(StudioMDL)
        compiler.game_path = game
        compiler.bundled_studiomdl_path = Path("/sdk/studiomdl.exe")
        compiler.studio_mdl_version = StudioMDLVersion.STUDIOMDL32
        return compiler

    def run_compile(self, returncode, game: Path | None = None, qc: Path = Path("/tmp/a b/`id`.qc"), writes=True):
        def run(args, **kwargs):
            if writes and game is not None:
                (game / "tf" / "models").mkdir(parents=True, exist_ok=True)
                (game / "tf" / "models" / "precache_0.mdl").write_bytes(b"IDST")
            return mock.Mock(returncode=returncode, stdout="compiling\n")

        precache.reset()
        with mock.patch("subprocess.run", side_effect=run) as call:
            ok = (self.compiler(game) if game else self.compiler()).make_model(qc)
        return ok, call.call_args

    def test_paths_reach_the_compiler_as_arguments(self):
        ok, call = self.run_compile(0)

        self.assertTrue(ok)
        self.assertIsInstance(call.args[0], list)
        self.assertFalse(call.kwargs.get("shell", False))
        qc = str(Path("/tmp/a b/`id`.qc"))
        self.assertIn(qc if sys.platform == "win32" else f"Z:{qc}", call.args[0])
        self.assertTrue(call.kwargs.get("timeout"))

    def test_failed_compiles_become_a_build_warning(self):
        ok, _ = self.run_compile(1)

        self.assertFalse(ok)
        self.assertTrue(any("QuickPrecache" in w and "exited with 1" in w for w in precache.warnings()))

    @unittest.skipIf(sys.platform == "win32", "Wine is only used off Windows")
    def test_wine_gets_its_own_prefix_and_no_windows(self):
        from core.config import config

        _, call = self.run_compile(0)

        env = call.kwargs["env"]
        self.assertEqual(Path(env["WINEPREFIX"]), config.project_dir / "wine")
        self.assertEqual(env["DISPLAY"], "")
        self.assertEqual(env["WAYLAND_DISPLAY"], "")
        self.assertIn("winemenubuilder.exe=d", env["WINEDLLOVERRIDES"])
        self.assertIn("mscoree", env["WINEDLLOVERRIDES"])

    def test_a_compile_that_writes_no_model_failed(self):
        with tempfile.TemporaryDirectory() as tmp:
            qc = Path(tmp) / "precache_0.qc"
            qc.write_text('$modelname "precache_0.mdl"\n$includemodel "bots/pyro/bot_pyro.mdl"\n')

            wrote, _ = self.run_compile(0, Path(tmp) / "game", qc)
            silent, _ = self.run_compile(0, Path(tmp) / "empty", qc, writes=False)

        self.assertTrue(wrote)
        self.assertFalse(silent)
        self.assertTrue(any("precache_0.mdl" in w for w in precache.warnings()))

    def test_a_stuck_compile_is_stopped(self):
        precache.reset()
        with mock.patch("subprocess.run", side_effect=subprocess.TimeoutExpired("wine", 300)):
            ok = self.compiler().make_model(Path("/tmp/precache_0.qc"))

        self.assertFalse(ok)
        self.assertTrue(any("precache_0.qc" in w for w in precache.warnings()))

    def test_missing_studiomdl_is_named(self):
        from core.quickprecache.quick_precache import QuickPrecache
        from core.quickprecache.studio_mdl import StudioMDL

        precache.install()
        precache.reset()
        with tempfile.TemporaryDirectory() as tmp, \
                mock.patch.object(StudioMDL, "_get_studio_mdl_version", return_value=StudioMDLVersionMissing()):
            ok = QuickPrecache(Path(tmp)).run(auto=True)

        self.assertFalse(ok)
        self.assertTrue(any("studiomdl.exe" in w for w in precache.warnings()))

    def test_the_bundled_studiomdl_ships_with_its_dlls_and_license(self):
        studio = ENGINE_DIR / "vendor" / "casual-pre-loader" / "core" / "quickprecache" / "studio"

        for name in ("studiomdl.exe", "tier0.dll", "vstdlib.dll", "bin/FileSystem_Stdio.dll", "bin/MaterialSystem.dll",
                     "bin/StudioRender.dll", "bin/mdllib.dll", "LICENSE", "thirdpartylegalnotices.txt"):
            self.assertTrue((studio / name).is_file(), name)


def StudioMDLVersionMissing():
    from core.quickprecache.studio_mdl import StudioMDLVersion

    return StudioMDLVersion.MISSING


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


class OtherGamesTest(unittest.TestCase):
    def test_huds_reach_games_other_than_tf2_and_leave_with_the_preload(self):
        addons, _ = library_dirs()
        with tempfile.TemporaryDirectory() as tmp:
            hud = make_mod(Path(tmp) / "src", "Some HUD", {"resource/clientscheme.res": b"x", "scripts/hudlayout.res": b"y"})
            (hud / "mod.json").write_text(json.dumps({"addon_name": "Some HUD", "type": "Hud"}))
            shutil.copytree(hud, addons / "Some HUD")
            custom = Path(tmp) / "game" / "custom"
            (custom / "player-hud").mkdir(parents=True)
            (custom / "player-hud" / "keep.txt").write_text("mine")

            build.place_huds(custom, ["Some HUD"])
            placed = json.loads((custom / "some hud" / "mod.json").read_text())
            build.remove_huds(custom)

            self.assertTrue(placed["preloader_installed"])
            self.assertFalse((custom / "some hud").exists())
            self.assertEqual((custom / "player-hud" / "keep.txt").read_text(), "mine")
        shutil.rmtree(addons / "Some HUD")

    def test_an_interrupted_build_is_undone_for_the_game_it_was_for(self):
        with tempfile.TemporaryDirectory() as tmp:
            journal = Journal(Path(tmp) / "journal.json")
            journal.begin("install", Path("/games/tf_goldrush"), game="TF2GR")
            entry = json.loads(journal.path.read_text())
            entry["pid"] = 2 ** 22 + 12345
            journal.path.write_text(json.dumps(entry))
            with mock.patch.object(build, "journal", return_value=journal), mock.patch.object(build, "uninstall") as undo:
                build.recover(lambda *_: None)

        undo.assert_called_once()
        self.assertEqual(undo.call_args.args[2], "TF2GR")


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
