import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eternal_engine import sounds  # noqa: E402

SCRIPT = '''"Player.Other"
{
	"wave"	"ui/other.wav"
}

"Player.HitSoundDefaultDing"
{
	"channel"		"CHAN_STATIC"
	"wave"	")ui/hitsound.wav"
}

"Player.KillSoundDefaultDing"
{
	"rndwave"
	{
		"wave"	"ui/a.wav"
	}
}

"Player.Last"
{
	"wave"	"ui/last.wav"
}
'''


class SetListTests(unittest.TestCase):
    def test_the_entry_becomes_a_random_list(self):
        text = sounds.set_list(SCRIPT, "Player.HitSoundDefaultDing", ["hit_0001.wav", "hit_0002.wav"])

        self.assertIn('"rndwave"', text.split('"Player.HitSoundDefaultDing"')[1].split('"Player.KillSoundDefaultDing"')[0])
        self.assertIn('")ui/eternal/hit_0002.wav"', text)
        self.assertNotIn(")ui/hitsound.wav", text)

    def test_an_entry_with_a_nested_block_is_replaced_whole(self):
        text = sounds.set_list(SCRIPT, "Player.KillSoundDefaultDing", ["kill_0001.wav"])

        self.assertNotIn("ui/a.wav", text)
        self.assertIn('"Player.Last"', text)
        self.assertIn("ui/last.wav", text)

    def test_other_entries_are_left_alone(self):
        text = sounds.set_list(SCRIPT, "Player.HitSoundDefaultDing", ["hit_0001.wav"])

        self.assertIn('"Player.Other"\n{\n\t"wave"\t"ui/other.wav"\n}', text)

    def test_a_missing_entry_is_reported(self):
        with self.assertRaises(ValueError):
            sounds.set_list(SCRIPT, "Player.Nope", ["hit_0001.wav"])


class ProcessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.mods = root / "mods"
        self.backup = root / "backup"
        (self.mods / "sound/ui/eternal").mkdir(parents=True)
        self.backup.mkdir()
        (self.backup / sounds.SCRIPT).write_text(SCRIPT, encoding="utf-8")

    def wave(self, name):
        (self.mods / sounds.FOLDER / name).write_bytes(b"RIFF")

    def test_numbered_sounds_survive_what_upstream_deletes(self):
        self.wave("hit_0001.wav")
        self.wave("hit_0002.wav")

        def upstream(_self, mods, _backup, _vpks):
            self.assertFalse((mods / sounds.FOLDER).exists())
            return {"message": "ok"}

        result = sounds.wrap(upstream)(None, self.mods, self.backup, [])

        self.assertEqual({"message": "ok"}, result)
        self.assertTrue((self.mods / sounds.FOLDER / "hit_0002.wav").is_file())
        script = (self.mods / "scripts" / sounds.SCRIPT).read_text(encoding="utf-8")
        self.assertIn('")ui/eternal/hit_0001.wav"', script)

    def test_a_script_upstream_already_edited_is_edited_further(self):
        self.wave("kill_0001.wav")
        self.wave("kill_0002.wav")
        (self.mods / "scripts").mkdir()
        (self.mods / "scripts" / sounds.SCRIPT).write_text(SCRIPT.replace("ui/other.wav", "misc/ui/other.wav"), encoding="utf-8")

        sounds.wrap(lambda *_: None)(None, self.mods, self.backup, [])

        script = (self.mods / "scripts" / sounds.SCRIPT).read_text(encoding="utf-8")
        self.assertIn("misc/ui/other.wav", script)
        self.assertIn("kill_0002.wav", script)

    def test_without_the_folder_upstream_runs_as_usual(self):
        (self.mods / sounds.FOLDER).rmdir()
        calls = []

        sounds.wrap(lambda *a: calls.append(a))(None, self.mods, self.backup, [])

        self.assertEqual(1, len(calls))
        self.assertFalse((self.mods / "scripts").exists())

    def test_files_that_are_not_ours_do_not_end_up_in_the_script(self):
        self.wave("hit_0001.wav")
        (self.mods / sounds.FOLDER / "evil; quit.wav").write_bytes(b"RIFF")

        sounds.wrap(lambda *_: None)(None, self.mods, self.backup, [])

        self.assertNotIn("evil", (self.mods / "scripts" / sounds.SCRIPT).read_text(encoding="utf-8"))


class ShippedScriptTests(unittest.TestCase):
    def test_the_game_script_in_the_vendored_backup_has_both_entries(self):
        script = Path(__file__).resolve().parents[1] / "vendor/casual-pre-loader/backup/scripts" / sounds.SCRIPT
        text = script.read_text(encoding="utf-8", errors="ignore")

        text = sounds.set_list(text, sounds.ENTRIES["hit"], ["hit_0001.wav", "hit_0002.wav"])
        text = sounds.set_list(text, sounds.ENTRIES["kill"], ["kill_0001.wav"])

        self.assertIn(")ui/eternal/hit_0002.wav", text)
        self.assertIn('"Player.HitSoundElectro"', text)


if __name__ == "__main__":
    unittest.main()
