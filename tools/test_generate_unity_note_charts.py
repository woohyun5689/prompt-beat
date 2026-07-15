import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import numpy as np


GENERATOR_PATH = Path(__file__).with_name("generate_unity_note_charts.py")
SPEC = importlib.util.spec_from_file_location("note_chart_generator", GENERATOR_PATH)
GENERATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GENERATOR)


class NoteChartGeneratorTests(unittest.TestCase):
    def test_manual_anchor_loader_and_interpolation(self):
        document = {
            "sampleRate": 1000,
            "totalSamples": 22000,
            "beatsPerBar": 4,
            "anchors": [
                {"beatIndex": 0, "sample": 1000},
                {"beatIndex": 4, "sample": 5000},
                {"beatIndex": 16, "sample": 17000},
            ],
        }
        features = {}
        precision = {
            "peak_samples": np.asarray([1000, 5000, 17000], dtype=np.int64),
        }
        beats, _ = GENERATOR.build_manual_beat_grid(
            features,
            precision,
            document["totalSamples"],
            document["sampleRate"],
            document,
        )
        self.assertEqual(1000, beats[0])
        self.assertEqual(5000, beats[4])
        self.assertEqual(17000, beats[16])

        with tempfile.TemporaryDirectory() as temporary_folder:
            project = Path(temporary_folder)
            anchor_folder = project / "Assets" / "BeatAnchorMaps"
            anchor_folder.mkdir(parents=True)
            sidecar = {"chartKey": "demo", **document}
            (anchor_folder / "demo.anchors.json").write_text(
                json.dumps(sidecar),
                encoding="utf-8",
            )
            loaded = GENERATOR.load_manual_anchor_maps(project)
        self.assertEqual(5000, loaded["demo"]["anchors"][1]["sample"])

        invalid_document = {
            **document,
            "anchors": [
                {"beatIndex": 0, "sample": 1000},
                {"beatIndex": 5, "sample": 6000},
            ],
        }
        with self.assertRaisesRegex(ValueError, "not a bar boundary"):
            GENERATOR.validate_manual_anchor_map(
                invalid_document,
                document["sampleRate"],
                document["totalSamples"],
            )

    def test_generate_all_passes_manual_anchor_map_into_chart_generation(self):
        with tempfile.TemporaryDirectory() as temporary_folder:
            project = Path(temporary_folder)
            music = project / "Assets" / "Resources" / "Music"
            anchors = project / "Assets" / "BeatAnchorMaps"
            music.mkdir(parents=True)
            anchors.mkdir(parents=True)

            manifest = {
                "songs": [{"name": "Demo Song", "chartKey": "demo"}],
            }
            (music / "bpm_manifest.json").write_text(
                json.dumps(manifest),
                encoding="utf-8",
            )
            (music / "Demo Song.mp3").write_bytes(b"demo audio")
            pcm = np.zeros(22000, dtype="<f4")
            anchor_document = {
                "chartKey": "demo",
                "songName": "Demo Song",
                "sampleRate": 1000,
                "totalSamples": len(pcm),
                "anchors": [
                    {"beatIndex": 0, "sample": 1000},
                    {"beatIndex": 4, "sample": 5000},
                ],
            }
            (anchors / "demo.anchors.json").write_text(
                json.dumps(anchor_document),
                encoding="utf-8",
            )

            captured = {}

            def fake_generate(audio, sample_rate, timing_profile, manual_anchor_map):
                captured["manual_anchor_map"] = manual_anchor_map
                beats = [1000 + index * 1000 for index in range(20)]
                features = {
                    "bpm": 60.0,
                    "strict_period_samples": 1000.0,
                    "strict_phase_samples": 1000.0,
                    "tempo_segments": [
                        {"timeSeconds": 0.0, "bpm": 60.0},
                        {"timeSeconds": 22.0, "bpm": 60.0},
                    ],
                    "audible_end_sample": 22000,
                    "playable_end_sample": 21880,
                    "audible_reference_rms": 0.1,
                    "audible_threshold_rms": 0.001,
                    "manual_anchor_map": manual_anchor_map,
                }
                charts = [
                    {
                        "difficulty": difficulty,
                        "notes": [{
                            "hitSample": beats[0],
                            "kind": "GoodTap",
                            "laneIndex": 1,
                        }],
                    }
                    for difficulty in ("EASY", "NORMAL", "HARD")
                ]
                return features, beats, beats, charts

            with mock.patch.object(
                GENERATOR,
                "generate_onbeat_difficulty_charts",
                side_effect=fake_generate,
            ), mock.patch.object(
                GENERATOR,
                "load_unity_pcm_export",
                return_value={
                    "demo song": {"audio": pcm, "sample_rate": 1000},
                },
            ), mock.patch.object(GENERATOR, "validate_chart"):
                GENERATOR.generate_all(project)

            output = json.loads(
                (project / "Assets" / "Resources" / "NoteCharts" / "demo.json")
                .read_text(encoding="utf-8")
            )
            self.assertEqual(anchor_document, captured["manual_anchor_map"])
            self.assertEqual("manual_anchors", output["beatGridMode"])
            self.assertEqual(2, output["manualAnchorCount"])

    def test_hard_adds_only_exact_half_beats_and_keeps_difficulties_nested(self):
        sample_rate = 1000
        candidates = []
        for beat_index in range(32):
            candidates.append({
                "hitSample": 1000 + beat_index * 500,
                "beatIndex": beat_index,
                "subdivision": 0,
                "strength": 0.2 + (beat_index % 4) * 0.1,
                "energy": 0.5,
                "score": 0.5 + (beat_index % 4) * 0.1,
            })
            candidates.append({
                "hitSample": 1250 + beat_index * 500,
                "beatIndex": beat_index,
                "subdivision": 2,
                "strength": 0.9,
                "energy": 0.5,
                "score": 0.9 + (beat_index % 4) * 0.01,
            })

        charts = {
            difficulty: GENERATOR.select_onbeat_difficulty_chart(
                candidates,
                difficulty,
                sample_rate,
            )
            for difficulty in ("EASY", "NORMAL", "HARD")
        }
        samples = {
            name: {note["hitSample"] for note in chart["notes"]}
            for name, chart in charts.items()
        }
        beat_samples = {
            candidate["hitSample"]
            for candidate in candidates
            if candidate["subdivision"] == 0
        }
        half_samples = {
            candidate["hitSample"]
            for candidate in candidates
            if candidate["subdivision"] == 2
        }

        self.assertEqual(16, len(samples["EASY"]))
        self.assertEqual(24, len(samples["NORMAL"]))
        self.assertGreater(len(samples["HARD"]), 32)
        self.assertTrue(samples["EASY"].issubset(samples["NORMAL"]))
        self.assertTrue(samples["NORMAL"].issubset(samples["HARD"]))
        self.assertTrue(samples["EASY"].issubset(beat_samples))
        self.assertTrue(samples["NORMAL"].issubset(beat_samples))
        self.assertTrue((samples["HARD"] - beat_samples).issubset(half_samples))
        self.assertEqual(0, charts["HARD"]["quarterBurstNoteCount"])
        self.assertEqual(0, charts["HARD"]["minimumGapSamples"])

    def test_high_bpm_hard_keeps_main_beats_and_adds_varied_half_beat_patterns(self):
        sample_rate = 1000
        beat_period = 326
        candidates = []
        for beat_index in range(32):
            main_sample = 1000 + beat_index * beat_period
            candidates.append({
                "hitSample": main_sample,
                "beatIndex": beat_index,
                "subdivision": 0,
                "strength": 1.0,
                "energy": 0.9,
                "score": 1.0,
            })
            candidates.append({
                "hitSample": main_sample + beat_period // 2,
                "beatIndex": beat_index,
                "subdivision": 2,
                "strength": 1.0,
                "energy": 0.9,
                "score": 1.0,
            })

        with mock.patch.object(
            GENERATOR,
            "choose_onbeat_wheel_indices",
            return_value=set(),
        ):
            charts = {
                difficulty: GENERATOR.select_onbeat_difficulty_chart(
                    candidates,
                    difficulty,
                    sample_rate,
                )
                for difficulty in ("EASY", "NORMAL", "HARD")
            }

        main_samples = {
            candidate["hitSample"]
            for candidate in candidates
            if candidate["subdivision"] == 0
        }
        half_by_sample = {
            candidate["hitSample"]: candidate
            for candidate in candidates
            if candidate["subdivision"] == 2
        }
        hard_samples = {
            note["hitSample"] for note in charts["HARD"]["notes"]
        }
        hard_halves = hard_samples - main_samples

        self.assertFalse(charts["EASY"]["highBpmRhythmMode"])
        self.assertFalse(charts["NORMAL"]["highBpmRhythmMode"])
        self.assertTrue(charts["HARD"]["highBpmRhythmMode"])
        self.assertAlmostEqual(184.04908, charts["HARD"]["estimatedBpm"], places=4)
        self.assertEqual(main_samples, hard_samples & main_samples)
        self.assertEqual(16, len(hard_halves))
        self.assertTrue(hard_halves.issubset(half_by_sample))
        self.assertEqual(0, charts["HARD"]["minimumGapSamples"])
        self.assertEqual(0, charts["HARD"]["quarterBurstNoteCount"])
        self.assertTrue(all(
            note["hitSample"] in main_samples
            for difficulty in ("EASY", "NORMAL")
            for note in charts[difficulty]["notes"]
        ))

        ordered = sorted(hard_samples)
        self.assertEqual(len(ordered), len(set(ordered)))
        patterns = set()
        for bar_index in range(8):
            slots = tuple(sorted(
                candidate["beatIndex"] % GENERATOR.BEATS_PER_BAR * 2 + 1
                for sample, candidate in half_by_sample.items()
                if sample in hard_halves
                and candidate["beatIndex"] // GENERATOR.BEATS_PER_BAR == bar_index
            ))
            patterns.add(slots)
        self.assertGreater(len(patterns), 1)

    def test_150_bpm_hard_can_keep_three_accents_per_bar(self):
        sample_rate = 1000
        beat_period = 400
        candidates = []
        for beat_index in range(16):
            main_sample = 1000 + beat_index * beat_period
            for subdivision, sample in (
                (0, main_sample),
                (2, main_sample + beat_period // 2),
            ):
                candidates.append({
                    "hitSample": sample,
                    "beatIndex": beat_index,
                    "subdivision": subdivision,
                    "strength": 1.4,
                    "energy": 1.3,
                    "score": 1.4,
                })

        with mock.patch.object(
            GENERATOR,
            "choose_onbeat_wheel_indices",
            return_value=set(),
        ):
            chart = GENERATOR.select_onbeat_difficulty_chart(
                candidates,
                "HARD",
                sample_rate,
            )

        main_samples = {
            candidate["hitSample"]
            for candidate in candidates
            if candidate["subdivision"] == 0
        }
        selected_samples = {
            note["hitSample"] for note in chart["notes"]
        }
        self.assertTrue(chart["highBpmRhythmMode"])
        self.assertEqual(main_samples, selected_samples & main_samples)
        self.assertEqual(12, len(selected_samples - main_samples))
        self.assertEqual(0, chart["minimumGapSamples"])

    def test_high_bpm_gap_filter_is_disabled(self):
        sample_rate = 1000
        beat_period = 260
        candidates = []
        for beat_index in range(16):
            main_sample = 1000 + beat_index * beat_period
            for subdivision, sample in (
                (0, main_sample),
                (2, main_sample + beat_period // 2),
            ):
                candidates.append({
                    "hitSample": sample,
                    "beatIndex": beat_index,
                    "subdivision": subdivision,
                    "strength": 1.4,
                    "energy": 1.3,
                    "score": 1.4,
                })

        with mock.patch.object(
            GENERATOR,
            "choose_onbeat_wheel_indices",
            return_value=set(),
        ):
            chart = GENERATOR.select_onbeat_difficulty_chart(
                candidates,
                "HARD",
                sample_rate,
            )

        main_samples = {
            candidate["hitSample"]
            for candidate in candidates
            if candidate["subdivision"] == 0
        }
        samples = sorted(note["hitSample"] for note in chart["notes"])
        self.assertTrue(chart["highBpmRhythmMode"])
        self.assertEqual(main_samples, set(samples) & main_samples)
        self.assertEqual(12, len(set(samples) - main_samples))
        self.assertEqual(0, chart["minimumGapSamples"])
        self.assertEqual(130, min(
            second - first for first, second in zip(samples, samples[1:])
        ))

    def test_locked_tempo_builds_constant_main_and_half_beat_grid(self):
        features = {}
        timing_profile = {
            "period_samples": 1000.0,
            "phase_samples": 100.0,
            "lock_tempo": True,
        }

        def fake_strict_grid(target, *_args):
            target["strict_period_samples"] = 1000.0
            target["strict_phase_samples"] = 100.0
            target["bpm"] = 60.0
            target["tempo_segments"] = [
                {"timeSeconds": 0.0, "bpm": 60.0},
                {"timeSeconds": 4.0, "bpm": 60.0},
            ]
            return [100, 1100, 2100, 3100]

        with mock.patch.object(
            GENERATOR,
            "build_strict_beat_grid",
            side_effect=fake_strict_grid,
        ), mock.patch.object(
            GENERATOR,
            "build_adaptive_anchor_beat_grid",
        ) as adaptive, mock.patch.object(
            GENERATOR,
            "audit_beat_samples",
        ), mock.patch.object(
            GENERATOR,
            "precision_window_values",
            return_value=(0.5, 0.5),
        ):
            candidates, beats, grid = GENERATOR.build_onbeat_candidates(
                features,
                {},
                4000,
                1000,
                timing_profile,
            )

        adaptive.assert_not_called()
        self.assertEqual("constant", features["beat_grid_mode"])
        self.assertEqual([100, 1100, 2100, 3100], beats)
        self.assertEqual(
            [100, 600, 1100, 1600, 2100, 2600, 3100, 3600],
            grid,
        )
        self.assertEqual(4, sum(
            candidate["subdivision"] == 2 for candidate in candidates
        ))

    def test_difficulties_stop_before_playable_end(self):
        sample_rate = 1000
        candidates = [
            {
                "hitSample": 1000 + beat_index * 500,
                "beatIndex": beat_index,
                "subdivision": 0,
                "strength": 0.8,
                "energy": 0.8,
                "score": 0.8,
            }
            for beat_index in range(16)
        ]
        playable_end_sample = 4250
        for difficulty in ("EASY", "NORMAL", "HARD"):
            chart = GENERATOR.select_onbeat_difficulty_chart(
                candidates,
                difficulty,
                sample_rate,
                playable_end_sample,
            )
            self.assertTrue(chart["notes"])
            self.assertLessEqual(
                chart["notes"][-1]["hitSample"],
                playable_end_sample,
            )

    def test_validator_rejects_non_main_beat_note(self):
        beats = [1000 + index * 500 for index in range(20)]

        def chart_notes():
            return [
                {"hitSample": sample, "kind": "GoodTap", "laneIndex": 1}
                for sample in beats[:4]
            ]

        document = {
            "version": 1,
            "totalSamples": 12000,
            "beatGridSubdivision": 1,
            "beatSamples": beats,
            "gridSamples": beats,
            "charts": [
                {"difficulty": difficulty, "notes": chart_notes()}
                for difficulty in ("EASY", "NORMAL", "HARD")
            ],
        }
        GENERATOR.validate_chart(document)
        document["charts"][2]["notes"][1]["hitSample"] = 1250
        with self.assertRaisesRegex(ValueError, "Unknown timing sample"):
            GENERATOR.validate_chart(document)


if __name__ == "__main__":
    unittest.main()
