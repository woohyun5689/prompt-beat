#!/usr/bin/env python3
"""Audit every Unity music chart against its source files, PCM, and generation rules."""

import argparse
import bisect
import csv
import gc
import hashlib
import json
import math
from pathlib import Path

import librosa
import numpy as np

import generate_unity_note_charts as chartgen


CURRENT_GENERATOR = chartgen.GENERATOR_VERSION
DIFFICULTIES = ("EASY", "NORMAL", "HARD")


def normalized_name(value):
    return str(value).strip().casefold()


def percentile(values, amount):
    return float(np.percentile(values, amount)) if values else 0.0


def nearest_peak_distances(peak_samples, samples):
    if len(peak_samples) == 0 or not samples:
        return []
    query = np.asarray(samples, dtype=np.int64)
    indices = np.searchsorted(peak_samples, query)
    right = np.minimum(indices, len(peak_samples) - 1)
    left = np.maximum(indices - 1, 0)
    distances = np.minimum(
        np.abs(peak_samples[right] - query),
        np.abs(peak_samples[left] - query),
    )
    return distances.astype(np.int64).tolist()


def build_candidates_from_document(document, precision):
    beats = [int(sample) for sample in document["beatSamples"]]
    beat_set = set(beats)
    halves_by_beat = {}
    for sample in document["gridSamples"]:
        sample = int(sample)
        if sample in beat_set:
            continue
        beat_index = bisect.bisect_right(beats, sample) - 1
        if beat_index < 0:
            raise ValueError(f"Grid sample {sample} precedes the first beat")
        halves_by_beat.setdefault(beat_index, []).append(sample)

    candidates = []
    radius = int(round(0.028 * int(document["sampleRate"])))
    for beat_index, beat_sample in enumerate(beats):
        samples = [(beat_sample, 0)]
        samples.extend(
            (sample, 2)
            for sample in sorted(halves_by_beat.get(beat_index, []))
        )
        for sample, subdivision in samples:
            strength, energy = chartgen.precision_window_values(
                precision,
                sample,
                radius,
            )
            beat_in_bar = beat_index % chartgen.BEATS_PER_BAR
            if subdivision == 0:
                accent_bonus = (
                    0.12
                    if beat_in_bar == 0
                    else 0.05 if beat_in_bar == 2 else 0.0
                )
            else:
                accent_bonus = 0.025
            candidates.append({
                "hitSample": sample,
                "beatIndex": beat_index,
                "subdivision": subdivision,
                "strength": strength,
                "energy": energy,
                "score": strength * 0.72 + energy * 0.28 + accent_bonus,
            })
    return candidates


def rebuild_difficulty_charts(document, precision):
    candidates = build_candidates_from_document(document, precision)
    charts = [
        chartgen.select_onbeat_difficulty_chart(
            candidates,
            difficulty,
            int(document["sampleRate"]),
            int(document["playableEndSample"]),
        )
        for difficulty in DIFFICULTIES
    ]
    chart_by_difficulty = {
        chart["difficulty"]: chart for chart in charts
    }
    beat_sample_set = set(document["beatSamples"])
    for lower_name, upper_name in (("NORMAL", "HARD"), ("EASY", "NORMAL")):
        lower_chart = chart_by_difficulty[lower_name]
        upper_samples = {
            note["hitSample"]
            for note in chart_by_difficulty[upper_name]["notes"]
        }
        previous_count = len(lower_chart["notes"])
        lower_chart["notes"] = [
            note
            for note in lower_chart["notes"]
            if note["hitSample"] in upper_samples
        ]
        lower_chart["nestedNotesRemoved"] = (
            previous_count - len(lower_chart["notes"])
        )
        lower_chart["actualDensityPercent"] = round(
            len(lower_chart["notes"])
            / max(1, lower_chart["activeBeatCount"])
            * 100.0,
            3,
        )
        lower_chart["burstNoteCount"] = sum(
            note["hitSample"] not in beat_sample_set
            for note in lower_chart["notes"]
        )
        lower_chart["quarterBurstNoteCount"] = 0
    for chart in charts:
        chartgen.apply_onbeat_color_patterns(chart, candidates)
    return charts, candidates


def compare_regenerated_charts(document, regenerated):
    stored = {
        chart["difficulty"]: chart
        for chart in document["charts"]
    }
    regenerated_by_name = {
        chart["difficulty"]: chart
        for chart in regenerated
    }
    mismatches = []
    for difficulty in DIFFICULTIES:
        if difficulty not in stored:
            mismatches.append(f"missing {difficulty}")
            continue
        if stored[difficulty] != regenerated_by_name[difficulty]:
            stored_notes = stored[difficulty].get("notes", [])
            expected_notes = regenerated_by_name[difficulty].get("notes", [])
            first_difference = next(
                (
                    index
                    for index, pair in enumerate(zip(stored_notes, expected_notes))
                    if pair[0] != pair[1]
                ),
                min(len(stored_notes), len(expected_notes)),
            )
            mismatches.append(
                f"{difficulty} differs at note {first_difference} "
                f"(stored={len(stored_notes)}, regenerated={len(expected_notes)})"
            )
    return mismatches


def independent_tempo(precision, sample_rate):
    stride = 16
    onset_envelope = np.asarray(
        precision["onset"][::stride],
        dtype=np.float32,
    )
    estimate = librosa.feature.tempo(
        onset_envelope=onset_envelope,
        sr=sample_rate,
        hop_length=precision["hop"] * stride,
        aggregate=np.median,
    )
    return float(np.asarray(estimate).reshape(-1)[0])


def octave_equivalent_tempo_error(reference_bpm, estimated_bpm):
    equivalents = [
        estimated_bpm * factor
        for factor in (0.25, 0.5, 1.0, 2.0, 4.0)
    ]
    matched = min(equivalents, key=lambda value: abs(value - reference_bpm))
    return matched, abs(matched - reference_bpm) / reference_bpm * 100.0


def sampled_onset_score(precision, samples, sample_rate):
    samples = np.asarray(samples, dtype=np.float64)
    if len(samples) == 0:
        return 0.0
    offsets = np.arange(
        -int(round(0.012 * sample_rate)),
        int(round(0.012 * sample_rate)) + 1,
        max(1, int(round(0.004 * sample_rate))),
        dtype=np.float64,
    )
    values = [
        chartgen.precision_values_at_samples(
            precision["onset"],
            precision,
            samples + offset,
        )
        for offset in offsets
    ]
    return float(np.mean(np.max(np.vstack(values), axis=0)))


def best_constant_grid_score(precision, bpm, total_samples, sample_rate):
    period = 60.0 * sample_rate / bpm
    phase_step = max(1, int(round(0.005 * sample_rate)))
    best_score = -1.0
    for phase in np.arange(0.0, period, phase_step):
        samples = np.arange(phase, total_samples, period)
        best_score = max(
            best_score,
            sampled_onset_score(precision, samples, sample_rate),
        )
    return best_score


def pulse_periodicity(precision, bpm, sample_rate):
    stride = 16
    envelope = np.asarray(precision["onset"][::stride], dtype=np.float64)
    envelope -= np.mean(envelope)
    lag = int(round(
        (60.0 / bpm)
        * sample_rate
        / (precision["hop"] * stride)
    ))
    radius = max(1, int(round(lag * 0.02)))
    denominator = max(1e-12, float(np.dot(envelope, envelope)))
    scores = []
    for candidate_lag in range(max(1, lag - radius), lag + radius + 1):
        scores.append(
            float(np.dot(
                envelope[:-candidate_lag],
                envelope[candidate_lag:],
            ))
            / denominator
        )
    return max(scores, default=0.0)


def section_transient_metrics(precision, beat_samples, sample_rate):
    peak_samples = precision["peak_samples"]
    radius = int(round(0.03 * sample_rate))
    sections = []
    for first in range(0, len(beat_samples), 32):
        section_beats = beat_samples[first : first + 32]
        active_beats = []
        for sample in section_beats:
            strength, _ = chartgen.precision_window_values(
                precision,
                sample,
                radius,
            )
            if strength >= 0.08:
                active_beats.append(sample)
        distances = nearest_peak_distances(peak_samples, active_beats)
        sections.append({
            "firstBeatIndex": first,
            "beatCount": len(section_beats),
            "activeBeatCount": len(active_beats),
            "medianMs": percentile(distances, 50) / sample_rate * 1000.0,
            "p95Ms": percentile(distances, 95) / sample_rate * 1000.0,
        })
    active_sections = [
        section for section in sections if section["activeBeatCount"] > 0
    ]
    return {
        "maximumMedianMs": max(
            (section["medianMs"] for section in active_sections),
            default=0.0,
        ),
        "maximumP95Ms": max(
            (section["p95Ms"] for section in active_sections),
            default=0.0,
        ),
        "endingMedianMs": (
            active_sections[-1]["medianMs"] if active_sections else 0.0
        ),
        "endingP95Ms": (
            active_sections[-1]["p95Ms"] if active_sections else 0.0
        ),
        "sections": sections,
    }


def note_strengths(candidates, selected_samples, subdivision=None):
    selected = set(selected_samples)
    return [
        float(candidate["strength"])
        for candidate in candidates
        if candidate["hitSample"] in selected
        and (subdivision is None or candidate["subdivision"] == subdivision)
    ]


def write_reports(output_folder, summary, rows):
    output_folder.mkdir(parents=True, exist_ok=True)
    json_path = output_folder / "note-chart-audit-112.json"
    csv_path = output_folder / "note-chart-audit-112.csv"
    markdown_path = output_folder / "note-chart-audit-112.md"

    json_path.write_text(
        json.dumps({"summary": summary, "songs": rows}, ensure_ascii=False, indent=2)
        + "\n",
        encoding="utf-8",
    )

    csv_fields = [
        "index",
        "status",
        "chartKey",
        "songName",
        "bpm",
        "independentBpm",
        "tempoErrorPercent",
        "beatGridMode",
        "durationSeconds",
        "easyNotes",
        "normalNotes",
        "hardNotes",
        "hardMainNotes",
        "hardHalfNotes",
        "wheelNotes",
        "minimumGapMs",
        "endingMarginMs",
        "beatTransientMedianMs",
        "beatTransientP95Ms",
        "mainOnsetSupportPercent",
        "hardHalfOnsetSupportPercent",
        "sourcePcmCorrelation",
        "sourceToUnityLagMs",
        "adaptiveOffsetP95Ms",
        "maximumSectionTransientP95Ms",
        "endingSectionTransientP95Ms",
        "chartGridScore",
        "alternativeGridScore",
        "gridScoreRatio",
        "chartPeriodicity",
        "alternativePeriodicity",
        "periodicityRatio",
        "failures",
        "reviewReasons",
    ]
    with csv_path.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=csv_fields)
        writer.writeheader()
        for row in rows:
            output = {key: row.get(key, "") for key in csv_fields}
            output["failures"] = " | ".join(row["failures"])
            output["reviewReasons"] = " | ".join(row["reviewReasons"])
            writer.writerow(output)

    lines = [
        "# Unity Note Chart Audit - 112 Songs",
        "",
        f"- Songs checked: {summary['songsChecked']}",
        f"- PASS: {summary['passCount']}",
        f"- REVIEW: {summary['reviewCount']}",
        f"- FAIL: {summary['failCount']}",
        f"- Total notes (E/N/H): {summary['totalEasyNotes']} / "
        f"{summary['totalNormalNotes']} / {summary['totalHardNotes']}",
        f"- Global file errors: {len(summary['globalFailures'])}",
        "",
        "| # | Status | Chart | Song | BPM | E / N / H | Main / Half | Median / P95 | Result |",
        "|---:|:---:|---|---|---:|---:|---:|---:|---|",
    ]
    for row in rows:
        reasons = row["failures"] or row["reviewReasons"] or ["OK"]
        result = "; ".join(reasons).replace("|", "/")
        lines.append(
            f"| {row['index']} | {row['status']} | {row['chartKey']} | "
            f"{row['songName'].replace('|', '/')} | {row.get('bpm', 0):.3f} | "
            f"{row.get('easyNotes', 0)} / {row.get('normalNotes', 0)} / "
            f"{row.get('hardNotes', 0)} | {row.get('hardMainNotes', 0)} / "
            f"{row.get('hardHalfNotes', 0)} | "
            f"{row.get('beatTransientMedianMs', 0):.2f} / "
            f"{row.get('beatTransientP95Ms', 0):.2f} ms | {result} |"
        )
    markdown_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return json_path, csv_path, markdown_path


def audit(project, output_folder):
    music_folder = project / "Assets" / "Resources" / "Music"
    chart_folder = project / "Assets" / "Resources" / "NoteCharts"
    pcm_folder = project / "Temp" / "NoteChartPcm"
    manifest = json.loads(
        (music_folder / "bpm_manifest.json").read_text(encoding="utf-8")
    )
    songs = manifest.get("songs", [])
    pcm_manifest = json.loads(
        (pcm_folder / "manifest.json").read_text(encoding="utf-8")
    )

    global_failures = []
    manifest_keys = [str(song.get("chartKey", "")).strip() for song in songs]
    manifest_names = [str(song.get("name", "")).strip() for song in songs]
    chart_paths = sorted(chart_folder.glob("*.json"))
    mp3_paths = sorted(music_folder.glob("*.mp3"))
    pcm_clips = pcm_manifest.get("clips", [])
    if len(songs) != 112:
        global_failures.append(f"Manifest has {len(songs)} songs instead of 112")
    if len(chart_paths) != len(songs):
        global_failures.append(
            f"Chart count {len(chart_paths)} differs from manifest {len(songs)}"
        )
    if len(mp3_paths) != len(songs):
        global_failures.append(
            f"MP3 count {len(mp3_paths)} differs from manifest {len(songs)}"
        )
    if len(pcm_clips) != len(songs):
        global_failures.append(
            f"PCM count {len(pcm_clips)} differs from manifest {len(songs)}"
        )
    if len(set(manifest_keys)) != len(manifest_keys):
        global_failures.append("Manifest contains duplicate chart keys")
    normalized_names = [normalized_name(name) for name in manifest_names]
    if len(set(normalized_names)) != len(normalized_names):
        global_failures.append("Manifest contains a runtime song-name collision")

    chart_file_keys = {path.stem for path in chart_paths}
    if chart_file_keys != set(manifest_keys):
        global_failures.append(
            "Manifest/chart file mismatch: missing={} extra={}".format(
                sorted(set(manifest_keys) - chart_file_keys),
                sorted(chart_file_keys - set(manifest_keys)),
            )
        )
    mp3_names = {normalized_name(path.stem) for path in mp3_paths}
    if mp3_names != set(normalized_names):
        global_failures.append("Manifest/MP3 song-name set does not match")

    pcm_by_name = {}
    for clip in pcm_clips:
        key = normalized_name(clip["name"])
        if key in pcm_by_name:
            global_failures.append(f"Duplicate PCM clip name: {clip['name']}")
        pcm_by_name[key] = clip
    if set(pcm_by_name) != set(normalized_names):
        global_failures.append("Manifest/PCM song-name set does not match")

    rows = []
    for index, song in enumerate(songs, start=1):
        chart_key = str(song["chartKey"]).strip()
        song_name = str(song["name"]).strip()
        row = {
            "index": index,
            "chartKey": chart_key,
            "songName": song_name,
            "status": "PASS",
            "failures": [],
            "reviewReasons": [],
        }
        precision = None
        pcm = None
        try:
            chart_path = chart_folder / f"{chart_key}.json"
            mp3_path = music_folder / f"{song_name}.mp3"
            if not chart_path.exists():
                raise FileNotFoundError(f"Missing chart: {chart_path.name}")
            if not mp3_path.exists():
                raise FileNotFoundError(f"Missing MP3: {mp3_path.name}")

            document = json.loads(chart_path.read_text(encoding="utf-8"))
            chartgen.validate_chart(document)
            if document.get("generator") != CURRENT_GENERATOR:
                row["failures"].append(
                    f"generator is {document.get('generator')}, expected {CURRENT_GENERATOR}"
                )
            if document.get("songName") != song_name:
                row["failures"].append("JSON songName differs from manifest")
            if document.get("audioFile") != mp3_path.name:
                row["failures"].append("JSON audioFile differs from MP3 filename")
            audio_hash = hashlib.sha256(mp3_path.read_bytes()).hexdigest()
            if document.get("audioSha256") != audio_hash:
                row["failures"].append("MP3 SHA-256 differs from chart")
            if abs(float(song["bpm"]) - float(document["bpm"])) > 0.000001:
                row["failures"].append("Manifest BPM differs from chart")
            expected_first_beat = round(
                int(document["firstBeatSample"]) / int(document["sampleRate"]),
                6,
            )
            if abs(float(song["firstBeat"]) - expected_first_beat) > 0.000001:
                row["failures"].append("Manifest firstBeat differs from chart")

            clip = pcm_by_name.get(normalized_name(song_name))
            if clip is None:
                raise KeyError("No matching Unity PCM clip")
            pcm_path = pcm_folder / clip["file"]
            if not pcm_path.exists():
                raise FileNotFoundError(f"Missing PCM file: {clip['file']}")
            if pcm_path.stat().st_size != int(clip["samples"]) * 4:
                row["failures"].append("PCM byte length differs from PCM manifest")
            if int(document["sampleRate"]) != int(clip["sampleRate"]):
                row["failures"].append("Chart sample rate differs from Unity PCM")
            if int(document["totalSamples"]) != int(clip["samples"]):
                row["failures"].append("Chart sample count differs from Unity PCM")

            pcm = np.memmap(pcm_path, dtype="<f4", mode="r")
            sample_rate = int(clip["sampleRate"])
            audible_end, reference_rms, threshold_rms = (
                chartgen.detect_audible_end_sample(pcm, sample_rate)
            )
            if int(document["audibleEndSample"]) != int(audible_end):
                row["failures"].append("Audible ending does not reproduce from PCM")
            expected_playable_end = max(
                0,
                min(
                    len(pcm) - 1,
                    audible_end
                    - int(round(chartgen.ENDING_GUARD_SECONDS * sample_rate)),
                ),
            )
            if int(document["playableEndSample"]) != expected_playable_end:
                row["failures"].append("Playable ending does not reproduce from PCM")
            if abs(float(document["audibleReferenceRms"]) - reference_rms) > 0.00000002:
                row["failures"].append("Audible reference RMS differs from PCM")
            if abs(float(document["audibleThresholdRms"]) - threshold_rms) > 0.00000002:
                row["failures"].append("Audible threshold RMS differs from PCM")

            precision = chartgen.build_precision_features(pcm, sample_rate)
            regenerated, candidates = rebuild_difficulty_charts(document, precision)
            row["failures"].extend(
                compare_regenerated_charts(document, regenerated)
            )

            recomputed_audit = {}
            chartgen.audit_beat_samples(
                recomputed_audit,
                precision,
                document["beatSamples"],
                sample_rate,
            )
            recomputed_median = recomputed_audit[
                "strict_grid_transient_median_ms"
            ]
            recomputed_p95 = recomputed_audit[
                "strict_grid_transient_p95_ms"
            ]
            if abs(recomputed_median - float(document["beatTransientMedianMs"])) > 0.00011:
                row["failures"].append("Beat transient median metadata is stale")
            if abs(recomputed_p95 - float(document["beatTransientP95Ms"])) > 0.00011:
                row["failures"].append("Beat transient P95 metadata is stale")

            charts = {
                chart["difficulty"]: chart
                for chart in document["charts"]
            }
            hard_samples = [
                int(note["hitSample"])
                for note in charts["HARD"]["notes"]
            ]
            beat_set = set(document["beatSamples"])
            hard_main = [sample for sample in hard_samples if sample in beat_set]
            hard_half = [sample for sample in hard_samples if sample not in beat_set]
            main_strength = note_strengths(
                candidates,
                hard_main,
                subdivision=0,
            )
            half_strength = note_strengths(
                candidates,
                hard_half,
                subdivision=2,
            )
            beat_distances = nearest_peak_distances(
                precision["peak_samples"],
                document["beatSamples"],
            )
            hard_distances = nearest_peak_distances(
                precision["peak_samples"],
                hard_samples,
            )

            source_audio, _ = librosa.load(
                str(mp3_path),
                sr=sample_rate,
                mono=True,
                duration=22.0,
            )
            lag_samples, correlation = chartgen.estimate_source_to_unity_lag(
                source_audio,
                pcm,
                sample_rate,
            )
            estimated_bpm = independent_tempo(precision, sample_rate)
            matched_bpm, tempo_error = octave_equivalent_tempo_error(
                float(document["bpm"]),
                estimated_bpm,
            )
            section_metrics = section_transient_metrics(
                precision,
                document["beatSamples"],
                sample_rate,
            )
            chart_grid_score = sampled_onset_score(
                precision,
                document["beatSamples"],
                sample_rate,
            )
            alternative_grid_score = best_constant_grid_score(
                precision,
                matched_bpm,
                len(pcm),
                sample_rate,
            )
            grid_score_ratio = chart_grid_score / max(
                1e-12,
                alternative_grid_score,
            )
            chart_periodicity = pulse_periodicity(
                precision,
                float(document["bpm"]),
                sample_rate,
            )
            alternative_periodicity = pulse_periodicity(
                precision,
                matched_bpm,
                sample_rate,
            )
            periodicity_ratio = chart_periodicity / max(
                1e-12,
                alternative_periodicity,
            )

            note_counts = {
                difficulty: len(charts[difficulty]["notes"])
                for difficulty in DIFFICULTIES
            }
            minimum_gap = min(
                (
                    second - first
                    for first, second in zip(hard_samples, hard_samples[1:])
                ),
                default=0,
            )
            wheel_notes = sum(
                "Wheel" in note["kind"]
                for chart in charts.values()
                for note in chart["notes"]
            )
            ending_margin = (
                int(document["audibleEndSample"]) - hard_samples[-1]
            )
            row.update({
                "generator": document["generator"],
                "bpm": round(float(document["bpm"]), 6),
                "independentBpm": round(matched_bpm, 6),
                "tempoErrorPercent": round(tempo_error, 4),
                "beatGridMode": document["beatGridMode"],
                "durationSeconds": round(len(pcm) / sample_rate, 3),
                "easyNotes": note_counts["EASY"],
                "normalNotes": note_counts["NORMAL"],
                "hardNotes": note_counts["HARD"],
                "hardMainNotes": len(hard_main),
                "hardHalfNotes": len(hard_half),
                "wheelNotes": wheel_notes,
                "minimumGapMs": round(minimum_gap / sample_rate * 1000.0, 4),
                "endingMarginMs": round(ending_margin / sample_rate * 1000.0, 4),
                "beatTransientMedianMs": round(recomputed_median, 4),
                "beatTransientP95Ms": round(recomputed_p95, 4),
                "beatNearestPeakMedianMs": round(
                    percentile(beat_distances, 50) / sample_rate * 1000.0,
                    4,
                ),
                "beatNearestPeakP95Ms": round(
                    percentile(beat_distances, 95) / sample_rate * 1000.0,
                    4,
                ),
                "hardNearestPeakMedianMs": round(
                    percentile(hard_distances, 50) / sample_rate * 1000.0,
                    4,
                ),
                "mainOnsetSupportPercent": round(
                    sum(value >= 0.08 for value in main_strength)
                    / max(1, len(main_strength))
                    * 100.0,
                    3,
                ),
                "hardHalfOnsetSupportPercent": round(
                    sum(value >= 0.08 for value in half_strength)
                    / max(1, len(half_strength))
                    * 100.0,
                    3,
                ),
                "hardHalfStrengthMedian": round(
                    percentile(half_strength, 50),
                    6,
                ),
                "sourcePcmCorrelation": round(correlation, 6),
                "sourceToUnityLagSamples": int(lag_samples),
                "sourceToUnityLagMs": round(
                    lag_samples / sample_rate * 1000.0,
                    4,
                ),
                "adaptiveOffsetP95Ms": round(
                    float(document.get("adaptiveOffsetP95Ms", 0.0)),
                    4,
                ),
                "maximumSectionTransientMedianMs": round(
                    section_metrics["maximumMedianMs"],
                    4,
                ),
                "maximumSectionTransientP95Ms": round(
                    section_metrics["maximumP95Ms"],
                    4,
                ),
                "endingSectionTransientMedianMs": round(
                    section_metrics["endingMedianMs"],
                    4,
                ),
                "endingSectionTransientP95Ms": round(
                    section_metrics["endingP95Ms"],
                    4,
                ),
                "chartGridScore": round(chart_grid_score, 6),
                "alternativeGridScore": round(alternative_grid_score, 6),
                "gridScoreRatio": round(grid_score_ratio, 4),
                "chartPeriodicity": round(chart_periodicity, 6),
                "alternativePeriodicity": round(alternative_periodicity, 6),
                "periodicityRatio": round(periodicity_ratio, 4),
                "sectionMetrics": section_metrics["sections"],
            })

            if recomputed_p95 > 25.0:
                row["reviewReasons"].append(
                    f"beat transient P95 is {recomputed_p95:.2f} ms"
                )
            if recomputed_median > 12.0:
                row["reviewReasons"].append(
                    f"beat transient median is {recomputed_median:.2f} ms"
                )
            tempo_is_ambiguous = tempo_error > 5.0 and (
                grid_score_ratio < 1.05
                or (
                    periodicity_ratio < 0.50
                    and grid_score_ratio < 1.30
                )
            )
            if tempo_is_ambiguous:
                row["reviewReasons"].append(
                    f"alternative BPM remains rhythmically competitive "
                    f"({matched_bpm:.2f} BPM)"
                )
            if correlation < 0.95:
                row["reviewReasons"].append(
                    f"MP3/Unity PCM correlation is {correlation:.4f}"
                )
            if row["mainOnsetSupportPercent"] < 70.0:
                row["reviewReasons"].append(
                    f"main-beat onset support is {row['mainOnsetSupportPercent']:.1f}%"
                )
            if hard_half and row["hardHalfOnsetSupportPercent"] < 35.0:
                row["reviewReasons"].append(
                    f"HARD half-beat onset support is {row['hardHalfOnsetSupportPercent']:.1f}%"
                )
            if float(document.get("adaptiveOffsetP95Ms", 0.0)) > 70.0:
                row["reviewReasons"].append(
                    "adaptive beat correction exceeds 70 ms"
                )
            if section_metrics["maximumP95Ms"] > 30.0:
                row["reviewReasons"].append(
                    f"a 32-beat section reaches "
                    f"{section_metrics['maximumP95Ms']:.2f} ms transient P95"
                )
        except Exception as error:
            row["failures"].append(f"audit exception: {type(error).__name__}: {error}")
        finally:
            if row["failures"]:
                row["status"] = "FAIL"
            elif row["reviewReasons"]:
                row["status"] = "REVIEW"
            rows.append(row)
            print(
                f"[{index:03d}/112] {row['status']:<6} {chart_key}: "
                f"{'; '.join(row['failures'] or row['reviewReasons'] or ['OK'])}",
                flush=True,
            )
            del precision, pcm
            gc.collect()

    summary = {
        "generator": CURRENT_GENERATOR,
        "songsExpected": 112,
        "songsChecked": len(rows),
        "passCount": sum(row["status"] == "PASS" for row in rows),
        "reviewCount": sum(row["status"] == "REVIEW" for row in rows),
        "failCount": sum(row["status"] == "FAIL" for row in rows),
        "totalEasyNotes": sum(int(row.get("easyNotes", 0)) for row in rows),
        "totalNormalNotes": sum(int(row.get("normalNotes", 0)) for row in rows),
        "totalHardNotes": sum(int(row.get("hardNotes", 0)) for row in rows),
        "globalFailures": global_failures,
    }
    paths = write_reports(output_folder, summary, rows)
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    for path in paths:
        print(path, flush=True)
    return 1 if global_failures or summary["failCount"] else 0


def main():
    parser = argparse.ArgumentParser()
    default_project = (
        Path(__file__).resolve().parents[1]
        / "gmaejam2026"
        / "My project (1)"
    )
    parser.add_argument("--project", type=Path, default=default_project)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "artifacts",
    )
    args = parser.parse_args()
    raise SystemExit(audit(args.project.resolve(), args.output.resolve()))


if __name__ == "__main__":
    main()
