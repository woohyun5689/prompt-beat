#!/usr/bin/env python3
"""Generate sample-locked Unity note-chart JSON files for every local song."""

import argparse
import hashlib
import json
import math
import random
from pathlib import Path

import librosa
import numpy as np
from scipy.ndimage import uniform_filter1d
from scipy.signal import correlate, correlation_lags, find_peaks


ANALYSIS_RATE = 100.0
BEATS_PER_BAR = 4
PHRASE_BEATS = 16
TIMING_ANALYSIS_BEATS = 16
TIMING_ANCHOR_BEATS = 4
TIMING_CONTEXT_WEIGHT = 0.20
SLOW_BPS = 1.75
REFERENCE_BPS = 2.05
FAST_BPS = 2.18
WHEEL_LEAD_IN_GAP = 0.34
AUDIBLE_END_RELATIVE_DB = -40.0
AUDIBLE_END_WINDOW_SECONDS = 0.05
ENDING_GUARD_SECONDS = 0.12
HIGH_BPM_THRESHOLD = 149.5
GENERATOR_VERSION = "bpm_eq_bps_loudness_snowflake_pattern_v8"

ORDERED_PIPELINE_SETTINGS = {
    "EASY": {
        "allows_half_beats": False,
        "eq_main_remove_percentile": 30.0,
        "eq_half_add_percentile": 101.0,
        "notes_per_beat": 0.68,
        "maximum_notes_per_second": 2.15,
        "minimum_notes_per_bar": 2,
        "maximum_notes_per_bar": 3,
        "quiet_loudness": 0.24,
        "loud_loudness": 0.92,
        "quiet_remove_count": 1,
        "color_max_run_length": 4,
        "half_trigger_minimum": 999.0,
        "maximum_half_beats_per_highlight_bar": 0,
        "highlight_target_bonus": 0,
        "wheel_score_percentile": 98.0,
        "wheel_lead_clearance_seconds": 0.34,
        "wheel_recovery_clearance_seconds": 1.20,
        "note_speed": 5.10,
    },
    "NORMAL": {
        "allows_half_beats": True,
        "eq_main_remove_percentile": 14.0,
        "eq_half_add_percentile": 76.0,
        "notes_per_beat": 1.02,
        "maximum_notes_per_second": 3.30,
        "minimum_notes_per_bar": 3,
        "maximum_notes_per_bar": 6,
        "quiet_loudness": 0.18,
        "loud_loudness": 0.76,
        "quiet_remove_count": 1,
        "color_max_run_length": 3,
        "half_trigger_minimum": 1.0,
        "maximum_half_beats_per_highlight_bar": 2,
        "highlight_target_bonus": 2,
        "wheel_score_percentile": 96.0,
        "wheel_lead_clearance_seconds": 0.34,
        "wheel_recovery_clearance_seconds": 0.95,
        "note_speed": 5.66,
    },
    "HARD": {
        "allows_half_beats": True,
        "allows_groove_half_beats": True,
        "groove_half_add_percentile": 78.0,
        "groove_half_relief_percentile": 58.0,
        "groove_half_minimum_support": 0.13,
        "groove_half_minimum_pulse": 0.08,
        "groove_half_minimum_energy": 0.20,
        "groove_half_fallback_minimum_support": 0.18,
        "groove_half_fallback_minimum_pulse": 0.10,
        "groove_half_fallback_minimum_energy": 0.16,
        "maximum_groove_half_beats_per_bar": 1,
        "maximum_main_only_bars_before_relief": 2,
        "eq_main_remove_percentile": 0.0,
        "eq_half_add_percentile": 52.0,
        "notes_per_beat": 1.55,
        "maximum_notes_per_second": 5.20,
        "minimum_notes_per_bar": 4,
        "maximum_notes_per_bar": 7,
        "quiet_loudness": 0.12,
        "loud_loudness": 0.66,
        "quiet_remove_count": 0,
        "color_max_run_length": 2,
        "half_trigger_minimum": 1.0,
        "maximum_half_beats_per_highlight_bar": 3,
        "highlight_target_bonus": 2,
        "wheel_score_percentile": 94.0,
        "wheel_lead_clearance_seconds": 0.34,
        "wheel_recovery_clearance_seconds": 0.78,
        "preserve_main_beats_around_wheels": True,
        "note_speed": 7.9375,
    },
}

SNOWFLAKE_WALTZ_REFERENCE = {
    "chart_key": "mureka_023",
    "song_name": "눈꽃 왈츠",
}

OUTPUT_NAMES = [
    "bhutan",
    "bubblegum_arcade",
    "colorful_summer",
    "shining_summer_miracle",
    "cornfield_dance",
    "our_summer",
    "blue_fragment",
    "prompt_beat_main",
    "midsummer_night_wind",
    "midsummer_night_frequency",
    "midsummer_night_drive",
    "lets_party",
]

# Final timing calibration recovered from the build-2 shipped charts.
STRICT_V6_BUILD_TIMING = {
    "bhutan": {"period_samples": 21633.181667, "phase_samples": 11429.55},
    "bubblegum_arcade": {
        "period_samples": 20514.12,
        "phase_samples": 13607.91,
        "rounding_bias_samples": -1e-7,
    },
    "colorful_summer": {
        "period_samples": 20672.005,
        "phase_samples": 5760.44,
        "rounding_bias_samples": -1e-7,
    },
    "shining_summer_miracle": {
        "period_samples": 21339.33,
        "phase_samples": 2379.74,
        "rounding_bias_samples": -1e-7,
    },
    "cornfield_dance": {"period_samples": 28022.605, "phase_samples": 6377.75},
    "our_summer": {"period_samples": 20672.275, "phase_samples": 12279.84},
    "blue_fragment": {
        "period_samples": 21513.14,
        "phase_samples": 2327.35,
        "rounding_bias_samples": -1e-7,
    },
    "prompt_beat_main": {
        "period_samples": 21689.02,
        "phase_samples": 10254.0,
        "rounding_bias_samples": -1e-7,
        "lock_tempo": True,
    },
    "midsummer_night_wind": {"period_samples": 19600.416667, "phase_samples": 11331.56},
    "midsummer_night_frequency": {"period_samples": 21452.828333, "phase_samples": 19515.71},
    "midsummer_night_drive": {
        "period_samples": 19867.73,
        "phase_samples": 9781.69,
        "rounding_bias_samples": -1e-7,
    },
    "lets_party": {
        "period_samples": 21542.55,
        "phase_samples": 11176.35,
        "rounding_bias_samples": -1e-7,
    },
}

ONBEAT_DIFFICULTY_SETTINGS = {
    "EASY": {
        "density": 0.50,
        "color_max_run_length": 4,
        "wheel_window_seconds": 24.0,
        "wheel_min_gap_seconds": 18.0,
        "wheel_minimum_strength": 0.30,
    },
    "NORMAL": {
        "density": 0.75,
        "color_max_run_length": 3,
        "wheel_window_seconds": 15.0,
        "wheel_min_gap_seconds": 11.0,
        "wheel_minimum_strength": 0.16,
    },
    "HARD": {
        "density": 1.00,
        "color_max_run_length": 2,
        "wheel_window_seconds": 9.0,
        "wheel_min_gap_seconds": 7.0,
        "wheel_minimum_strength": 0.08,
    },
}

ONBEAT_COLOR_PATTERN_SEEDS = {
    "EASY": (
        (True, True, True, False, False, False, True, True),
        (True, True, False, False, True, True, False, False),
        (True, True, True, False, False, True, False, False),
        (True, True, False, True, True, False, False, False),
    ),
    "NORMAL": (
        (True, True, False, True, False, False, True, False),
        (True, False, False, True, True, False, True, False),
        (True, True, False, False, True, False, True, False),
        (True, False, True, True, False, True, False, False),
        (True, True, False, True, True, False, False, True),
        (True, False, True, False, False, True, True, False),
    ),
    "HARD": (
        (True, False, True, True, False, True, False, False, True, False),
        (False, True, False, False, True, False, True, True, False, True),
        (True, True, False, True, False, False, True, False, True, False),
        (False, False, True, False, True, True, False, True, False, True),
        (True, False, True, False, False, True, False, True, True, False),
        (True, True, False, True, False, True, False, False, True, False),
        (True, False, False, True, False, True, True, False, True, False),
        (True, False, True, True, False, False, True, False, True, False),
    ),
}

PATTERNS = {
    2: ((0, 4), (0, 2), (0, 6)),
    3: ((0, 2, 4), (0, 4, 6), (0, 2, 6), (0, 3, 6), (0, 4, 7)),
    4: ((0, 2, 4, 6), (0, 2, 4, 7), (0, 3, 4, 6), (0, 2, 5, 6), (0, 1, 4, 6)),
    5: ((0, 2, 4, 6, 7), (0, 1, 2, 4, 6), (0, 2, 3, 4, 6), (0, 2, 4, 5, 6), (0, 1, 4, 5, 6)),
}

DIFFICULTIES = {
    "EASY": {
        "tap_gap": 0.40,
        "long_gap": 1.20,
        "long_energy_threshold": 0.30,
        "long_note_chance": 0.48,
        "color_switch_chance": 0.30,
        "good_note_chance": 0.58,
        "peak_radius": 0.06,
        "allows_half_beats": False,
    },
    "NORMAL": {
        "tap_gap": 0.30,
        "long_gap": 0.95,
        "long_energy_threshold": 0.22,
        "long_note_chance": 0.72,
        "color_switch_chance": 0.50,
        "good_note_chance": 0.55,
        "peak_radius": 0.06,
        "allows_half_beats": False,
    },
    "HARD": {
        "tap_gap": 0.24,
        "long_gap": 0.78,
        "long_energy_threshold": 0.28,
        "long_note_chance": 0.55,
        "color_switch_chance": 0.82,
        "good_note_chance": 0.52,
        "peak_radius": 0.055,
        "allows_half_beats": True,
    },
}

DIRECT_ONSET_DIFFICULTIES = {
    "EASY": {
        "percentile": 85.0,
        "minimum_score": 0.95,
        "minimum_gap": 0.44,
    },
    "NORMAL": {
        "percentile": 74.0,
        "minimum_score": 0.78,
        "minimum_gap": 0.30,
    },
    "HARD": {
        "percentile": 65.0,
        "minimum_score": 0.62,
        "minimum_gap": 0.22,
    },
}
DIRECT_ONSET_FIRST_SECONDS = 0.20
DIRECT_ONSET_GROUP_BEATS = 4


def clamp(value, low, high):
    return max(low, min(high, value))


def inverse_lerp(low, high, value):
    if high == low:
        return 0.0
    return clamp((value - low) / (high - low), 0.0, 1.0)


def lerp(low, high, amount):
    return low + (high - low) * clamp(amount, 0.0, 1.0)


def stable_seed(value):
    result = 17
    for char in value:
        result = ((result * 31 + ord(char)) + 2**31) % 2**32 - 2**31
    return 2**31 - 1 if result == -(2**31) else abs(result)


def expand_color_pattern_seeds(seeds):
    variants = []
    seen = set()
    for seed in seeds:
        for oriented in (tuple(seed), tuple(reversed(seed))):
            for inverted in (False, True):
                transformed = tuple(
                    not value if inverted else value
                    for value in oriented
                )
                for rotation in range(len(transformed)):
                    variant = (
                        transformed[rotation:]
                        + transformed[:rotation]
                    )
                    if variant not in seen:
                        seen.add(variant)
                        variants.append(variant)
    return tuple(variants)


ONBEAT_COLOR_PATTERN_BANKS = {
    difficulty: expand_color_pattern_seeds(seeds)
    for difficulty, seeds in ONBEAT_COLOR_PATTERN_SEEDS.items()
}


def normalize_envelope(values, percentile):
    if len(values) == 0:
        return values
    ordered = np.sort(values)
    index = int(round((len(ordered) - 1) * percentile))
    reference = max(0.000001, float(ordered[index]))
    return np.clip(values / reference, 0.0, 1.0)


def analyze_samples(audio, sample_rate):
    hop = max(128, int(round(sample_rate / ANALYSIS_RATE)))
    hop_count = max(1, len(audio) // hop)
    frames = audio[: hop_count * hop].reshape(hop_count, hop)
    energy = np.sqrt(np.mean(frames * frames, axis=1))
    transient = np.mean(np.abs(np.diff(frames, axis=1)), axis=1)
    energy = normalize_envelope(energy, 0.92)
    transient = normalize_envelope(transient, 0.92)

    onset = np.zeros_like(energy)
    for index in range(1, len(energy)):
        first = max(0, index - 8)
        energy_mean = float(np.mean(energy[first:index]))
        transient_mean = float(np.mean(transient[first:index]))
        energy_flux = max(0.0, float(energy[index]) - energy_mean * 0.92)
        transient_flux = max(0.0, float(transient[index]) - transient_mean * 0.90)
        onset[index] = energy_flux * 0.68 + transient_flux * 0.32

    return {
        "sample_rate": int(sample_rate),
        "total_samples": int(len(audio)),
        "hop": hop,
        "energy": energy,
        "onset": normalize_envelope(onset, 0.94),
    }


def analyze_audio(path):
    audio, sample_rate = librosa.load(str(path), sr=None, mono=True)
    return analyze_samples(audio, sample_rate)


def estimate_source_to_unity_lag(source_audio, unity_audio, sample_rate):
    comparison_samples = min(len(source_audio), len(unity_audio), sample_rate * 12)
    source = source_audio[:comparison_samples].astype(np.float64, copy=False)
    unity = unity_audio[:comparison_samples].astype(np.float64, copy=False)
    source = source - np.mean(source)
    unity = unity - np.mean(unity)
    correlations = correlate(unity, source, mode="full", method="fft")
    lags = correlation_lags(len(unity), len(source), mode="full")
    maximum_lag = int(round(sample_rate * 0.15))
    valid = (lags >= -maximum_lag) & (lags <= maximum_lag)
    lag = int(lags[valid][np.argmax(correlations[valid])])

    if lag >= 0:
        unity_aligned = unity_audio[lag : min(len(unity_audio), len(source_audio) + lag)]
        source_aligned = source_audio[: len(unity_aligned)]
    else:
        source_aligned = source_audio[-lag : min(len(source_audio), len(unity_audio) - lag)]
        unity_aligned = unity_audio[: len(source_aligned)]
    audit_samples = min(len(source_aligned), len(unity_aligned), sample_rate * 20)
    correlation = float(np.corrcoef(
        source_aligned[:audit_samples],
        unity_aligned[:audit_samples],
    )[0, 1])
    if not np.isfinite(correlation) or correlation < 0.90:
        raise ValueError(f"Could not align source MP3 to Unity PCM (correlation={correlation:.4f})")
    return lag, correlation


def load_unity_pcm_export(export_folder):
    manifest_path = export_folder / "manifest.json"
    if not manifest_path.exists():
        return {}
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    entries = {}
    for clip in manifest.get("clips", []):
        raw_path = export_folder / clip["file"]
        audio = np.memmap(raw_path, dtype="<f4", mode="r")
        if len(audio) != int(clip["samples"]):
            raise ValueError(
                f"Unity PCM sample mismatch for {clip['name']}: "
                f"manifest={clip['samples']} file={len(audio)}"
            )
        entries[clip["name"].strip().lower()] = {
            "audio": audio,
            "sample_rate": int(clip["sampleRate"]),
        }
    return entries


def load_manual_anchor_maps(project):
    folder = project / "Assets" / "BeatAnchorMaps"
    if not folder.exists():
        return {}
    maps = {}
    suffix = ".anchors.json"
    for path in sorted(folder.glob(f"*{suffix}")):
        chart_key = path.name[: -len(suffix)]
        document = json.loads(path.read_text(encoding="utf-8"))
        if document.get("chartKey") and document["chartKey"] != chart_key:
            raise ValueError(
                f"Anchor chart key mismatch: file={chart_key} json={document['chartKey']}"
            )
        maps[chart_key] = document
    return maps


def envelope_index(analysis, sample):
    return int(clamp(round(sample / analysis["hop"]), 0, len(analysis["onset"]) - 1))


def sample_envelope(envelope, analysis, sample):
    return float(envelope[envelope_index(analysis, sample)])


def average_envelope(envelope, analysis, first_sample, last_sample):
    first = envelope_index(analysis, first_sample)
    last = envelope_index(analysis, max(first_sample, last_sample))
    return float(np.mean(envelope[first : last + 1]))


def peak_onset(analysis, center_sample, radius_seconds):
    radius = max(1, int(round(radius_seconds * analysis["sample_rate"] / analysis["hop"])))
    center = envelope_index(analysis, center_sample)
    first = max(0, center - radius)
    last = min(len(analysis["onset"]) - 1, center + radius)
    return float(np.max(analysis["onset"][first : last + 1]))


def last_audible_sample(analysis):
    indices = np.flatnonzero(analysis["energy"] >= 0.05)
    if len(indices) == 0:
        return analysis["total_samples"]
    return min(analysis["total_samples"] - 1, int(indices[-1] * analysis["hop"]))


def detect_audible_end_sample(audio, sample_rate):
    window_samples = max(1, int(round(AUDIBLE_END_WINDOW_SECONDS * sample_rate)))
    frame_count = int(math.ceil(len(audio) / window_samples))
    padded = np.zeros(frame_count * window_samples, dtype=np.float32)
    padded[: len(audio)] = audio
    frames = padded.reshape(frame_count, window_samples)
    rms = np.sqrt(np.mean(np.square(frames, dtype=np.float64), axis=1))
    reference_rms = max(0.000001, float(np.percentile(rms, 95.0)))
    threshold_rms = max(
        0.000001,
        reference_rms * (10.0 ** (AUDIBLE_END_RELATIVE_DB / 20.0)),
    )
    active = rms >= threshold_rms
    if len(active) >= 3:
        active = np.convolve(active.astype(np.int8), np.ones(3, dtype=np.int8), mode="same") >= 2
    active_frames = np.flatnonzero(active)
    if len(active_frames) == 0:
        return len(audio), reference_rms, threshold_rms
    audible_end = min(len(audio), int((active_frames[-1] + 1) * window_samples))
    return audible_end, reference_rms, threshold_rms


def build_beat_samples(first_beat, bpm, sample_rate, total_samples):
    beat_seconds = 60.0 / bpm
    beats = []
    index = 0
    while True:
        sample = int(round((first_beat + index * beat_seconds) * sample_rate))
        if sample >= total_samples:
            break
        if sample >= 0 and (not beats or sample > beats[-1]):
            beats.append(sample)
        index += 1
    return beats


def midpoint_sample(first, second):
    return int(round((first + second) * 0.5))


def slot_accent(slot):
    if slot == 0:
        return 1.0
    if slot == 4:
        return 0.78
    if slot in (2, 6):
        return 0.58
    if slot == 7:
        return 0.34
    if slot in (3, 5):
        return 0.26
    return 0.18


def target_slots(difficulty, density, bps):
    if difficulty == "EASY":
        return 3 if (bps >= FAST_BPS and density >= 1.05) or density >= 1.15 else 2
    if difficulty == "HARD":
        if bps < SLOW_BPS:
            return 4 if density >= 1.08 else 3
        if bps >= FAST_BPS:
            return 5 if density >= 0.94 else 4
        return 4
    if bps < SLOW_BPS:
        return 3 if density >= 1.10 else 2
    if bps >= FAST_BPS:
        return 4 if density >= 1.0 else 3
    return 3 if density >= 1.0 else 2


def rhythm_slot_score(onset, energy, slot, density, bps, allow_half):
    score = onset * 0.62 + energy * 0.20 + slot_accent(slot) * 0.18
    if slot % 2 == 1:
        fast = inverse_lerp(REFERENCE_BPS, FAST_BPS, bps)
        slow = 1.0 - inverse_lerp(SLOW_BPS, REFERENCE_BPS, bps)
        score += max(0.0, density - 1.0) * 0.12 + fast * 0.24
        if not allow_half:
            score -= 0.28
        elif density < lerp(1.08, 0.88, fast):
            score -= lerp(0.24, 0.04, fast)
        score -= slow * 0.14
    return score


def pattern_score(pattern, slot_scores, density, bps):
    score = sum(slot_scores[slot] for slot in pattern)
    offbeats = sum(1 for slot in pattern if slot % 2 == 1)
    if len(pattern) == BEATS_PER_BAR and offbeats == 0:
        score += 0.22
    fast = inverse_lerp(REFERENCE_BPS, FAST_BPS, bps)
    penalty = lerp(0.16, 0.04, inverse_lerp(0.95, 1.2, density))
    return score - offbeats * lerp(penalty, 0.02, fast)


def best_unchosen_slot(slot_scores, chosen, allow_half, density, bps, fill):
    best_slot = -1
    best_score = -1e9
    fast = inverse_lerp(REFERENCE_BPS, FAST_BPS, bps)
    for slot in range(1, len(chosen)):
        if chosen[slot] or (not allow_half and slot % 2 == 1):
            continue
        score = slot_scores[slot]
        if fill:
            if slot == 7:
                score += lerp(0.14, 0.30, inverse_lerp(0.9, 1.2, density)) + fast * 0.12
            elif slot == 5:
                score += 0.13 + fast * 0.08
            elif slot == 3:
                score += 0.10 + fast * 0.06
            elif slot == 6:
                score += 0.08
            else:
                score += 0.02
        if score > best_score:
            best_score = score
            best_slot = slot
    return best_slot


def select_pattern(slot_scores, count, allow_half, density, bps):
    chosen = [False] * 8
    fast = inverse_lerp(REFERENCE_BPS, FAST_BPS, bps)
    half_allowed = allow_half and (density >= lerp(1.08, 0.88, fast) or count > BEATS_PER_BAR)
    candidates = [
        pattern
        for pattern in PATTERNS[min(5, max(2, count))]
        if half_allowed or all(slot % 2 == 0 for slot in pattern)
    ]
    if candidates:
        pattern = max(candidates, key=lambda item: pattern_score(item, slot_scores, density, bps))
        for slot in pattern:
            chosen[slot] = True
    else:
        chosen[0] = True
        for _ in range(1, count):
            slot = best_unchosen_slot(slot_scores, chosen, half_allowed, density, bps, False)
            if slot < 0:
                break
            chosen[slot] = True
    return chosen


def choose_color(rng, preset, last_good, run_length):
    previous = last_good
    good = not last_good if rng.random() <= preset["color_switch_chance"] else last_good
    if rng.random() <= 0.14:
        good = rng.random() <= preset["good_note_chance"]
    if good == previous:
        run_length += 1
        if run_length >= 3:
            good = not good
            run_length = 0
    else:
        run_length = 0
    return good, run_length


def tap_gap(preset, beat_seconds):
    if preset["allows_half_beats"]:
        return max(0.12, min(preset["tap_gap"], beat_seconds * 0.46))
    return max(0.18, min(preset["tap_gap"], beat_seconds * 0.68))


def wheel_lead_gap(preset, beat_seconds):
    return max(tap_gap(preset, beat_seconds), min(WHEEL_LEAD_IN_GAP, beat_seconds * 0.72))


def note_gap(kind, preset, beat_seconds):
    return preset["long_gap"] if "Wheel" in kind else tap_gap(preset, beat_seconds)


def try_add_note(notes, sample, kind, sample_rate, preset, beat_seconds):
    if notes:
        previous = notes[-1]
        previous_gap = note_gap(previous["kind"], preset, beat_seconds)
        next_gap = wheel_lead_gap(preset, beat_seconds) if "Wheel" in kind else note_gap(kind, preset, beat_seconds)
        if (sample - previous["hitSample"]) / sample_rate < max(previous_gap, next_gap):
            return False
    notes.append({"hitSample": int(sample), "kind": kind, "laneIndex": 1})
    return True


def ensure_minimum_wheels(notes, preset, sample_rate, beat_seconds):
    if len(notes) < 3:
        return
    duration = (notes[-1]["hitSample"] - notes[0]["hitSample"]) / sample_rate
    desired = max(2, int(math.floor(duration / 9.0)))
    count = sum(1 for note in notes if "Wheel" in note["kind"])
    last_wheel = -1e12
    lead_gap = wheel_lead_gap(preset, beat_seconds)
    for index in range(1, len(notes) - 1):
        if count >= desired:
            break
        note = notes[index]
        time = note["hitSample"] / sample_rate
        if "Wheel" in note["kind"]:
            last_wheel = time
            continue
        previous_gap = (note["hitSample"] - notes[index - 1]["hitSample"]) / sample_rate
        next_gap = (notes[index + 1]["hitSample"] - note["hitSample"]) / sample_rate
        if time - last_wheel < 6.0 or previous_gap < lead_gap or next_gap < preset["long_gap"]:
            continue
        note["kind"] = "GoodWheelUp" if note["kind"] == "GoodTap" else "BadWheelDown"
        last_wheel = time
        count += 1


def fill_minimum(difficulty, density, bps):
    fast = inverse_lerp(REFERENCE_BPS, FAST_BPS, bps)
    if difficulty == "EASY":
        return 0.42
    if difficulty == "HARD":
        return lerp(0.34, 0.22, inverse_lerp(0.85, 1.2, density)) - fast * 0.04
    return 0.34 - fast * 0.04


def normalize_feature(values, percentile=95.0):
    reference = max(1e-9, float(np.percentile(values, percentile)))
    return np.clip(values / reference, 0.0, 2.0)


def pulse_grid_score(onset, period, phase):
    positions = phase + np.arange(max(1, int((len(onset) - phase) / period))) * period
    positions = positions[positions < len(onset) - 1]
    if len(positions) < 8:
        return -1e9
    values = np.interp(positions, np.arange(len(onset)), onset)
    top_count = max(4, int(math.ceil(len(values) * 0.55)))
    strongest = np.partition(values, len(values) - top_count)[-top_count:]
    return float(np.mean(values) * 0.35 + np.mean(strongest) * 0.65)


def optimize_pulse_grid(onset, center_period):
    best_score = -1e9
    best_period = center_period
    best_phase = 0.0
    for period in np.arange(center_period - 1.5, center_period + 1.5001, 0.05):
        for phase in np.arange(0.0, period, 0.5):
            score = pulse_grid_score(onset, period, phase)
            if score > best_score:
                best_score = score
                best_period = float(period)
                best_phase = float(phase)

    coarse_period = best_period
    coarse_phase = best_phase
    for period in np.arange(coarse_period - 0.10, coarse_period + 0.1001, 0.005):
        for phase_offset in np.arange(-1.0, 1.0001, 0.10):
            phase = (coarse_phase + phase_offset) % period
            score = pulse_grid_score(onset, period, phase)
            if score > best_score:
                best_score = score
                best_period = float(period)
                best_phase = float(phase)
    return best_period, best_phase, best_score


def related_tempo(raw_bpm, reference_bpm):
    candidates = []
    for ratio in (0.5, 2.0 / 3.0, 1.0, 1.5, 2.0):
        candidate = raw_bpm * ratio
        if 80.0 <= candidate <= 180.0:
            candidates.append(candidate)
    if not candidates:
        return reference_bpm
    return min(candidates, key=lambda value: abs(math.log(value / reference_bpm)))


def build_local_tempo_segments(onset, sample_rate, hop, global_bpm):
    frame_rate = sample_rate / hop
    duration_seconds = len(onset) / frame_rate
    segments = []
    for start_seconds in np.arange(0.0, duration_seconds, 20.0):
        end_seconds = min(duration_seconds, start_seconds + 20.0)
        center_seconds = (start_seconds + end_seconds) * 0.5
        first = max(0, int(round(start_seconds * frame_rate)))
        last = min(len(onset), int(round(end_seconds * frame_rate)))
        if end_seconds - start_seconds < 10.0 and segments:
            local_bpm = segments[-1]["bpm"]
        elif last <= first:
            local_bpm = global_bpm
        else:
            raw_bpm = float(np.asarray(librosa.feature.tempo(
                onset_envelope=onset[first:last],
                sr=sample_rate,
                hop_length=hop,
                aggregate=np.median,
            )).reshape(-1)[0])
            local_bpm = related_tempo(raw_bpm, global_bpm)
        local_bpm = clamp(local_bpm, global_bpm * 0.94, global_bpm * 1.06)
        segments.append({
            "timeSeconds": float(center_seconds),
            "bpm": float(local_bpm),
        })
    if segments:
        segments.insert(0, {
            "timeSeconds": 0.0,
            "bpm": segments[0]["bpm"],
        })
        segments.append({
            "timeSeconds": float(duration_seconds),
            "bpm": segments[-1]["bpm"],
        })
    return segments


def local_bpm_at_frame(segments, frame, sample_rate, hop):
    time_seconds = frame * hop / sample_rate
    times = [segment["timeSeconds"] for segment in segments]
    bpms = [segment["bpm"] for segment in segments]
    return float(np.interp(time_seconds, times, bpms))


def local_phase_correction(onset, start_frame, segments, sample_rate, hop):
    frame_rate = sample_rate / hop
    local_bpm = local_bpm_at_frame(segments, start_frame, sample_rate, hop)
    period = frame_rate * 60.0 / local_bpm
    offsets = np.arange(-period * 0.10, period * 0.1001, 0.25)

    def score_offset(offset):
        positions = []
        cursor = start_frame + offset
        for _ in range(8):
            if cursor < 0 or cursor >= len(onset) - 1:
                break
            positions.append(cursor)
            bpm = local_bpm_at_frame(segments, cursor, sample_rate, hop)
            cursor += frame_rate * 60.0 / bpm
        if len(positions) < 4:
            return -1e9
        values = np.interp(positions, np.arange(len(onset)), onset)
        return float(np.mean(values) - abs(offset) / period * 0.08)

    baseline = score_offset(0.0)
    scores = [(score_offset(offset), float(offset)) for offset in offsets]
    best_score, best_offset = max(scores)
    return best_offset if best_score >= baseline + 0.025 else 0.0


def build_adaptive_beat_frames(onset, initial_phase, segments, sample_rate, hop):
    frame_rate = sample_rate / hop
    beats = []
    frame = initial_phase
    beat_index = 0
    while frame < len(onset) - 1:
        if beat_index % 8 == 0:
            frame += local_phase_correction(onset, frame, segments, sample_rate, hop)
        if beats and frame <= beats[-1]:
            frame = beats[-1] + 1.0
        beats.append(float(frame))
        bpm = local_bpm_at_frame(segments, frame, sample_rate, hop)
        frame += frame_rate * 60.0 / bpm
        beat_index += 1
    return beats


def detect_audio_pulse_grid(audio, sample_rate):
    hop = 256
    raw_onset = librosa.onset.onset_strength(
        y=audio,
        sr=sample_rate,
        hop_length=hop,
        aggregate=np.median,
    )
    onset = normalize_feature(raw_onset)
    energy = librosa.feature.rms(
        y=audio,
        frame_length=1024,
        hop_length=hop,
        center=True,
    )[0]
    energy = normalize_feature(energy, 92.0)

    frame_rate = sample_rate / hop
    minimum_lag = max(2, int(math.floor(frame_rate * 60.0 / 180.0)))
    maximum_lag = int(math.ceil(frame_rate * 60.0 / 80.0))
    tempo_signal = np.maximum(0.0, onset - float(np.median(onset)))
    autocorrelation = librosa.autocorrelate(tempo_signal, max_size=maximum_lag * 2 + 3)
    lag_scores = []
    for lag in range(minimum_lag, maximum_lag + 1):
        score = float(autocorrelation[lag]) / max(1, len(tempo_signal) - lag)
        if lag * 2 < len(autocorrelation):
            score += float(autocorrelation[lag * 2]) / max(1, len(tempo_signal) - lag * 2) * 0.22
        lag_scores.append((score, lag))
    rough_period = float(max(lag_scores)[1])

    best_period, best_phase, best_score = optimize_pulse_grid(onset, rough_period)
    bpm = 60.0 * frame_rate / best_period
    # Percussive K-pop often has a stronger three-beat accent cycle than its
    # quarter-note pulse. When that lands below the game's tempo floor, test
    # the corresponding 3:2 pulse directly from the audio instead.
    if bpm < 92.0 and bpm * 1.5 <= 150.0:
        best_period, best_phase, best_score = optimize_pulse_grid(onset, best_period / 1.5)
        bpm = 60.0 * frame_rate / best_period
    tempo_segments = build_local_tempo_segments(raw_onset, sample_rate, hop, bpm)
    beat_frames = build_adaptive_beat_frames(
        onset,
        best_phase,
        tempo_segments,
        sample_rate,
        hop,
    )
    return {
        "hop": hop,
        "onset": onset,
        "energy": energy,
        "bpm": float(bpm),
        "period_frames": best_period,
        "phase_frames": best_phase,
        "beat_frames": beat_frames,
        "tempo_segments": tempo_segments,
        "grid_score": best_score,
    }


def snap_pulse_candidate(features, expected_frame, radius_seconds, sample_rate):
    hop = features["hop"]
    onset = features["onset"]
    energy = features["energy"]
    radius = max(1, int(round(radius_seconds * sample_rate / hop)))
    center = int(round(expected_frame))
    first = max(0, center - radius)
    last = min(len(onset) - 1, center + radius)
    indices = np.arange(first, last + 1)
    distance = np.abs(indices - expected_frame) / max(1.0, radius)
    adjusted = onset[first : last + 1] - distance * 0.10
    peak_frame = int(indices[int(np.argmax(adjusted))])
    strength = float(onset[peak_frame])
    energy_value = float(energy[min(peak_frame, len(energy) - 1)])
    return peak_frame * hop, strength, energy_value


def build_precision_features(audio, sample_rate):
    hop = 32
    source = np.asarray(audio, dtype=np.float32)

    squared = np.square(source, dtype=np.float32)
    smooth_squared = uniform_filter1d(squared, size=128, mode="nearest")
    energy = np.sqrt(np.maximum(0.0, smooth_squared[::hop])).astype(np.float32)

    difference = np.empty_like(source)
    difference[0] = 0.0
    np.subtract(source[1:], source[:-1], out=difference[1:])
    np.abs(difference, out=difference)
    smooth_difference = uniform_filter1d(difference, size=32, mode="nearest")
    transient = smooth_difference[::hop].astype(np.float32)

    log_energy = np.log1p(energy * 24.0)
    log_transient = np.log1p(transient * 48.0)
    lag = max(1, int(round(0.006 * sample_rate / hop)))

    previous_energy = np.empty_like(log_energy)
    previous_energy[:lag] = log_energy[:lag]
    previous_energy[lag:] = log_energy[:-lag]
    energy_flux = np.maximum(0.0, log_energy - previous_energy)

    previous_transient = np.empty_like(log_transient)
    previous_transient[:lag] = log_transient[:lag]
    previous_transient[lag:] = log_transient[:-lag]
    transient_flux = np.maximum(0.0, log_transient - previous_transient)

    baseline_size = max(3, int(round(0.08 * sample_rate / hop)))
    energy_baseline = uniform_filter1d(log_energy, size=baseline_size, mode="nearest")
    energy_lift = np.maximum(0.0, log_energy - energy_baseline * 0.98)
    onset = normalize_feature(
        energy_flux * 0.56 + transient_flux * 0.28 + energy_lift * 0.16,
        96.0,
    )
    energy = normalize_feature(energy, 92.0)

    peak_frames, properties = find_peaks(
        onset,
        height=0.06,
        prominence=0.025,
        distance=max(1, int(round(0.03 * sample_rate / hop))),
    )

    sample_flux = np.maximum(0.0, smooth_squared - np.roll(smooth_squared, 32))
    sample_flux[:32] = 0.0
    refined_samples = []
    refined_strengths = []
    for peak_index, peak_frame in enumerate(peak_frames):
        center = int(peak_frame * hop)
        first = max(0, center - hop * 2)
        last = min(len(sample_flux), center + hop * 2 + 1)
        sample = first + int(np.argmax(sample_flux[first:last]))
        refined_samples.append(sample)
        refined_strengths.append(float(properties["peak_heights"][peak_index]))

    order = np.argsort(refined_samples)
    peak_samples = np.asarray(refined_samples, dtype=np.int64)[order]
    peak_strengths = np.asarray(refined_strengths, dtype=np.float32)[order]

    del difference, smooth_difference, sample_flux

    low_wave = uniform_filter1d(source, size=128, mode="nearest")
    low_squared = np.square(low_wave, dtype=np.float32)
    low_energy = np.sqrt(np.maximum(
        0.0,
        uniform_filter1d(low_squared, size=256, mode="nearest")[::hop],
    )).astype(np.float32)
    log_low_energy = np.log1p(low_energy * 36.0)
    previous_low = np.empty_like(log_low_energy)
    previous_low[:lag] = log_low_energy[:lag]
    previous_low[lag:] = log_low_energy[:-lag]
    low_flux = np.maximum(0.0, log_low_energy - previous_low)
    low_baseline = uniform_filter1d(log_low_energy, size=baseline_size, mode="nearest")
    low_lift = np.maximum(0.0, log_low_energy - low_baseline * 0.985)
    low_onset = normalize_feature(low_flux * 0.82 + low_lift * 0.18, 96.0)

    del squared, smooth_squared, low_wave, low_squared, low_energy
    return {
        "hop": hop,
        "onset": onset,
        "low_onset": low_onset,
        "energy": energy,
        "peak_samples": peak_samples,
        "peak_strengths": peak_strengths,
    }


def precision_value_at_sample(values, precision, sample):
    position = sample / precision["hop"]
    first = int(clamp(math.floor(position), 0, len(values) - 1))
    second = min(len(values) - 1, first + 1)
    fraction = position - first
    return float(values[first] * (1.0 - fraction) + values[second] * fraction)


def precision_values_at_samples(values, precision, samples):
    positions = np.asarray(samples, dtype=np.float64) / precision["hop"]
    positions = np.clip(positions, 0.0, len(values) - 1.0)
    first_floor = np.floor(positions)
    first = first_floor.astype(np.int64)
    second = np.minimum(len(values) - 1, first + 1)
    fractions = positions - first_floor
    return values[first] * (1.0 - fractions) + values[second] * fractions


def precision_window_values(precision, sample, radius_samples):
    center = int(round(sample / precision["hop"]))
    radius = max(1, int(math.ceil(radius_samples / precision["hop"])))
    first = max(0, center - radius)
    last = min(len(precision["onset"]), center + radius + 1)
    if last <= first:
        return 0.0, 0.0
    return (
        float(np.max(precision["onset"][first:last])),
        float(np.max(precision["energy"][first:last])),
    )


def precision_window_max(values, precision, sample, radius_samples):
    center = int(round(sample / precision["hop"]))
    radius = max(1, int(math.ceil(radius_samples / precision["hop"])))
    first = max(0, center - radius)
    last = min(len(values), center + radius + 1)
    return float(np.max(values[first:last])) if last > first else 0.0


def audit_beat_samples(features, precision, beat_samples, sample_rate):
    active_distances = []
    peak_samples = precision["peak_samples"]
    audit_radius = int(round(sample_rate * 0.03))
    for beat_sample in beat_samples:
        strength, _ = precision_window_values(
            precision,
            beat_sample,
            audit_radius,
        )
        if strength < 0.08 or len(peak_samples) == 0:
            continue
        insertion = int(np.searchsorted(peak_samples, beat_sample))
        distances = []
        if insertion < len(peak_samples):
            distances.append(abs(int(peak_samples[insertion]) - beat_sample))
        if insertion > 0:
            distances.append(abs(int(peak_samples[insertion - 1]) - beat_sample))
        if distances:
            active_distances.append(min(distances))

    features["precision_peak_count"] = int(len(peak_samples))
    features["strict_grid_active_beat_percent"] = float(
        len(active_distances) / max(1, len(beat_samples)) * 100.0
    )
    features["strict_grid_transient_median_ms"] = float(
        np.median(active_distances) / sample_rate * 1000.0
        if active_distances else 0.0
    )
    features["strict_grid_transient_p95_ms"] = float(
        np.percentile(active_distances, 95) / sample_rate * 1000.0
        if active_distances else 0.0
    )


def strict_grid_positions(phase_samples, period_samples, total_samples):
    first_index = int(math.ceil(-phase_samples / period_samples))
    last_index = int(math.floor((total_samples - 1 - phase_samples) / period_samples))
    if last_index < first_index:
        return np.asarray([], dtype=np.float64)
    indices = np.arange(first_index, last_index + 1, dtype=np.float64)
    return phase_samples + indices * period_samples


def strict_grid_score(precision, total_samples, period_samples, phase_samples):
    positions = strict_grid_positions(phase_samples, period_samples, total_samples)
    if len(positions) < 8:
        return -1e9
    broad_values = precision_values_at_samples(precision["onset"], precision, positions)
    low_values = precision_values_at_samples(precision["low_onset"], precision, positions)
    values = broad_values * 0.42 + low_values * 0.58
    offbeat_positions = positions + period_samples * 0.5
    offbeat_broad = precision_values_at_samples(
        precision["onset"], precision, offbeat_positions
    )
    offbeat_low = precision_values_at_samples(
        precision["low_onset"], precision, offbeat_positions
    )
    offbeat_values = offbeat_broad * 0.35 + offbeat_low * 0.65
    top_count = max(8, int(math.ceil(len(values) * 0.65)))
    strongest = np.partition(values, len(values) - top_count)[-top_count:]
    return float(
        np.mean(values) * 0.42
        + np.mean(strongest) * 0.58
        - np.mean(offbeat_values) * 0.10
    )


def choose_downbeat_rotation(precision, total_samples, period_samples, phase_samples):
    best_rotation = 0
    best_score = -1e9
    for rotation in range(BEATS_PER_BAR):
        downbeat_phase = phase_samples + rotation * period_samples
        downbeats = strict_grid_positions(
            downbeat_phase,
            period_samples * BEATS_PER_BAR,
            total_samples,
        )
        if len(downbeats) < 4:
            continue
        downbeat_low = precision_values_at_samples(
            precision["low_onset"], precision, downbeats
        )
        downbeat_broad = precision_values_at_samples(
            precision["onset"], precision, downbeats
        )
        downbeat_values = downbeat_low * 0.72 + downbeat_broad * 0.28

        other_positions = []
        for offset in range(1, BEATS_PER_BAR):
            positions = downbeats + offset * period_samples
            other_positions.extend(positions[positions < total_samples].tolist())
        other_values = (
            precision_values_at_samples(
                precision["low_onset"], precision, other_positions
            )
            if other_positions else np.asarray([0.0])
        )
        top_count = max(2, int(math.ceil(len(downbeat_values) * 0.55)))
        strongest = np.partition(
            downbeat_values,
            len(downbeat_values) - top_count,
        )[-top_count:]
        score = float(
            np.mean(downbeat_values) * 0.42
            + np.mean(strongest) * 0.58
            - np.mean(other_values) * 0.10
        )
        if score > best_score:
            best_score = score
            best_rotation = rotation
    return best_rotation, best_score


def build_strict_beat_grid(
    features,
    precision,
    total_samples,
    sample_rate,
    timing_profile=None,
):
    if timing_profile is not None:
        best_period = float(timing_profile["period_samples"])
        best_phase = float(timing_profile["phase_samples"]) + float(
            timing_profile.get("rounding_bias_samples", 0.0)
        )
        best_score = strict_grid_score(
            precision,
            total_samples,
            best_period,
            best_phase,
        )
        downbeat_rotation = 0
        downbeat_score = best_score
    else:
        initial_period = float(features["period_frames"] * features["hop"])
        initial_phase = float(features["phase_frames"] * features["hop"])
        best_score = -1e9
        best_period = initial_period
        best_phase = initial_phase

        phase_step = max(precision["hop"], sample_rate * 0.0015)
        for phase in np.arange(0.0, initial_period, phase_step):
            score = strict_grid_score(precision, total_samples, initial_period, phase)
            if score > best_score:
                best_score = score
                best_phase = float(phase)

        initial_phase = best_phase
        for period in np.arange(initial_period - 3.0, initial_period + 3.0001, 0.25):
            for phase in np.arange(
                initial_phase - sample_rate * 0.035,
                initial_phase + sample_rate * 0.0351,
                sample_rate * 0.0005,
            ):
                score = strict_grid_score(precision, total_samples, period, phase)
                if score > best_score:
                    best_score = score
                    best_period = float(period)
                    best_phase = float(phase)

        coarse_period = best_period
        coarse_phase = best_phase
        for period in np.arange(coarse_period - 0.35, coarse_period + 0.3501, 0.025):
            for phase in np.arange(
                coarse_phase - sample_rate * 0.002,
                coarse_phase + sample_rate * 0.00201,
                sample_rate * 0.0001,
            ):
                score = strict_grid_score(precision, total_samples, period, phase)
                if score > best_score:
                    best_score = score
                    best_period = float(period)
                    best_phase = float(phase)

        best_phase %= best_period
        downbeat_rotation, downbeat_score = choose_downbeat_rotation(
            precision,
            total_samples,
            best_period,
            best_phase,
        )
        best_phase += downbeat_rotation * best_period

    beat_positions = strict_grid_positions(best_phase, best_period, total_samples)
    beat_samples = np.rint(beat_positions).astype(np.int64).tolist()
    bpm = 60.0 * sample_rate / best_period
    duration_seconds = total_samples / sample_rate

    active_distances = []
    peak_samples = precision["peak_samples"]
    audit_radius = int(round(sample_rate * 0.03))
    for beat_sample in beat_samples:
        strength, _ = precision_window_values(precision, beat_sample, audit_radius)
        if strength < 0.08 or len(peak_samples) == 0:
            continue
        insertion = int(np.searchsorted(peak_samples, beat_sample))
        distances = []
        if insertion < len(peak_samples):
            distances.append(abs(int(peak_samples[insertion]) - beat_sample))
        if insertion > 0:
            distances.append(abs(int(peak_samples[insertion - 1]) - beat_sample))
        if distances:
            active_distances.append(min(distances))

    features["bpm"] = float(bpm)
    features["strict_period_samples"] = float(best_period)
    features["strict_phase_samples"] = float(best_phase)
    features["strict_grid_score"] = float(best_score)
    features["downbeat_rotation"] = int(downbeat_rotation)
    features["downbeat_score"] = float(downbeat_score)
    features["precision_peak_count"] = int(len(peak_samples))
    features["strict_grid_active_beat_percent"] = float(
        len(active_distances) / max(1, len(beat_samples)) * 100.0
    )
    features["strict_grid_transient_median_ms"] = float(
        np.median(active_distances) / sample_rate * 1000.0
        if active_distances else 0.0
    )
    features["strict_grid_transient_p95_ms"] = float(
        np.percentile(active_distances, 95) / sample_rate * 1000.0
        if active_distances else 0.0
    )
    features["tempo_segments"] = [
        {"timeSeconds": 0.0, "bpm": float(bpm)},
        {"timeSeconds": float(duration_seconds), "bpm": float(bpm)},
    ]
    return beat_samples


def make_piecewise_beat_evaluator(anchor_beat_indices, anchor_samples):
    beat_indices = np.asarray(anchor_beat_indices, dtype=np.float64)
    samples = np.asarray(anchor_samples, dtype=np.float64)

    def sample_at_beat(beat_position):
        segment = int(np.searchsorted(beat_indices, beat_position, side="right") - 1)
        segment = int(clamp(segment, 0, len(beat_indices) - 2))
        first_beat = beat_indices[segment]
        last_beat = beat_indices[segment + 1]
        fraction = (beat_position - first_beat) / (last_beat - first_beat)
        return samples[segment] + (samples[segment + 1] - samples[segment]) * fraction

    return sample_at_beat


def adaptive_anchor_alignment_scores(
    precision,
    expected_sample,
    offsets,
    period_samples,
    beats_in_window,
):
    candidates = expected_sample + offsets
    beat_offsets = np.arange(beats_in_window, dtype=np.float64) * period_samples
    positions = candidates[:, np.newaxis] + beat_offsets[np.newaxis, :]
    broad = precision_values_at_samples(precision["onset"], precision, positions)
    low = precision_values_at_samples(precision["low_onset"], precision, positions)
    energy = precision_values_at_samples(precision["energy"], precision, positions)
    values = low * 0.60 + broad * 0.34 + energy * 0.06

    # Preserve the 4/4 accents across whichever context window is being scored.
    weights = np.ones(beats_in_window, dtype=np.float64)
    weights[::BEATS_PER_BAR] = 1.18
    weights[0] = 1.35
    weights[2::BEATS_PER_BAR] = 1.08
    weighted = np.sum(values * weights[np.newaxis, :], axis=1) / np.sum(weights)

    offbeat_positions = positions + period_samples * 0.5
    offbeat_low = precision_values_at_samples(
        precision["low_onset"], precision, offbeat_positions
    )
    offbeat_broad = precision_values_at_samples(
        precision["onset"], precision, offbeat_positions
    )
    offbeat = offbeat_low * 0.65 + offbeat_broad * 0.35
    return weighted + np.max(values, axis=1) * 0.12 - np.mean(offbeat, axis=1) * 0.09


def build_adaptive_anchor_beat_grid(
    features,
    precision,
    total_samples,
    sample_rate,
    timing_profile=None,
):
    strict_beats = build_strict_beat_grid(
        features,
        precision,
        total_samples,
        sample_rate,
        timing_profile,
    )
    period = float(features["strict_period_samples"])
    phase = float(features["strict_phase_samples"])
    beat_count = len(strict_beats)
    anchor_beats = list(range(0, beat_count, TIMING_ANCHOR_BEATS))
    if len(anchor_beats) < 2:
        return strict_beats, lambda beat_position: phase + beat_position * period

    radius = int(round(min(period * 0.20, sample_rate * 0.10)))
    offset_step = max(precision["hop"] * 2, 1)
    offsets = np.arange(-radius, radius + 1, offset_step, dtype=np.float64)
    local_scores = []
    for beat_index in anchor_beats:
        expected = phase + beat_index * period
        local_beats = min(TIMING_ANCHOR_BEATS, beat_count - beat_index)
        context_beats = min(TIMING_ANALYSIS_BEATS, beat_count - beat_index)
        local = adaptive_anchor_alignment_scores(
            precision,
            expected,
            offsets,
            period,
            local_beats,
        )
        context = adaptive_anchor_alignment_scores(
            precision,
            expected,
            offsets,
            period,
            context_beats,
        )
        scores = local * (1.0 - TIMING_CONTEXT_WEIGHT) + context * TIMING_CONTEXT_WEIGHT
        scores -= (offsets / max(1.0, radius)) ** 2 * 0.07
        local_scores.append(scores)

    anchor_span_scale = TIMING_ANCHOR_BEATS / BEATS_PER_BAR
    transition_scale = max(sample_rate * 0.018, period * 0.038 * anchor_span_scale)
    accumulated = local_scores[0].astype(np.float64, copy=True)
    back_pointers = []
    offset_changes = offsets[np.newaxis, :] - offsets[:, np.newaxis]
    transition_penalties = (offset_changes / transition_scale) ** 2 * 0.22
    for anchor_index in range(1, len(anchor_beats)):
        transitions = accumulated[:, np.newaxis] - transition_penalties
        back = np.argmax(transitions, axis=0).astype(np.int32)
        accumulated = local_scores[anchor_index] + transitions[back, np.arange(len(offsets))]
        back_pointers.append(back)

    selected_indices = [0] * len(anchor_beats)
    selected_indices[-1] = int(np.argmax(accumulated))
    for anchor_index in range(len(anchor_beats) - 1, 0, -1):
        selected_indices[anchor_index - 1] = int(
            back_pointers[anchor_index - 1][selected_indices[anchor_index]]
        )
    selected_offsets = np.asarray(
        [offsets[index] for index in selected_indices],
        dtype=np.float64,
    )

    smoothed_offsets = selected_offsets.copy()
    for index in range(1, len(selected_offsets) - 1):
        smoothed_offsets[index] = float(np.median(selected_offsets[index - 1 : index + 2]))

    anchor_samples = []
    minimum_interval = period * TIMING_ANCHOR_BEATS * 0.98
    maximum_interval = period * TIMING_ANCHOR_BEATS * 1.02
    for index, beat_index in enumerate(anchor_beats):
        sample = float(phase + beat_index * period + smoothed_offsets[index])
        if index == 0 and sample < 0.0:
            sample = phase
        if anchor_samples:
            sample = clamp(
                sample,
                anchor_samples[-1] + minimum_interval,
                anchor_samples[-1] + maximum_interval,
            )
        anchor_samples.append(sample)

    sample_at_beat = make_piecewise_beat_evaluator(anchor_beats, anchor_samples)
    beat_samples = []
    for beat_index in range(100000):
        sample = int(round(sample_at_beat(beat_index)))
        if sample >= total_samples:
            break
        if sample >= 0:
            beat_samples.append(sample)
    else:
        raise ValueError("Adaptive beat grid exceeded the safety limit")

    segment_bpms = []
    for index in range(len(anchor_beats) - 1):
        beat_delta = anchor_beats[index + 1] - anchor_beats[index]
        sample_delta = anchor_samples[index + 1] - anchor_samples[index]
        segment_bpms.append(60.0 * sample_rate * beat_delta / sample_delta)
    average_period = (
        anchor_samples[-1] - anchor_samples[0]
    ) / (anchor_beats[-1] - anchor_beats[0])
    average_bpm = 60.0 * sample_rate / average_period

    tempo_segments = [{"timeSeconds": 0.0, "bpm": float(segment_bpms[0])}]
    for index in range(1, len(anchor_beats)):
        tempo_segments.append({
            "timeSeconds": anchor_samples[index] / sample_rate,
            "bpm": float(segment_bpms[min(index, len(segment_bpms) - 1)]),
        })
    tempo_segments.append({
        "timeSeconds": total_samples / sample_rate,
        "bpm": float(segment_bpms[-1]),
    })

    features["bpm"] = float(average_bpm)
    features["strict_period_samples"] = float(average_period)
    features["strict_phase_samples"] = float(anchor_samples[0])
    features["tempo_segments"] = tempo_segments
    features["auto_timing_anchors"] = [
        {"beatIndex": int(beat), "sample": int(round(sample))}
        for beat, sample in zip(anchor_beats, anchor_samples)
    ]
    features["adaptive_offset_p95_ms"] = float(
        np.percentile(np.abs(smoothed_offsets), 95) / sample_rate * 1000.0
    )
    features["adaptive_segment_bpm_min"] = float(min(segment_bpms))
    features["adaptive_segment_bpm_max"] = float(max(segment_bpms))
    return beat_samples, sample_at_beat


def validate_manual_anchor_map(document, sample_rate, total_samples):
    if int(document.get("sampleRate", 0)) != sample_rate:
        raise ValueError(
            f"Manual anchor sample rate mismatch: json={document.get('sampleRate')} clip={sample_rate}"
        )
    if int(document.get("totalSamples", 0)) != total_samples:
        raise ValueError(
            f"Manual anchor sample count mismatch: json={document.get('totalSamples')} clip={total_samples}"
        )
    beats_per_bar = int(document.get("beatsPerBar", BEATS_PER_BAR))
    if beats_per_bar != BEATS_PER_BAR:
        raise ValueError(
            f"Manual anchor meter mismatch: json={beats_per_bar} expected={BEATS_PER_BAR}"
        )
    raw_anchors = document.get("anchors") or []
    if not raw_anchors:
        raise ValueError("Manual anchor map has no anchors")
    anchors = [
        {
            "beatIndex": int(anchor["beatIndex"]),
            "sample": int(anchor["sample"]),
        }
        for anchor in raw_anchors
    ]
    anchors.sort(key=lambda anchor: anchor["beatIndex"])
    if anchors[0]["beatIndex"] != 0:
        raise ValueError("Manual anchor map must begin at beatIndex 0")
    previous_beat = -1
    previous_sample = -1
    for anchor in anchors:
        beat_index = anchor["beatIndex"]
        sample = anchor["sample"]
        if beat_index % beats_per_bar != 0:
            raise ValueError(
                f"Manual anchor beat is not a bar boundary: {beat_index}"
            )
        if beat_index <= previous_beat:
            raise ValueError(f"Duplicate or reversed manual beat index: {beat_index}")
        if sample <= previous_sample or sample < 0 or sample >= total_samples:
            raise ValueError(f"Invalid manual anchor sample: {sample}")
        previous_beat = beat_index
        previous_sample = sample
    return anchors


def build_manual_beat_grid(
    features,
    precision,
    total_samples,
    sample_rate,
    document,
    timing_profile=None,
):
    anchors = validate_manual_anchor_map(document, sample_rate, total_samples)
    if len(anchors) == 1:
        build_strict_beat_grid(
            features,
            precision,
            total_samples,
            sample_rate,
            timing_profile,
        )
        fallback_period = float(features["strict_period_samples"])
    else:
        fallback_period = (
            anchors[-1]["sample"] - anchors[0]["sample"]
        ) / (anchors[-1]["beatIndex"] - anchors[0]["beatIndex"])

    beat_indices = np.asarray([anchor["beatIndex"] for anchor in anchors], dtype=np.float64)
    anchor_samples = np.asarray([anchor["sample"] for anchor in anchors], dtype=np.float64)

    def sample_at_beat(beat_position):
        if len(anchors) == 1:
            return anchor_samples[0] + beat_position * fallback_period
        segment = int(np.searchsorted(beat_indices, beat_position, side="right") - 1)
        segment = int(clamp(segment, 0, len(anchors) - 2))
        first_beat = beat_indices[segment]
        last_beat = beat_indices[segment + 1]
        first_sample = anchor_samples[segment]
        last_sample = anchor_samples[segment + 1]
        fraction = (beat_position - first_beat) / (last_beat - first_beat)
        return first_sample + (last_sample - first_sample) * fraction

    beat_samples = []
    for beat_index in range(100000):
        sample = int(round(sample_at_beat(beat_index)))
        if sample >= total_samples:
            break
        if sample >= 0:
            beat_samples.append(sample)
    else:
        raise ValueError("Manual beat grid exceeded the safety limit")
    if len(beat_samples) < PHRASE_BEATS:
        raise ValueError("Manual anchor map produced too few beats")

    bpm = 60.0 * sample_rate / fallback_period
    segment_bpms = []
    if len(anchors) == 1:
        segment_bpms.append(bpm)
    else:
        for index in range(len(anchors) - 1):
            beat_delta = anchors[index + 1]["beatIndex"] - anchors[index]["beatIndex"]
            sample_delta = anchors[index + 1]["sample"] - anchors[index]["sample"]
            segment_bpms.append(60.0 * sample_rate * beat_delta / sample_delta)

    tempo_segments = [{"timeSeconds": 0.0, "bpm": float(segment_bpms[0])}]
    for index in range(1, len(anchors)):
        segment_bpm = segment_bpms[min(index, len(segment_bpms) - 1)]
        tempo_segments.append({
            "timeSeconds": anchors[index]["sample"] / sample_rate,
            "bpm": float(segment_bpm),
        })
    tempo_segments.append({
        "timeSeconds": total_samples / sample_rate,
        "bpm": float(segment_bpms[-1]),
    })

    features["bpm"] = float(bpm)
    features["strict_period_samples"] = float(fallback_period)
    features["strict_phase_samples"] = float(anchors[0]["sample"])
    features["precision_peak_count"] = int(len(precision["peak_samples"]))
    features["tempo_segments"] = tempo_segments
    features["manual_anchor_map"] = {
        "beatsPerBar": int(document.get("beatsPerBar", BEATS_PER_BAR)),
        "anchors": anchors,
    }
    return beat_samples, sample_at_beat


def nearest_precision_peak(precision, expected_sample, radius_samples, maximum_candidates=6):
    peak_samples = precision["peak_samples"]
    first = int(np.searchsorted(peak_samples, expected_sample - radius_samples, side="left"))
    last = int(np.searchsorted(peak_samples, expected_sample + radius_samples, side="right"))
    if last <= first:
        return None
    candidates = []
    for index in range(first, last):
        sample = int(peak_samples[index])
        strength = float(precision["peak_strengths"][index])
        distance = abs(sample - expected_sample) / max(1.0, radius_samples)
        candidates.append((strength - distance * 0.10, sample, strength))
    candidates.sort(reverse=True)
    return candidates[:maximum_candidates]


def track_precision_beat_path(features, precision, total_samples, sample_rate):
    predicted = np.asarray(
        [int(round(frame * features["hop"])) for frame in features["beat_frames"]],
        dtype=np.int64,
    )
    predicted = predicted[(predicted >= 0) & (predicted < total_samples)]
    if len(predicted) < 2:
        return predicted.tolist()

    median_period = float(np.median(np.diff(predicted)))
    radius = int(round(min(0.055 * sample_rate, median_period * 0.14)))
    options = []
    local_scores = []
    for expected in predicted:
        nearby = nearest_precision_peak(precision, int(expected), radius, 6) or []
        samples = [int(item[1]) for item in nearby]
        strengths = [float(item[2]) for item in nearby]
        fallback = int(clamp(
            int(round(expected / precision["hop"])) * precision["hop"],
            0,
            total_samples - 1,
        ))
        if fallback not in samples:
            samples.append(fallback)
            strengths.append(precision_value_at_sample(precision["onset"], precision, fallback) * 0.16)
        order = np.argsort(samples)
        samples = [samples[index] for index in order]
        strengths = [strengths[index] for index in order]
        options.append(samples)
        local_scores.append([
            strength - abs(sample - expected) / max(1.0, radius) * 0.18
            for sample, strength in zip(samples, strengths)
        ])

    scores = [np.asarray(local_scores[0], dtype=np.float64)]
    back_pointers = [np.full(len(options[0]), -1, dtype=np.int32)]
    for beat_index in range(1, len(options)):
        current_scores = np.full(len(options[beat_index]), -1e12, dtype=np.float64)
        current_back = np.full(len(options[beat_index]), -1, dtype=np.int32)
        expected_interval = float(predicted[beat_index] - predicted[beat_index - 1])
        interval_scale = max(sample_rate * 0.014, expected_interval * 0.035)
        offset_scale = max(sample_rate * 0.018, expected_interval * 0.04)
        for current_index, current_sample in enumerate(options[beat_index]):
            current_offset = current_sample - predicted[beat_index]
            for previous_index, previous_sample in enumerate(options[beat_index - 1]):
                interval_error = (current_sample - previous_sample) - expected_interval
                previous_offset = previous_sample - predicted[beat_index - 1]
                offset_change = current_offset - previous_offset
                transition_penalty = (
                    (interval_error / interval_scale) ** 2 * 0.34
                    + (offset_change / offset_scale) ** 2 * 0.14
                )
                score = (
                    scores[-1][previous_index]
                    + local_scores[beat_index][current_index]
                    - transition_penalty
                )
                if score > current_scores[current_index]:
                    current_scores[current_index] = score
                    current_back[current_index] = previous_index
        scores.append(current_scores)
        back_pointers.append(current_back)

    selected_indices = [0] * len(options)
    selected_indices[-1] = int(np.argmax(scores[-1]))
    for beat_index in range(len(options) - 1, 0, -1):
        selected_indices[beat_index - 1] = int(back_pointers[beat_index][selected_indices[beat_index]])
    tracked = [options[index][selected_indices[index]] for index in range(len(options))]

    for index in range(1, len(tracked)):
        if tracked[index] <= tracked[index - 1]:
            tracked[index] = tracked[index - 1] + 1
    adjustments = np.asarray(tracked, dtype=np.float64) - predicted[: len(tracked)]
    features["precision_adjustment_p95_ms"] = float(
        np.percentile(np.abs(adjustments), 95) / sample_rate * 1000.0
    )
    features["precision_peak_count"] = int(len(precision["peak_samples"]))
    peak_sample_set = set(int(sample) for sample in precision["peak_samples"])
    features["precision_beat_match_percent"] = float(
        sum(1 for sample in tracked if sample in peak_sample_set) / max(1, len(tracked)) * 100.0
    )
    return tracked


def build_audio_driven_candidates(
    features,
    precision,
    total_samples,
    sample_rate,
    manual_anchor_map=None,
):
    candidates = []
    grid_samples = []
    if manual_anchor_map:
        beat_samples, sample_at_beat = build_manual_beat_grid(
            features,
            precision,
            total_samples,
            sample_rate,
            manual_anchor_map,
        )
    else:
        beat_samples, sample_at_beat = build_adaptive_anchor_beat_grid(
            features,
            precision,
            total_samples,
            sample_rate,
        )

    beat_count = len(beat_samples)
    for beat_index in range(beat_count):
        for subdivision in range(4):
            expected_sample = int(round(
                sample_at_beat(beat_index + subdivision / 4.0)
            ))
            if expected_sample >= total_samples:
                continue
            sample = int(clamp(expected_sample, 0, total_samples - 1))
            grid_samples.append(sample)
            strength, energy = precision_window_values(
                precision,
                sample,
                int(round(0.028 * sample_rate)),
            )
            if strength < 0.08:
                continue
            candidates.append({
                "hitSample": sample,
                "beatIndex": beat_index,
                "subdivision": subdivision,
                "strength": strength,
                "energy": energy,
                "score": strength * 0.78 + energy * 0.22,
            })

    unique = {}
    for candidate in candidates:
        sample = candidate["hitSample"]
        if sample not in unique or candidate["score"] > unique[sample]["score"]:
            unique[sample] = candidate
    candidates = sorted(unique.values(), key=lambda item: item["hitSample"])

    return candidates, beat_samples, sorted(set(grid_samples))


def select_audio_driven_notes(
    candidates,
    beat_samples,
    difficulty,
    sample_rate,
    playable_end_sample,
):
    if difficulty == "EASY":
        allowed = {0}
        minimum_strength = 0.16
        base_target = 2
        minimum_gap = 0.28
    elif difficulty == "NORMAL":
        allowed = {0, 2}
        minimum_strength = 0.12
        base_target = 3
        minimum_gap = 0.18
    else:
        allowed = {0, 1, 2, 3}
        minimum_strength = 0.11
        base_target = 4
        minimum_gap = 0.13

    selected = []
    beat_count = len(beat_samples)
    for bar_start in range(0, beat_count, BEATS_PER_BAR):
        bar_candidates = [
            candidate
            for candidate in candidates
            if bar_start <= candidate["beatIndex"] < bar_start + BEATS_PER_BAR
            and candidate["subdivision"] in allowed
            and candidate["strength"] >= minimum_strength
            and candidate["hitSample"] <= playable_end_sample
        ]
        if not bar_candidates:
            continue
        section_energy = float(np.mean([candidate["energy"] for candidate in bar_candidates]))
        if difficulty == "EASY":
            target = base_target + (1 if section_energy >= 0.92 else 0)
        elif difficulty == "NORMAL":
            target = base_target + (1 if section_energy >= 0.78 else 0)
        else:
            target = base_target + (1 if section_energy >= 0.82 else 0)

        def rhythmic_priority(item):
            subdivision_bonus = {
                0: 0.13,
                2: 0.06,
                1: -0.08,
                3: -0.08,
            }[item["subdivision"]]
            return item["score"] + subdivision_bonus

        ranked = sorted(
            bar_candidates,
            key=rhythmic_priority,
            reverse=True,
        )
        if difficulty == "HARD":
            chosen = []
            decoration_count = 0
            for candidate in ranked:
                is_decoration = candidate["subdivision"] in {1, 3}
                if is_decoration:
                    if decoration_count >= 1 or candidate["strength"] < 0.55:
                        continue
                    decoration_count += 1
                chosen.append(candidate)
                if len(chosen) >= target:
                    break
            selected.extend(chosen)
        else:
            selected.extend(ranked[:target])

    selected.sort(key=lambda item: item["hitSample"])
    filtered = []
    minimum_gap_samples = int(round(minimum_gap * sample_rate))
    for candidate in selected:
        if filtered and candidate["hitSample"] - filtered[-1]["hitSample"] < minimum_gap_samples:
            if candidate["score"] > filtered[-1]["score"]:
                filtered[-1] = candidate
            continue
        filtered.append(candidate)

    cadence_subdivisions = {0} if difficulty == "EASY" else {0, 2}
    cadence_candidates = [
        candidate
        for candidate in candidates
        if candidate["hitSample"] <= playable_end_sample
        and candidate["subdivision"] in cadence_subdivisions
        and candidate["strength"] >= minimum_strength
    ]
    final_cadence = cadence_candidates[-1] if cadence_candidates else (filtered[-1] if filtered else None)
    if final_cadence is not None:
        final_sample = final_cadence["hitSample"]
        filtered = [candidate for candidate in filtered if candidate["hitSample"] <= final_sample]
        if not filtered:
            filtered.append(final_cadence)
        elif filtered[-1]["hitSample"] == final_sample:
            filtered[-1] = final_cadence
        elif final_sample - filtered[-1]["hitSample"] < minimum_gap_samples:
            filtered[-1] = final_cadence
        else:
            filtered.append(final_cadence)

    notes = []
    last_wheel_sample = -10 * sample_rate
    for index, candidate in enumerate(filtered):
        good = index % 2 == 0
        kind = "GoodTap" if good else "BadTap"
        previous_gap = (
            candidate["hitSample"] - filtered[index - 1]["hitSample"]
            if index > 0
            else 10 * sample_rate
        )
        next_gap = (
            filtered[index + 1]["hitSample"] - candidate["hitSample"]
            if index + 1 < len(filtered)
            else 10 * sample_rate
        )
        can_be_wheel = (
            index + 1 < len(filtered)
            and candidate["subdivision"] == 0
            and candidate["strength"] >= 0.72
            and candidate["hitSample"] - last_wheel_sample >= 9 * sample_rate
            and previous_gap >= int(round(0.34 * sample_rate))
            and next_gap >= int(round(0.78 * sample_rate))
        )
        if can_be_wheel:
            kind = "GoodWheelUp" if good else "BadWheelDown"
            last_wheel_sample = candidate["hitSample"]
        notes.append({
            "hitSample": int(candidate["hitSample"]),
            "kind": kind,
            "laneIndex": 1,
        })
    return notes, (int(final_cadence["hitSample"]) if final_cadence is not None else 0)


def build_strict_v6_candidates(
    features,
    precision,
    total_samples,
    sample_rate,
    timing_profile=None,
):
    beat_samples = build_strict_beat_grid(
        features,
        precision,
        total_samples,
        sample_rate,
        timing_profile,
    )
    period_samples = float(features["strict_period_samples"])
    phase_samples = float(features["strict_phase_samples"])
    candidates = []
    grid_samples = []
    for beat_index in range(len(beat_samples)):
        for subdivision in range(4):
            expected_sample = phase_samples + (
                beat_index + subdivision / 4.0
            ) * period_samples
            if expected_sample >= total_samples:
                continue
            sample = int(clamp(round(expected_sample), 0, total_samples - 1))
            grid_samples.append(sample)
            strength, energy = precision_window_values(
                precision,
                sample,
                int(round(0.028 * sample_rate)),
            )
            if strength < 0.08:
                continue
            candidates.append({
                "hitSample": sample,
                "beatIndex": beat_index,
                "subdivision": subdivision,
                "strength": strength,
                "energy": energy,
                "score": strength * 0.78 + energy * 0.22,
            })
    return candidates, beat_samples, sorted(set(grid_samples))


def select_strict_v6_notes(candidates, beat_samples, difficulty, sample_rate):
    if difficulty == "EASY":
        allowed = {0}
        minimum_strength = 0.16
        minimum_gap = 0.28
    elif difficulty == "NORMAL":
        allowed = {0, 2}
        minimum_strength = 0.12
        minimum_gap = 0.18
    else:
        allowed = {0, 1, 2, 3}
        minimum_strength = 0.10
        minimum_gap = 0.10

    selected = []
    for bar_start in range(0, len(beat_samples), BEATS_PER_BAR):
        bar_candidates = [
            candidate
            for candidate in candidates
            if bar_start <= candidate["beatIndex"] < bar_start + BEATS_PER_BAR
            and candidate["subdivision"] in allowed
            and candidate["strength"] >= minimum_strength
        ]
        if not bar_candidates:
            continue

        section_energy = float(np.mean([
            candidate["energy"] for candidate in bar_candidates
        ]))
        if difficulty == "EASY":
            target = 2 + (1 if section_energy >= 0.92 else 0)
        elif difficulty == "NORMAL":
            target = 3 + (1 if section_energy >= 0.78 else 0)
        else:
            target = (
                4
                + (1 if section_energy >= 0.34 else 0)
                + (1 if section_energy >= 1.0 else 0)
            )
        selected.extend(sorted(
            bar_candidates,
            key=lambda candidate: candidate["score"]
            + (0.08 if candidate["subdivision"] == 0 else 0.0),
            reverse=True,
        )[:target])

    selected.sort(key=lambda candidate: candidate["hitSample"])
    filtered = []
    minimum_gap_samples = int(round(minimum_gap * sample_rate))
    for candidate in selected:
        if filtered and candidate["hitSample"] - filtered[-1]["hitSample"] < minimum_gap_samples:
            if candidate["score"] > filtered[-1]["score"]:
                filtered[-1] = candidate
            continue
        filtered.append(candidate)

    notes = []
    last_wheel_sample = -10 * sample_rate
    for index, candidate in enumerate(filtered):
        good = index % 2 == 0
        kind = "GoodTap" if good else "BadTap"
        previous_gap = (
            candidate["hitSample"] - filtered[index - 1]["hitSample"]
            if index > 0
            else 10 * sample_rate
        )
        next_gap = (
            filtered[index + 1]["hitSample"] - candidate["hitSample"]
            if index + 1 < len(filtered)
            else 10 * sample_rate
        )
        can_be_wheel = (
            candidate["subdivision"] == 0
            and candidate["strength"] >= 0.72
            and candidate["hitSample"] - last_wheel_sample >= 9 * sample_rate
            and previous_gap >= int(round(0.34 * sample_rate))
            and next_gap >= int(round(0.78 * sample_rate))
        )
        if can_be_wheel:
            kind = "GoodWheelUp" if good else "BadWheelDown"
            last_wheel_sample = candidate["hitSample"]
        notes.append({
            "hitSample": int(candidate["hitSample"]),
            "kind": kind,
            "laneIndex": 1,
        })
    return notes


def generate_strict_v6_charts(audio, sample_rate, timing_profile=None):
    features = (
        {}
        if timing_profile is not None
        else detect_audio_pulse_grid(audio, sample_rate)
    )
    precision = build_precision_features(audio, sample_rate)
    candidates, beat_samples, grid_samples = build_strict_v6_candidates(
        features,
        precision,
        len(audio),
        sample_rate,
        timing_profile,
    )
    charts = [
        {
            "difficulty": difficulty,
            "notes": select_strict_v6_notes(
                candidates,
                beat_samples,
                difficulty,
                sample_rate,
            ),
        }
        for difficulty in ("EASY", "NORMAL", "HARD")
    ]
    return features, beat_samples, grid_samples, charts


def build_onbeat_candidates(
    features,
    precision,
    total_samples,
    sample_rate,
    timing_profile=None,
    manual_anchor_map=None,
):
    if manual_anchor_map is not None:
        beat_samples, sample_at_beat = build_manual_beat_grid(
            features,
            precision,
            total_samples,
            sample_rate,
            manual_anchor_map,
            timing_profile,
        )
        features["beat_grid_mode"] = "manual_anchors"
    elif timing_profile is not None and timing_profile.get("lock_tempo", False):
        beat_samples = build_strict_beat_grid(
            features,
            precision,
            total_samples,
            sample_rate,
            timing_profile,
        )
        period_samples = float(features["strict_period_samples"])
        phase_samples = float(features["strict_phase_samples"])
        sample_at_beat = lambda beat_position: (
            phase_samples + beat_position * period_samples
        )
        features["auto_timing_anchors"] = []
        features["adaptive_offset_p95_ms"] = 0.0
        features["beat_grid_mode"] = "constant"
    else:
        beat_samples, sample_at_beat = build_adaptive_anchor_beat_grid(
            features,
            precision,
            total_samples,
            sample_rate,
            timing_profile,
        )
        features["beat_grid_mode"] = "adaptive_bar_anchors"

    audit_beat_samples(features, precision, beat_samples, sample_rate)

    local_bps = []
    fallback_period = max(1.0, float(features["strict_period_samples"]))
    for beat_index in range(len(beat_samples)):
        neighboring_periods = []
        if beat_index > 0:
            neighboring_periods.append(
                beat_samples[beat_index] - beat_samples[beat_index - 1]
            )
        if beat_index + 1 < len(beat_samples):
            neighboring_periods.append(
                beat_samples[beat_index + 1] - beat_samples[beat_index]
            )
        local_period = (
            float(np.median(neighboring_periods))
            if neighboring_periods
            else fallback_period
        )
        local_bps.append(sample_rate / max(1.0, local_period))

    candidates = []
    grid_samples = []
    for beat_index, beat_sample in enumerate(beat_samples):
        for subdivision in (0, 2):
            if subdivision == 0:
                sample = int(beat_sample)
            else:
                sample = int(round(sample_at_beat(beat_index + 0.5)))
            if sample < 0 or sample >= total_samples:
                continue

            grid_samples.append(sample)
            strength, energy = precision_window_values(
                precision,
                sample,
                int(round(0.028 * sample_rate)),
            )
            low_strength = precision_window_max(
                precision["low_onset"],
                precision,
                sample,
                int(round(0.028 * sample_rate)),
            )
            beat_in_bar = beat_index % BEATS_PER_BAR
            accent_bonus = 0.0
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
                "localBps": float(local_bps[beat_index]),
                "strength": strength,
                "lowStrength": low_strength,
                "energy": energy,
                "score": strength * 0.72 + energy * 0.28 + accent_bonus,
            })
    return candidates, beat_samples, sorted(set(grid_samples))


def analyze_candidate_equalizer(audio, sample_rate, candidates):
    del audio, sample_rate
    for candidate in candidates:
        low_pulse = float(candidate["lowStrength"])
        broad_pulse = float(candidate["strength"])
        loudness = float(candidate["energy"])
        upper_pulse = max(0.0, broad_pulse - low_pulse * 0.58)
        middle_pulse = max(0.0, broad_pulse * 0.82 - low_pulse * 0.18)
        band_flux = (low_pulse, middle_pulse, upper_pulse)
        band_energy = (
            loudness * (0.58 + low_pulse * 0.20),
            loudness * (0.68 + middle_pulse * 0.16),
            loudness * (0.48 + upper_pulse * 0.22),
        )
        equalizer_flux = (
            low_pulse * 0.46
            + middle_pulse * 0.34
            + upper_pulse * 0.20
        )
        equalizer_energy = loudness
        support = (
            equalizer_flux * 0.52
            + equalizer_energy * 0.23
            + candidate["strength"] * 0.17
            + candidate["energy"] * 0.08
        )
        dominant_band = int(np.argmax(
            np.asarray(band_flux) * 0.72 + np.asarray(band_energy) * 0.28
        ))
        candidate["eqBandEnergy"] = tuple(float(value) for value in band_energy)
        candidate["eqBandFlux"] = tuple(float(value) for value in band_flux)
        candidate["eqFlux"] = equalizer_flux
        candidate["eqEnergy"] = equalizer_energy
        candidate["eqSupport"] = float(support)
        candidate["dominantBand"] = dominant_band


def annotate_pipeline_highlights(candidates):
    bars = {}
    for candidate in candidates:
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        bars.setdefault(bar_index, []).append(candidate)

    records = []
    for bar_index in sorted(bars):
        main_candidates = [
            candidate for candidate in bars[bar_index]
            if candidate["subdivision"] == 0
        ]
        if not main_candidates:
            continue
        records.append({
            "barIndex": bar_index,
            "candidates": bars[bar_index],
            "equalizer": float(
                np.mean([candidate["eqSupport"] for candidate in main_candidates]) * 0.62
                + max(candidate["eqSupport"] for candidate in main_candidates) * 0.38
            ),
            "loudness": float(
                np.mean([candidate["energy"] for candidate in main_candidates]) * 0.62
                + max(candidate["energy"] for candidate in main_candidates) * 0.38
            ),
            "transient": float(
                np.mean([candidate["strength"] for candidate in main_candidates]) * 0.55
                + max(candidate["strength"] for candidate in main_candidates) * 0.45
            ),
            "bps": float(np.median([
                candidate["localBps"] for candidate in main_candidates
            ])),
        })

    if not records:
        return {
            "barCount": 0,
            "highlightBarCount": 0,
            "equalizerRiseBarCount": 0,
            "loudnessRiseBarCount": 0,
            "bpsRiseBarCount": 0,
            "anyTriggerBarCount": 0,
        }

    equalizer_values = np.asarray(
        [record["equalizer"] for record in records], dtype=np.float64
    )
    loudness_values = np.asarray(
        [record["loudness"] for record in records], dtype=np.float64
    )
    transient_values = np.asarray(
        [record["transient"] for record in records], dtype=np.float64
    )
    bps_values = np.asarray(
        [record["bps"] for record in records], dtype=np.float64
    )
    equalizer_rises = np.zeros(len(records), dtype=np.float64)
    loudness_rises = np.zeros(len(records), dtype=np.float64)
    bps_rises = np.zeros(len(records), dtype=np.float64)
    for index in range(1, len(records)):
        baseline_start = max(0, index - 2)
        equalizer_baseline = float(np.mean(equalizer_values[baseline_start:index]))
        loudness_baseline = float(np.mean(loudness_values[baseline_start:index]))
        bps_baseline = float(np.mean(bps_values[baseline_start:index]))
        equalizer_rises[index] = max(
            0.0, equalizer_values[index] - equalizer_baseline
        )
        loudness_rises[index] = max(
            0.0, loudness_values[index] - loudness_baseline
        )
        bps_rises[index] = max(
            0.0,
            (bps_values[index] - bps_baseline) / max(0.01, bps_baseline),
        )

    highlight_scores = (
        normalize_feature(equalizer_values, 90.0) * 0.32
        + normalize_feature(loudness_values, 90.0) * 0.38
        + normalize_feature(transient_values, 90.0) * 0.20
        + normalize_feature(bps_values, 90.0) * 0.10
    )

    def positive_rise_threshold(values, percentile, minimum):
        positive = values[values > 1e-9]
        if len(positive) == 0:
            return float("inf")
        return max(minimum, float(np.percentile(positive, percentile)))

    highlight_threshold = max(
        1e-6, float(np.percentile(highlight_scores, 72.0))
    )
    equalizer_rise_threshold = positive_rise_threshold(
        equalizer_rises, 60.0, 0.030
    )
    loudness_rise_threshold = positive_rise_threshold(
        loudness_rises, 60.0, 0.034
    )
    bps_rise_threshold = positive_rise_threshold(
        bps_rises, 55.0, 0.0012
    )

    reason_counts = {
        "highlight": 0,
        "equalizer": 0,
        "loudness": 0,
        "bps": 0,
        "any": 0,
    }
    for index, record in enumerate(records):
        highlight_trigger = highlight_scores[index] >= highlight_threshold
        equalizer_trigger = equalizer_rises[index] >= equalizer_rise_threshold
        loudness_trigger = loudness_rises[index] >= loudness_rise_threshold
        bps_trigger = bps_rises[index] >= bps_rise_threshold
        ratios = [highlight_scores[index] / highlight_threshold]
        ratios.append(
            equalizer_rises[index] / equalizer_rise_threshold
            if np.isfinite(equalizer_rise_threshold) else 0.0
        )
        ratios.append(
            loudness_rises[index] / loudness_rise_threshold
            if np.isfinite(loudness_rise_threshold) else 0.0
        )
        ratios.append(
            bps_rises[index] / bps_rise_threshold
            if np.isfinite(bps_rise_threshold) else 0.0
        )
        trigger_score = float(max(ratios))
        any_trigger = (
            highlight_trigger
            or equalizer_trigger
            or loudness_trigger
            or bps_trigger
        )
        reason_counts["highlight"] += int(highlight_trigger)
        reason_counts["equalizer"] += int(equalizer_trigger)
        reason_counts["loudness"] += int(loudness_trigger)
        reason_counts["bps"] += int(bps_trigger)
        reason_counts["any"] += int(any_trigger)
        for candidate in record["candidates"]:
            candidate["highlightScore"] = float(highlight_scores[index])
            candidate["halfBeatTriggerScore"] = trigger_score
            candidate["highlightTrigger"] = bool(highlight_trigger)
            candidate["equalizerRiseTrigger"] = bool(equalizer_trigger)
            candidate["loudnessRiseTrigger"] = bool(loudness_trigger)
            candidate["bpsRiseTrigger"] = bool(bps_trigger)

    return {
        "barCount": len(records),
        "highlightBarCount": reason_counts["highlight"],
        "equalizerRiseBarCount": reason_counts["equalizer"],
        "loudnessRiseBarCount": reason_counts["loudness"],
        "bpsRiseBarCount": reason_counts["bps"],
        "anyTriggerBarCount": reason_counts["any"],
        "highlightThreshold": round(highlight_threshold, 6),
        "equalizerRiseThreshold": round(equalizer_rise_threshold, 6)
        if np.isfinite(equalizer_rise_threshold) else 0.0,
        "loudnessRiseThreshold": round(loudness_rise_threshold, 6)
        if np.isfinite(loudness_rise_threshold) else 0.0,
        "bpsRiseThreshold": round(bps_rise_threshold, 6)
        if np.isfinite(bps_rise_threshold) else 0.0,
    }


def estimate_onbeat_bpm(candidates, sample_rate):
    main_candidates = sorted(
        (
            candidate
            for candidate in candidates
            if candidate["subdivision"] == 0
        ),
        key=lambda candidate: candidate["beatIndex"],
    )
    periods = []
    for first, second in zip(main_candidates, main_candidates[1:]):
        beat_delta = second["beatIndex"] - first["beatIndex"]
        sample_delta = second["hitSample"] - first["hitSample"]
        if beat_delta > 0 and sample_delta > 0:
            periods.append(sample_delta / beat_delta)
    if not periods:
        return 0.0
    return 60.0 * sample_rate / float(np.median(periods))


def pipeline_bps_priority(candidate):
    main_beat_bonus = 0.38 if candidate["subdivision"] == 0 else 0.0
    beat_in_bar = candidate["beatIndex"] % BEATS_PER_BAR
    accent_bonus = 0.24 if beat_in_bar == 0 else 0.09 if beat_in_bar == 2 else 0.0
    return (
        candidate["eqSupport"] * 0.66
        + candidate["eqFlux"] * 0.18
        + candidate["strength"] * 0.16
        + main_beat_bonus
        + accent_bonus
    )


def pipeline_loudness_priority(candidate):
    main_beat_bonus = 0.24 if candidate["subdivision"] == 0 else 0.0
    downbeat_bonus = (
        0.18
        if candidate["subdivision"] == 0
        and candidate["beatIndex"] % BEATS_PER_BAR == 0
        else 0.0
    )
    return (
        candidate["energy"] * 0.52
        + candidate["eqSupport"] * 0.34
        + candidate["strength"] * 0.14
        + main_beat_bonus
        + downbeat_bonus
    )


def pipeline_target_count(settings, bar_candidates, highlight_active=False):
    main_candidates = [
        candidate for candidate in bar_candidates
        if candidate["subdivision"] == 0
    ]
    if not main_candidates:
        return 0, 0.0, 0.0
    local_bps = float(np.median([
        candidate["localBps"] for candidate in main_candidates
    ]))
    bar_beats = len(main_candidates)
    target_nps = min(
        settings["maximum_notes_per_second"],
        local_bps * settings["notes_per_beat"],
    )
    bar_duration = bar_beats / max(0.01, local_bps)
    target = int(round(target_nps * bar_duration))
    if highlight_active:
        target += int(settings["highlight_target_bonus"])
    minimum = int(math.ceil(
        settings["minimum_notes_per_bar"] * bar_beats / BEATS_PER_BAR
    ))
    maximum = int(math.ceil(
        settings["maximum_notes_per_bar"] * bar_beats / BEATS_PER_BAR
    ))
    target = int(clamp(target, minimum, maximum))
    return target, local_bps, target_nps


def pipeline_wheel_score(candidate):
    return (
        candidate["eqFlux"] * 0.34
        + candidate["energy"] * 0.31
        + candidate["eqSupport"] * 0.20
        + candidate["strength"] * 0.10
        + min(2.0, candidate.get("halfBeatTriggerScore", 0.0)) * 0.05
        + (
            0.10
            if candidate["beatIndex"] % BEATS_PER_BAR == 0
            else 0.0
        )
    )


def build_snowflake_pattern_profile(beat_samples, charts):
    sample_slots = {}
    for beat_index, sample in enumerate(beat_samples):
        sample_slots[int(sample)] = (
            beat_index // BEATS_PER_BAR,
            (beat_index % BEATS_PER_BAR) * 2,
        )
        if beat_index + 1 < len(beat_samples):
            half_sample = midpoint_sample(sample, beat_samples[beat_index + 1])
            sample_slots[int(half_sample)] = (
                beat_index // BEATS_PER_BAR,
                (beat_index % BEATS_PER_BAR) * 2 + 1,
            )

    bar_count = int(math.ceil(len(beat_samples) / BEATS_PER_BAR))
    profile = {}
    for chart in charts:
        masks = [0] * bar_count
        wheel_slot_counts = {}
        for note in chart["notes"]:
            location = sample_slots.get(int(note["hitSample"]))
            if location is None:
                continue
            bar_index, slot = location
            masks[bar_index] |= 1 << slot
            if "Wheel" in note.get("kind", ""):
                wheel_slot_counts[slot] = wheel_slot_counts.get(slot, 0) + 1

        mask_counts = {}
        masks_by_count = {}
        for mask in masks:
            mask_counts[mask] = mask_counts.get(mask, 0) + 1
        for mask, frequency in mask_counts.items():
            masks_by_count.setdefault(mask.bit_count(), []).append(
                (mask, frequency)
            )
        for patterns in masks_by_count.values():
            patterns.sort(key=lambda item: (-item[1], item[0]))

        transition_counts = {}
        for first, second in zip(masks, masks[1:]):
            key = (first, second)
            transition_counts[key] = transition_counts.get(key, 0) + 1
        maximum_wheel_frequency = max(wheel_slot_counts.values(), default=1)
        profile[chart["difficulty"]] = {
            "masks_by_count": masks_by_count,
            "transition_counts": transition_counts,
            "wheel_slot_weights": {
                slot: count / maximum_wheel_frequency
                for slot, count in wheel_slot_counts.items()
            },
            "bar_count": bar_count,
            "unique_pattern_count": len(mask_counts),
        }
    return profile


def pipeline_wheel_rank_score(candidate, wheel_slot_weights=None):
    slot = (candidate["beatIndex"] % BEATS_PER_BAR) * 2
    reference_bonus = (
        wheel_slot_weights.get(slot, 0.0) * 0.12
        if wheel_slot_weights else 0.0
    )
    return pipeline_wheel_score(candidate) + reference_bonus


def choose_pipeline_wheel_samples(
    selected,
    settings,
    sample_rate,
    wheel_slot_weights=None,
):
    selected = sorted(selected, key=lambda candidate: candidate["hitSample"])
    if len(selected) < 3:
        return set(), selected, 0, 0.0, []

    lead_clearance = int(round(
        settings["wheel_lead_clearance_seconds"] * sample_rate
    ))
    recovery_clearance = int(round(
        settings["wheel_recovery_clearance_seconds"] * sample_rate
    ))
    first_sample = selected[0]["hitSample"]
    last_sample = selected[-1]["hitSample"]
    eligible = [
        candidate for candidate in selected
        if candidate["subdivision"] == 0
        and candidate["eqSupport"] >= 0.28
        and candidate["energy"] >= 0.20
        and candidate["hitSample"] - first_sample >= lead_clearance
        and last_sample - candidate["hitSample"] >= recovery_clearance
    ]
    if not eligible:
        return set(), selected, 0, 0.0, []

    scores = np.asarray(
        [
            pipeline_wheel_rank_score(candidate, wheel_slot_weights)
            for candidate in eligible
        ],
        dtype=np.float64,
    )
    score_threshold = float(np.percentile(
        scores,
        settings["wheel_score_percentile"],
    ))
    ranked = sorted(
        (
            candidate for candidate in eligible
            if pipeline_wheel_rank_score(candidate, wheel_slot_weights)
            >= score_threshold
        ),
        key=lambda candidate: pipeline_wheel_rank_score(
            candidate,
            wheel_slot_weights,
        ),
        reverse=True,
    )

    wheel_samples = set()
    reserved_intervals = []
    for candidate in ranked:
        sample = candidate["hitSample"]
        interval = (sample - lead_clearance, sample + recovery_clearance)
        if any(
            interval[0] < reserved_end and interval[1] > reserved_start
            for reserved_start, reserved_end in reserved_intervals
        ):
            continue
        wheel_samples.add(sample)
        reserved_intervals.append(interval)

    kept = []
    removed_count = 0
    for candidate in selected:
        sample = candidate["hitSample"]
        if sample in wheel_samples:
            kept.append(candidate)
            continue
        if (
            settings.get("preserve_main_beats_around_wheels", False)
            and candidate["subdivision"] == 0
        ):
            kept.append(candidate)
            continue
        if any(
            reserved_start < sample < reserved_end
            for reserved_start, reserved_end in reserved_intervals
        ):
            removed_count += 1
            continue
        kept.append(candidate)
    if wheel_samples:
        score_threshold = min(
            pipeline_wheel_rank_score(candidate, wheel_slot_weights)
            for candidate in eligible
            if candidate["hitSample"] in wheel_samples
        )
    return (
        wheel_samples,
        kept,
        removed_count,
        score_threshold,
        reserved_intervals,
    )


def apply_snowflake_pattern_grammar(
    selected,
    candidate_pool,
    wheel_samples,
    reserved_intervals,
    pattern_profile,
    preserve_main_beats_around_wheels=False,
):
    selected_by_sample = {
        candidate["hitSample"]: candidate for candidate in selected
    }
    selected_by_bar = {}
    for candidate in selected_by_sample.values():
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        selected_by_bar.setdefault(bar_index, []).append(candidate)

    candidates_by_bar_slot = {}
    for candidate in candidate_pool:
        sample = candidate["hitSample"]
        inside_wheel_clearance = any(
            start < sample < end for start, end in reserved_intervals
        )
        if (
            sample not in wheel_samples
            and inside_wheel_clearance
            and not (
                preserve_main_beats_around_wheels
                and candidate["subdivision"] == 0
                and sample in selected_by_sample
            )
        ):
            continue
        if not (
            sample in selected_by_sample
            or sample in wheel_samples
            or candidate["energy"] >= 0.055
            or candidate["eqSupport"] >= 0.10
            or candidate["strength"] >= 0.08
        ):
            continue
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        slot = (candidate["beatIndex"] % BEATS_PER_BAR) * 2
        if candidate["subdivision"] == 2:
            slot += 1
        candidates_by_bar_slot.setdefault(bar_index, {})[slot] = candidate

    matched_bar_count = 0
    repositioned_bar_count = 0
    previous_mask = None
    for bar_index in sorted(selected_by_bar):
        current_candidates = selected_by_bar[bar_index]
        target_count = len(current_candidates)
        if target_count <= 0:
            continue
        slot_candidates = candidates_by_bar_slot.get(bar_index, {})
        current_mask = 0
        protected_wheel_mask = 0
        for candidate in current_candidates:
            slot = (candidate["beatIndex"] % BEATS_PER_BAR) * 2
            if candidate["subdivision"] == 2:
                slot += 1
            current_mask |= 1 << slot
            if (
                candidate["hitSample"] in wheel_samples
                or (
                    preserve_main_beats_around_wheels
                    and candidate["subdivision"] == 0
                    and any(
                        start < candidate["hitSample"] < end
                        for start, end in reserved_intervals
                    )
                )
            ):
                protected_wheel_mask |= 1 << slot

        patterns = pattern_profile["masks_by_count"].get(target_count, [])
        valid_patterns = []
        for mask, frequency in patterns:
            if mask & protected_wheel_mask != protected_wheel_mask:
                continue
            slots = [slot for slot in range(8) if mask & (1 << slot)]
            if all(slot in slot_candidates for slot in slots):
                valid_patterns.append((mask, frequency, slots))
        if not valid_patterns:
            previous_mask = current_mask
            continue

        raw_slot_scores = {
            slot: (
                pipeline_bps_priority(candidate) * 0.58
                + pipeline_loudness_priority(candidate) * 0.42
            )
            for slot, candidate in slot_candidates.items()
        }
        minimum_score = min(raw_slot_scores.values(), default=0.0)
        maximum_score = max(raw_slot_scores.values(), default=1.0)
        score_span = max(1e-6, maximum_score - minimum_score)
        maximum_frequency = max(frequency for _, frequency, _ in valid_patterns)
        transition_counts = pattern_profile["transition_counts"]
        maximum_transition = max(
            (
                count for (first, _), count in transition_counts.items()
                if first == previous_mask
            ),
            default=1,
        )

        best_mask = current_mask
        best_score = -1e9
        for mask, frequency, slots in valid_patterns:
            audio_score = sum(
                (raw_slot_scores[slot] - minimum_score) / score_span
                for slot in slots
            ) / max(1, len(slots))
            frequency_score = frequency / max(1, maximum_frequency)
            transition_score = (
                transition_counts.get((previous_mask, mask), 0)
                / max(1, maximum_transition)
                if previous_mask is not None else 0.0
            )
            overlap_score = (
                (mask & current_mask).bit_count() / max(1, target_count)
            )
            score = (
                audio_score * 0.60
                + frequency_score * 0.24
                + transition_score * 0.11
                + overlap_score * 0.05
            )
            if score > best_score or (
                math.isclose(score, best_score) and mask < best_mask
            ):
                best_score = score
                best_mask = mask

        matched_bar_count += 1
        if best_mask != current_mask:
            repositioned_bar_count += 1
            for candidate in current_candidates:
                selected_by_sample.pop(candidate["hitSample"], None)
            for slot in range(8):
                if best_mask & (1 << slot):
                    candidate = slot_candidates[slot]
                    selected_by_sample[candidate["hitSample"]] = candidate
        previous_mask = best_mask

    patterned = sorted(
        selected_by_sample.values(),
        key=lambda candidate: candidate["hitSample"],
    )
    return patterned, {
        "matched_bar_count": matched_bar_count,
        "repositioned_bar_count": repositioned_bar_count,
    }


def build_pipeline_notes(selected, settings, wheel_samples):
    selected = sorted(selected, key=lambda candidate: candidate["hitSample"])
    wheel_samples = {
        sample for sample in wheel_samples
        if any(candidate["hitSample"] == sample for candidate in selected)
    }
    if selected:
        wheel_samples.discard(selected[-1]["hitSample"])
    maximum_run = settings["color_max_run_length"]
    previous_good = None
    run_length = 0
    notes = []
    for index, candidate in enumerate(selected):
        band_energy = candidate["eqBandEnergy"]
        band_flux = candidate["eqBandFlux"]
        low_value = band_flux[0] * 0.72 + band_energy[0] * 0.28
        high_value = band_flux[2] * 0.72 + band_energy[2] * 0.28
        if abs(low_value - high_value) < 0.055:
            good = (candidate["beatIndex"] + candidate["subdivision"] + index) % 2 == 0
        else:
            good = low_value >= high_value
        if good == previous_good and run_length >= maximum_run:
            good = not good
        run_length = run_length + 1 if good == previous_good else 1
        previous_good = good
        is_wheel = candidate["hitSample"] in wheel_samples
        if is_wheel:
            kind = "GoodWheelUp" if good else "BadWheelDown"
        else:
            kind = "GoodTap" if good else "BadTap"
        notes.append({
            "hitSample": int(candidate["hitSample"]),
            "kind": kind,
            "laneIndex": 1,
        })
    if notes and "Wheel" in notes[-1]["kind"]:
        notes[-1]["kind"] = (
            "GoodTap" if notes[-1]["kind"].startswith("Good") else "BadTap"
        )
    return notes, len(wheel_samples)


def select_ordered_pipeline_chart(
    candidates,
    difficulty,
    sample_rate,
    playable_end_sample=None,
    pattern_profile=None,
):
    settings = ORDERED_PIPELINE_SETTINGS[difficulty]
    candidate_by_sample = {
        candidate["hitSample"]: candidate
        for candidate in candidates
        if playable_end_sample is None
        or candidate["hitSample"] <= playable_end_sample
    }
    eligible_candidates = sorted(
        candidate_by_sample.values(),
        key=lambda candidate: candidate["hitSample"],
    )
    main_candidates = [
        candidate for candidate in eligible_candidates
        if candidate["subdivision"] == 0
    ]
    half_candidates = [
        candidate for candidate in eligible_candidates
        if candidate["subdivision"] == 2
    ]

    groove_half_score_threshold = float("inf")
    groove_half_relief_threshold = float("inf")
    groove_half_samples = set()
    groove_half_relief_samples = set()
    groove_half_fallback_samples = set()
    if settings.get("allows_groove_half_beats", False) and half_candidates:
        groove_scores = np.asarray(
            [pipeline_bps_priority(candidate) for candidate in half_candidates],
            dtype=np.float64,
        )
        groove_half_score_threshold = float(np.percentile(
            groove_scores,
            settings["groove_half_add_percentile"],
        ))
        groove_half_relief_threshold = float(np.percentile(
            groove_scores,
            settings["groove_half_relief_percentile"],
        ))

        def collect_groove_half_samples(score_threshold, threshold_scale):
            candidates_by_bar = {}
            for candidate in half_candidates:
                if pipeline_bps_priority(candidate) < score_threshold:
                    continue
                if (
                    candidate["eqSupport"]
                    < settings["groove_half_minimum_support"] * threshold_scale
                ):
                    continue
                pulse_threshold = (
                    settings["groove_half_minimum_pulse"] * threshold_scale
                )
                energy_threshold = (
                    settings["groove_half_minimum_energy"] * threshold_scale
                )
                if not (
                    candidate["eqFlux"] >= pulse_threshold
                    or candidate["strength"] >= pulse_threshold
                    or candidate["energy"] >= energy_threshold
                ):
                    continue
                bar_index = candidate["beatIndex"] // BEATS_PER_BAR
                candidates_by_bar.setdefault(bar_index, []).append(candidate)

            selected_samples = set()
            maximum_per_bar = int(
                settings["maximum_groove_half_beats_per_bar"]
            )
            for bar_candidates in candidates_by_bar.values():
                ranked = sorted(
                    bar_candidates,
                    key=pipeline_bps_priority,
                    reverse=True,
                )
                selected_samples.update(
                    candidate["hitSample"]
                    for candidate in ranked[:maximum_per_bar]
                )
            return selected_samples

        groove_half_samples = collect_groove_half_samples(
            groove_half_score_threshold,
            1.0,
        )
        groove_half_relief_samples = collect_groove_half_samples(
            groove_half_relief_threshold,
            0.80,
        )

        fallback_candidates_by_bar = {}
        for candidate in half_candidates:
            if (
                candidate["eqSupport"]
                < settings["groove_half_fallback_minimum_support"]
            ):
                continue
            if not (
                candidate["eqFlux"]
                >= settings["groove_half_fallback_minimum_pulse"]
                or candidate["strength"]
                >= settings["groove_half_fallback_minimum_pulse"]
                or candidate["energy"]
                >= settings["groove_half_fallback_minimum_energy"]
            ):
                continue
            bar_index = candidate["beatIndex"] // BEATS_PER_BAR
            fallback_candidates_by_bar.setdefault(bar_index, []).append(
                candidate
            )
        for bar_candidates in fallback_candidates_by_bar.values():
            best = max(bar_candidates, key=pipeline_bps_priority)
            groove_half_fallback_samples.add(best["hitSample"])

    def is_highlight_half_candidate(candidate):
        return (
            settings["allows_half_beats"]
            and candidate["subdivision"] == 2
            and candidate.get("halfBeatTriggerScore", 0.0)
            >= settings["half_trigger_minimum"]
            and (
                candidate.get("highlightTrigger", False)
                or candidate.get("equalizerRiseTrigger", False)
                or candidate.get("loudnessRiseTrigger", False)
                or candidate.get("bpsRiseTrigger", False)
            )
        )

    def is_groove_half_candidate(candidate):
        return candidate["hitSample"] in groove_half_samples

    def is_relief_groove_half_candidate(candidate):
        sample = candidate["hitSample"]
        return (
            sample in groove_half_relief_samples
            or sample in groove_half_fallback_samples
        )

    def has_bps_add_support(candidate):
        if candidate["subdivision"] == 0:
            return candidate["eqSupport"] >= main_threshold * 0.62
        if is_groove_half_candidate(candidate):
            return (
                candidate["eqSupport"]
                >= settings.get("groove_half_minimum_support", 0.13)
            )
        return candidate["eqSupport"] >= max(0.13, half_threshold * 0.55)

    triggered_half_candidates = [
        candidate for candidate in half_candidates
        if is_highlight_half_candidate(candidate)
    ]
    estimated_bpm = estimate_onbeat_bpm(main_candidates, sample_rate)
    bars = {}
    for candidate in eligible_candidates:
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        bars.setdefault(bar_index, []).append(candidate)

    # 1. BPM creates the main-beat foundation.
    selected_samples = {candidate["hitSample"] for candidate in main_candidates}
    bpm_result_count = len(selected_samples)

    # 2. Equalizer support removes weak main beats and adds natural half-beat accents.
    main_support = np.asarray(
        [candidate["eqSupport"] for candidate in main_candidates],
        dtype=np.float64,
    )
    main_threshold = (
        float(np.percentile(
            main_support,
            settings["eq_main_remove_percentile"],
        ))
        if len(main_support) else 0.0
    )
    eq_rejected_samples = set()
    before_equalizer = set(selected_samples)
    for candidate in main_candidates:
        beat_in_bar = candidate["beatIndex"] % BEATS_PER_BAR
        protection = 0.44 if beat_in_bar == 0 else 0.72 if beat_in_bar == 2 else 1.0
        if candidate["eqSupport"] < main_threshold * protection:
            selected_samples.discard(candidate["hitSample"])
            eq_rejected_samples.add(candidate["hitSample"])

    half_threshold = float("inf")
    if triggered_half_candidates:
        half_threshold = float(np.percentile(
            [candidate["eqSupport"] for candidate in triggered_half_candidates],
            settings["eq_half_add_percentile"],
        ))
        equalizer_half_by_bar = {}
        for candidate in triggered_half_candidates:
            if not (
                candidate.get("highlightTrigger", False)
                or candidate.get("equalizerRiseTrigger", False)
            ):
                continue
            if (
                candidate["eqSupport"] >= max(0.20, half_threshold)
                and (
                    candidate["eqFlux"] >= 0.11
                    or candidate["eqEnergy"] >= 0.38
                )
            ):
                bar_index = candidate["beatIndex"] // BEATS_PER_BAR
                equalizer_half_by_bar.setdefault(bar_index, []).append(candidate)
        for bar_candidates in equalizer_half_by_bar.values():
            ranked = sorted(
                bar_candidates,
                key=pipeline_bps_priority,
                reverse=True,
            )
            for candidate in ranked[:settings["maximum_half_beats_per_highlight_bar"]]:
                selected_samples.add(candidate["hitSample"])
    eq_removed_count = len(before_equalizer - selected_samples)
    eq_added_count = len(selected_samples - before_equalizer)
    equalizer_result_count = len(selected_samples)

    # 3. Local BPS controls the playable note rate without imposing a gap rule.
    before_bps = set(selected_samples)
    bps_targets = []
    local_bps_values = []
    target_nps_values = []
    for bar_index in sorted(bars):
        bar_candidates = bars[bar_index]
        highlight_active = any(
            is_highlight_half_candidate(candidate)
            for candidate in bar_candidates
        )
        allowed = [
            candidate for candidate in bar_candidates
            if candidate["subdivision"] == 0
            or candidate["hitSample"] in selected_samples
            or is_groove_half_candidate(candidate)
            or (
                is_highlight_half_candidate(candidate)
                and (
                    candidate.get("highlightTrigger", False)
                    or candidate.get("bpsRiseTrigger", False)
                )
            )
        ]
        target, local_bps, target_nps = pipeline_target_count(
            settings,
            bar_candidates,
            highlight_active,
        )
        if target <= 0:
            continue
        bps_targets.append(target)
        local_bps_values.append(local_bps)
        target_nps_values.append(target_nps)
        bar_samples = {
            candidate["hitSample"] for candidate in allowed
            if candidate["hitSample"] in selected_samples
        }
        if len(bar_samples) > target:
            ranked = sorted(
                (candidate for candidate in allowed if candidate["hitSample"] in bar_samples),
                key=pipeline_bps_priority,
            )
            for candidate in ranked[:len(bar_samples) - target]:
                selected_samples.discard(candidate["hitSample"])
        elif len(bar_samples) < target:
            addable = [
                candidate for candidate in allowed
                if candidate["hitSample"] not in selected_samples
                and candidate["hitSample"] not in eq_rejected_samples
                and has_bps_add_support(candidate)
            ]
            addable.sort(key=pipeline_bps_priority, reverse=True)
            half_count = sum(
                candidate["subdivision"] == 2
                and candidate["hitSample"] in selected_samples
                for candidate in bar_candidates
            )
            half_limit = (
                settings["maximum_half_beats_per_highlight_bar"]
                if highlight_active else int(
                    settings.get("maximum_groove_half_beats_per_bar", 0)
                )
            )
            additions_needed = target - len(bar_samples)
            additions_made = 0
            for candidate in addable:
                if additions_made >= additions_needed:
                    break
                if candidate["subdivision"] == 2:
                    if half_count >= half_limit:
                        continue
                    half_count += 1
                selected_samples.add(candidate["hitSample"])
                additions_made += 1

    streak_relief_samples = set()
    maximum_main_only_bars = int(
        settings.get("maximum_main_only_bars_before_relief", 0)
    )
    if maximum_main_only_bars > 0 and (
        groove_half_relief_samples or groove_half_fallback_samples
    ):
        main_only_run = 0
        for bar_index in sorted(bars):
            bar_candidates = bars[bar_index]
            selected_in_bar = [
                candidate for candidate in bar_candidates
                if candidate["hitSample"] in selected_samples
            ]
            selected_main_count = sum(
                candidate["subdivision"] == 0
                for candidate in selected_in_bar
            )
            selected_half_count = sum(
                candidate["subdivision"] == 2
                for candidate in selected_in_bar
            )
            if selected_main_count < 3:
                main_only_run = 0
                continue
            if selected_half_count > 0:
                main_only_run = 0
                continue

            main_only_run += 1
            if main_only_run <= maximum_main_only_bars:
                continue
            if len(selected_in_bar) >= settings["maximum_notes_per_bar"]:
                continue

            addable = [
                candidate for candidate in bar_candidates
                if is_relief_groove_half_candidate(candidate)
                and candidate["hitSample"] not in selected_samples
                and candidate["hitSample"] not in eq_rejected_samples
            ]
            if not addable:
                continue
            candidate = max(addable, key=pipeline_bps_priority)
            selected_samples.add(candidate["hitSample"])
            streak_relief_samples.add(candidate["hitSample"])
            main_only_run = 0
    bps_removed_count = len(before_bps - selected_samples)
    bps_added_count = len(selected_samples - before_bps)
    bps_result_count = len(selected_samples)

    # 4. Loudness thins quiet bars and reinforces loud bars after BPS balancing.
    before_loudness = set(selected_samples)
    for bar_index in sorted(bars):
        bar_candidates = bars[bar_index]
        main_in_bar = [
            candidate for candidate in bar_candidates
            if candidate["subdivision"] == 0
        ]
        if not main_in_bar:
            continue
        bar_loudness = float(
            np.mean([candidate["energy"] for candidate in main_in_bar]) * 0.62
            + max(candidate["energy"] for candidate in main_in_bar) * 0.38
        )
        selected_in_bar = [
            candidate for candidate in bar_candidates
            if candidate["hitSample"] in selected_samples
        ]
        loudness_expansion = any(
            candidate.get("highlightTrigger", False)
            or candidate.get("loudnessRiseTrigger", False)
            for candidate in bar_candidates
        )
        if (
            max(candidate["energy"] for candidate in main_in_bar) < 0.055
            and max(candidate["eqSupport"] for candidate in main_in_bar) < 0.10
        ):
            for candidate in selected_in_bar:
                selected_samples.discard(candidate["hitSample"])
            continue
        if bar_loudness < settings["quiet_loudness"] and len(selected_in_bar) > 1:
            remove_count = int(settings["quiet_remove_count"])
            if (
                remove_count > 0
                and bar_loudness < settings["quiet_loudness"] * 0.48
            ):
                remove_count += 1
            if remove_count > 0:
                removable = sorted(selected_in_bar, key=pipeline_loudness_priority)
                for candidate in removable[:min(remove_count, len(removable) - 1)]:
                    selected_samples.discard(candidate["hitSample"])
        elif (
            loudness_expansion
            and bar_loudness
            >= max(
                settings["quiet_loudness"] * 1.35,
                settings["loud_loudness"] * 0.70,
            )
        ):
            allowed = [
                candidate for candidate in bar_candidates
                if candidate["subdivision"] == 0
                or candidate["hitSample"] in selected_samples
                or (
                    is_highlight_half_candidate(candidate)
                    and (
                        candidate.get("highlightTrigger", False)
                        or candidate.get("loudnessRiseTrigger", False)
                    )
                )
            ]
            addable = [
                candidate for candidate in allowed
                if candidate["hitSample"] not in selected_samples
                and candidate["hitSample"] not in eq_rejected_samples
                and candidate["eqSupport"] >= 0.16
            ]
            addable.sort(key=pipeline_loudness_priority, reverse=True)
            add_count = 2 if (
                difficulty == "HARD"
                and bar_loudness >= settings["loud_loudness"] * 1.45
            ) else 1
            half_count = sum(
                candidate["subdivision"] == 2
                and candidate["hitSample"] in selected_samples
                for candidate in bar_candidates
            )
            additions_made = 0
            for candidate in addable:
                if additions_made >= add_count:
                    break
                if candidate["subdivision"] == 2:
                    if (
                        half_count
                        >= settings["maximum_half_beats_per_highlight_bar"]
                    ):
                        continue
                    half_count += 1
                selected_samples.add(candidate["hitSample"])
                additions_made += 1
    loudness_removed_count = len(before_loudness - selected_samples)
    loudness_added_count = len(selected_samples - before_loudness)

    selected = [
        candidate_by_sample[sample]
        for sample in sorted(selected_samples)
    ]
    pre_wheel_count = len(selected)
    difficulty_pattern_profile = (
        pattern_profile.get(difficulty)
        if pattern_profile is not None else None
    )
    wheel_slot_weights = (
        difficulty_pattern_profile["wheel_slot_weights"]
        if difficulty_pattern_profile is not None else None
    )
    (
        wheel_samples,
        selected,
        wheel_clearance_removed_count,
        wheel_score_threshold,
        reserved_wheel_intervals,
    ) = choose_pipeline_wheel_samples(
        selected,
        settings,
        sample_rate,
        wheel_slot_weights,
    )
    wheel_clearance_result_count = len(selected)
    reference_candidate_pool = [
        candidate for candidate in eligible_candidates
        if candidate["subdivision"] == 0
        or is_highlight_half_candidate(candidate)
        or is_relief_groove_half_candidate(candidate)
    ]
    pattern_stats = {
        "matched_bar_count": 0,
        "repositioned_bar_count": 0,
    }
    if difficulty_pattern_profile is not None:
        selected, pattern_stats = apply_snowflake_pattern_grammar(
            selected,
            reference_candidate_pool,
            wheel_samples,
            reserved_wheel_intervals,
            difficulty_pattern_profile,
            settings.get("preserve_main_beats_around_wheels", False),
        )
    notes, wheel_count = build_pipeline_notes(
        selected,
        settings,
        wheel_samples,
    )
    burst_note_count = sum(
        candidate["subdivision"] == 2 for candidate in selected
    )
    eligible_half_bar_indices = {
        candidate["beatIndex"] // BEATS_PER_BAR
        for candidate in triggered_half_candidates
    }
    selected_half_candidates = [
        candidate for candidate in selected
        if candidate["subdivision"] == 2
    ]
    selected_groove_half_candidates = [
        candidate for candidate in selected_half_candidates
        if not is_highlight_half_candidate(candidate)
    ]
    selected_half_bar_indices = {
        candidate["beatIndex"] // BEATS_PER_BAR
        for candidate in selected_half_candidates
    }
    actual_density = len(selected) / max(1, len(main_candidates)) * 100.0
    return {
        "difficulty": difficulty,
        "actualDensityPercent": round(actual_density, 3),
        "activeBeatCount": len(main_candidates),
        "halfBeatAllowed": settings["allows_half_beats"],
        "noteSpeedWorldUnitsPerSecond": settings["note_speed"],
        "colorRunLength": settings["color_max_run_length"],
        "colorPattern": "equalizer_dominant_band",
        "burstNoteCount": burst_note_count,
        "quarterBurstNoteCount": 0,
        "halfBeatSelectionPolicy": (
            "highlight_or_equalizer_rise_or_loudness_rise_or_bps_rise_or_audio_supported_groove"
            if settings.get("allows_groove_half_beats", False)
            else "highlight_or_equalizer_rise_or_loudness_rise_or_bps_rise_only"
        ),
        "maximumHalfBeatsPerHighlightBar": settings[
            "maximum_half_beats_per_highlight_bar"
        ],
        "highlightTargetBonus": settings["highlight_target_bonus"],
        "halfBeatEligibleBarCount": len(eligible_half_bar_indices),
        "halfBeatSelectedBarCount": len(selected_half_bar_indices),
        "halfBeatHighlightNoteCount": sum(
            candidate.get("highlightTrigger", False)
            for candidate in selected_half_candidates
        ),
        "halfBeatEqualizerRiseNoteCount": sum(
            candidate.get("equalizerRiseTrigger", False)
            for candidate in selected_half_candidates
        ),
        "halfBeatLoudnessRiseNoteCount": sum(
            candidate.get("loudnessRiseTrigger", False)
            for candidate in selected_half_candidates
        ),
        "halfBeatBpsRiseNoteCount": sum(
            candidate.get("bpsRiseTrigger", False)
            for candidate in selected_half_candidates
        ),
        "grooveHalfBeatAllowed": settings.get(
            "allows_groove_half_beats", False
        ),
        "grooveHalfBeatScorePercentile": settings.get(
            "groove_half_add_percentile", 0.0
        ),
        "grooveHalfBeatScoreThreshold": round(
            groove_half_score_threshold, 6
        ) if np.isfinite(groove_half_score_threshold) else 0.0,
        "grooveHalfBeatReliefPercentile": settings.get(
            "groove_half_relief_percentile", 0.0
        ),
        "maximumGrooveHalfBeatsPerBar": settings.get(
            "maximum_groove_half_beats_per_bar", 0
        ),
        "maximumMainOnlyBarsBeforeRelief": maximum_main_only_bars,
        "grooveHalfBeatEligibleBarCount": len({
            candidate["beatIndex"] // BEATS_PER_BAR
            for candidate in half_candidates
            if candidate["hitSample"] in groove_half_samples
            and not is_highlight_half_candidate(candidate)
        }),
        "grooveHalfBeatFallbackBarCount": len({
            candidate["beatIndex"] // BEATS_PER_BAR
            for candidate in half_candidates
            if candidate["hitSample"] in groove_half_fallback_samples
            and not is_highlight_half_candidate(candidate)
        }),
        "grooveHalfBeatSelectedNoteCount": len(
            selected_groove_half_candidates
        ),
        "streakReliefHalfBeatCount": sum(
            candidate["hitSample"] in streak_relief_samples
            for candidate in selected_half_candidates
        ),
        "minimumGapSamples": 0,
        "estimatedBpm": round(estimated_bpm, 6),
        "wheelSelectionPolicy": (
            "audio_score_percentile_with_snowflake_wheel_slot_bias"
            if difficulty_pattern_profile is not None
            else "audio_score_percentile_without_time_quota"
        ),
        "wheelScorePercentile": settings["wheel_score_percentile"],
        "wheelScoreThreshold": round(wheel_score_threshold, 6),
        "wheelLeadClearanceSamples": int(round(
            settings["wheel_lead_clearance_seconds"] * sample_rate
        )),
        "wheelRecoveryClearanceSamples": int(round(
            settings["wheel_recovery_clearance_seconds"] * sample_rate
        )),
        "wheelMainBeatPreservation": settings.get(
            "preserve_main_beats_around_wheels", False
        ),
        "wheelClearancePolicy": (
            "preserve_main_beats_clear_half_beats"
            if settings.get("preserve_main_beats_around_wheels", False)
            else "clear_all_overlapping_notes"
        ),
        "wheelNoteCount": wheel_count,
        "wheelClearanceRemovedCount": wheel_clearance_removed_count,
        "referenceChartKey": SNOWFLAKE_WALTZ_REFERENCE["chart_key"],
        "referenceSongName": SNOWFLAKE_WALTZ_REFERENCE["song_name"],
        "referencePatternApplied": difficulty_pattern_profile is not None,
        "referenceUniquePatternCount": (
            difficulty_pattern_profile["unique_pattern_count"]
            if difficulty_pattern_profile is not None else 0
        ),
        "referencePatternMatchedBarCount": pattern_stats["matched_bar_count"],
        "referencePatternRepositionedBarCount": pattern_stats[
            "repositioned_bar_count"
        ],
        "finalNoteCount": len(notes),
        "bpmBaseCount": bpm_result_count,
        "equalizerRemovedCount": eq_removed_count,
        "equalizerAddedCount": eq_added_count,
        "bpsRemovedCount": bps_removed_count,
        "bpsAddedCount": bps_added_count,
        "loudnessRemovedCount": loudness_removed_count,
        "loudnessAddedCount": loudness_added_count,
        "localBpsMinimum": round(min(local_bps_values), 6) if local_bps_values else 0.0,
        "localBpsMaximum": round(max(local_bps_values), 6) if local_bps_values else 0.0,
        "targetNotesPerSecondMinimum": round(min(target_nps_values), 6) if target_nps_values else 0.0,
        "targetNotesPerSecondMaximum": round(max(target_nps_values), 6) if target_nps_values else 0.0,
        "pipelineStages": [
            {
                "order": 1,
                "name": "BPM",
                "added": bpm_result_count,
                "removed": 0,
                "result": bpm_result_count,
            },
            {
                "order": 2,
                "name": "EQUALIZER",
                "added": eq_added_count,
                "removed": eq_removed_count,
                "result": equalizer_result_count,
            },
            {
                "order": 3,
                "name": "BPS",
                "added": bps_added_count,
                "removed": bps_removed_count,
                "result": bps_result_count,
            },
            {
                "order": 4,
                "name": "LOUDNESS",
                "added": loudness_added_count,
                "removed": loudness_removed_count,
                "result": pre_wheel_count,
            },
            {
                "order": 5,
                "name": "LONG_NOTE_CLEARANCE",
                "added": 0,
                "removed": wheel_clearance_removed_count,
                "result": wheel_clearance_result_count,
            },
            {
                "order": 6,
                "name": "SNOWFLAKE_PATTERN",
                "added": 0,
                "removed": 0,
                "result": len(notes),
                "repositionedBars": pattern_stats["repositioned_bar_count"],
            },
        ],
        "notes": notes,
    }


def generate_onbeat_difficulty_charts(
    audio,
    sample_rate,
    timing_profile=None,
    manual_anchor_map=None,
    pattern_profile=None,
):
    features = (
        {}
        if timing_profile is not None
        else detect_audio_pulse_grid(audio, sample_rate)
    )
    precision = build_precision_features(audio, sample_rate)
    audible_end_sample, audible_reference_rms, audible_threshold_rms = (
        detect_audible_end_sample(audio, sample_rate)
    )
    playable_end_sample = max(
        0,
        min(
            len(audio) - 1,
            audible_end_sample - int(round(ENDING_GUARD_SECONDS * sample_rate)),
        ),
    )
    candidates, beat_samples, grid_samples = build_onbeat_candidates(
        features,
        precision,
        len(audio),
        sample_rate,
        timing_profile,
        manual_anchor_map,
    )
    analyze_candidate_equalizer(audio, sample_rate, candidates)
    features["highlight_summary"] = annotate_pipeline_highlights(candidates)
    charts = [
        select_ordered_pipeline_chart(
            candidates,
            difficulty,
            sample_rate,
            playable_end_sample,
            pattern_profile,
        )
        for difficulty in ("EASY", "NORMAL", "HARD")
    ]
    features["audible_end_sample"] = int(audible_end_sample)
    features["playable_end_sample"] = int(playable_end_sample)
    features["audible_reference_rms"] = float(audible_reference_rms)
    features["audible_threshold_rms"] = float(audible_threshold_rms)
    return features, beat_samples, grid_samples, charts


def build_direct_onset_candidates(
    precision,
    sample_rate,
    playable_end_sample,
):
    candidates = []
    first_sample = int(round(DIRECT_ONSET_FIRST_SECONDS * sample_rate))
    for sample, peak_strength in zip(
        precision["peak_samples"],
        precision["peak_strengths"],
    ):
        sample = int(sample)
        if sample < first_sample or sample > playable_end_sample:
            continue
        low_strength = precision_value_at_sample(precision["low_onset"], precision, sample)
        energy = precision_value_at_sample(precision["energy"], precision, sample)
        score = float(peak_strength) * 0.68 + low_strength * 0.20 + energy * 0.12
        candidates.append({
            "hitSample": sample,
            "strength": float(peak_strength),
            "lowStrength": low_strength,
            "energy": energy,
            "score": score,
        })
    return candidates


def select_direct_onset_notes(candidates, difficulty, sample_rate):
    settings = DIRECT_ONSET_DIFFICULTIES[difficulty]
    scores = np.asarray([candidate["score"] for candidate in candidates], dtype=np.float64)
    percentile_score = float(np.percentile(scores, settings["percentile"]))
    threshold = max(settings["minimum_score"], percentile_score)
    eligible = [candidate for candidate in candidates if candidate["score"] >= threshold]
    minimum_gap_samples = int(round(settings["minimum_gap"] * sample_rate))

    selected = []
    for candidate in eligible:
        if selected and candidate["hitSample"] - selected[-1]["hitSample"] < minimum_gap_samples:
            if candidate["score"] > selected[-1]["score"]:
                selected[-1] = candidate
            continue
        selected.append(candidate)

    final_onset = eligible[-1] if eligible else (selected[-1] if selected else None)
    if final_onset is not None:
        if not selected:
            selected.append(final_onset)
        elif selected[-1]["hitSample"] != final_onset["hitSample"]:
            if final_onset["hitSample"] - selected[-1]["hitSample"] < minimum_gap_samples:
                selected[-1] = final_onset
            else:
                selected.append(final_onset)

    notes = []
    last_wheel_sample = -10 * sample_rate
    for index, candidate in enumerate(selected):
        good = index % 2 == 0
        kind = "GoodTap" if good else "BadTap"
        previous_gap = (
            candidate["hitSample"] - selected[index - 1]["hitSample"]
            if index > 0
            else 10 * sample_rate
        )
        next_gap = (
            selected[index + 1]["hitSample"] - candidate["hitSample"]
            if index + 1 < len(selected)
            else 10 * sample_rate
        )
        can_be_wheel = (
            index + 1 < len(selected)
            and candidate["score"] >= 1.15
            and candidate["lowStrength"] >= 0.55
            and candidate["hitSample"] - last_wheel_sample >= 9 * sample_rate
            and previous_gap >= int(round(0.34 * sample_rate))
            and next_gap >= int(round(0.78 * sample_rate))
        )
        if can_be_wheel:
            kind = "GoodWheelUp" if good else "BadWheelDown"
            last_wheel_sample = candidate["hitSample"]
        notes.append({
            "hitSample": int(candidate["hitSample"]),
            "kind": kind,
            "laneIndex": 1,
        })
    return notes, (int(final_onset["hitSample"]) if final_onset is not None else 0), threshold


def generate_direct_onset_charts(audio, sample_rate):
    features = detect_audio_pulse_grid(audio, sample_rate)
    audible_end_sample, audible_reference_rms, audible_threshold_rms = detect_audible_end_sample(
        audio,
        sample_rate,
    )
    precision = build_precision_features(audio, sample_rate)
    playable_end_sample = max(
        0,
        min(
            len(audio) - 1,
            audible_end_sample - int(round(ENDING_GUARD_SECONDS * sample_rate)),
        ),
    )
    beat_period_samples = float(features["period_frames"] * features["hop"])
    group_period_samples = beat_period_samples * DIRECT_ONSET_GROUP_BEATS
    group_phase_sample = float(features["phase_frames"] * features["hop"])
    candidates = build_direct_onset_candidates(
        precision,
        sample_rate,
        playable_end_sample,
    )
    if not candidates:
        raise ValueError("No direct onset candidates were detected")
    charts = []
    for difficulty in ("EASY", "NORMAL", "HARD"):
        notes, final_onset_sample, threshold = select_direct_onset_notes(
            candidates,
            difficulty,
            sample_rate,
        )
        charts.append({
            "difficulty": difficulty,
            "selectionPercentile": DIRECT_ONSET_DIFFICULTIES[difficulty]["percentile"],
            "selectionThreshold": round(threshold, 6),
            "minimumGapSamples": int(round(
                DIRECT_ONSET_DIFFICULTIES[difficulty]["minimum_gap"] * sample_rate
            )),
            "finalOnsetSample": final_onset_sample,
            "notes": notes,
        })
    features["precision_peak_count"] = int(len(precision["peak_samples"]))
    features["direct_onset_samples"] = [candidate["hitSample"] for candidate in candidates]
    features["rhythm_group_phase_sample"] = group_phase_sample
    features["rhythm_group_period_samples"] = group_period_samples
    features["audible_end_sample"] = int(audible_end_sample)
    features["playable_end_sample"] = int(playable_end_sample)
    features["audible_reference_rms"] = float(audible_reference_rms)
    features["audible_threshold_rms"] = float(audible_threshold_rms)
    return features, charts


def generate_difficulty(song_name, analysis, beat_samples, bpm, difficulty):
    preset = DIFFICULTIES[difficulty]
    sample_rate = analysis["sample_rate"]
    beat_seconds = 60.0 / bpm
    bps = bpm / 60.0
    first_playable = max(int(round(0.85 * sample_rate)), beat_samples[0])
    final_playable = max(
        first_playable,
        min(
            analysis["total_samples"] - int(round(1.1 * sample_rate)),
            last_audible_sample(analysis) - int(round(0.35 * sample_rate)),
        ),
    )
    song_energy = max(0.05, average_envelope(analysis["energy"], analysis, first_playable, final_playable))
    playable = [i for i, sample in enumerate(beat_samples) if first_playable <= sample <= final_playable]
    difficulty_index = ("EASY", "NORMAL", "HARD").index(difficulty)
    rng = random.Random(abs(stable_seed(song_name) ^ ((difficulty_index + 1) * 7919)))
    notes = []
    last_good = True
    run_length = 0
    last_wheel_beat = -100
    radius = min(preset["peak_radius"], beat_seconds * 0.30)

    for phrase_start in range(0, len(playable), PHRASE_BEATS):
        phrase_length = min(PHRASE_BEATS, len(playable) - phrase_start)
        if phrase_length <= 0:
            continue
        phrase_first = beat_samples[playable[phrase_start]]
        phrase_last = min(
            analysis["total_samples"] - 1,
            beat_samples[playable[phrase_start + phrase_length - 1]] + int(round(beat_seconds * sample_rate)),
        )
        strength = [0.0] * 8
        energy = [0.0] * 8
        samples = [0] * 8
        for offset in range(phrase_length):
            beat_index = playable[phrase_start + offset]
            beat_sample = beat_samples[beat_index]
            next_beat = beat_samples[beat_index + 1] if beat_index + 1 < len(beat_samples) else beat_sample + int(round(beat_seconds * sample_rate))
            half_sample = midpoint_sample(beat_sample, next_beat)
            base = (offset % BEATS_PER_BAR) * 2
            for slot, slot_sample in ((base, beat_sample), (base + 1, half_sample)):
                strength[slot] += peak_onset(analysis, slot_sample, radius)
                energy[slot] += sample_envelope(analysis["energy"], analysis, slot_sample)
                samples[slot] += 1
        for slot in range(8):
            if samples[slot]:
                strength[slot] /= samples[slot]
                energy[slot] /= samples[slot]

        section_energy = average_envelope(analysis["energy"], analysis, phrase_first, phrase_last)
        density = clamp(section_energy / song_energy, 0.7, 1.2)
        count = target_slots(difficulty, density, bps)
        slot_scores = [
            rhythm_slot_score(strength[slot], energy[slot], slot, density, bps, preset["allows_half_beats"])
            for slot in range(8)
        ]
        chosen = select_pattern(slot_scores, count, preset["allows_half_beats"], density, bps)
        fast = inverse_lerp(REFERENCE_BPS, FAST_BPS, bps)
        fill_half = preset["allows_half_beats"] and density >= lerp(0.92, 0.76, fast)
        fill_slot = best_unchosen_slot(slot_scores, chosen, fill_half, density, bps, True)

        for offset in range(phrase_length):
            global_beat = phrase_start + offset
            beat_index = playable[global_beat]
            beat_sample = beat_samples[beat_index]
            next_beat = beat_samples[beat_index + 1] if beat_index + 1 < len(beat_samples) else beat_sample + int(round(beat_seconds * sample_rate))
            half_sample = midpoint_sample(beat_sample, next_beat)
            base = (offset % BEATS_PER_BAR) * 2
            fill_bar = phrase_length == PHRASE_BEATS and offset >= phrase_length - BEATS_PER_BAR
            for half, note_sample in ((0, beat_sample), (1, half_sample)):
                slot = base + half
                is_fill = fill_bar and slot == fill_slot
                if not chosen[slot] and not is_fill:
                    continue
                onset_strength = peak_onset(analysis, note_sample, radius)
                energy_strength = sample_envelope(analysis["energy"], analysis, note_sample)
                if energy_strength < 0.06 and onset_strength < 0.05:
                    continue
                note_score = onset_strength * 0.68 + energy_strength * 0.20 + slot_accent(slot) * 0.12
                if is_fill and note_score < fill_minimum(difficulty, density, bps):
                    continue

                good, run_length = choose_color(rng, preset, last_good, run_length)
                last_good = good
                is_long = False
                beat_in_bar = offset % BEATS_PER_BAR
                if half == 0 and beat_in_bar in (0, 2) and global_beat - last_wheel_beat >= 2:
                    sustain_end = min(
                        analysis["total_samples"] - 1,
                        note_sample + int(round(beat_seconds * 1.5 * sample_rate)),
                    )
                    sustained = average_envelope(analysis["energy"], analysis, note_sample, sustain_end)
                    chance = preset["long_note_chance"]
                    if offset == 0:
                        chance *= 2.4
                    elif beat_in_bar == 0:
                        chance *= 1.5
                    else:
                        chance *= 0.8
                    chance *= 0.24
                    is_long = sustained >= preset["long_energy_threshold"] and rng.random() <= min(0.92, chance)

                if is_long:
                    kind = "GoodWheelUp" if good else "BadWheelDown"
                else:
                    kind = "GoodTap" if good else "BadTap"
                if try_add_note(notes, note_sample, kind, sample_rate, preset, beat_seconds):
                    if is_long:
                        last_wheel_beat = global_beat
                elif is_long:
                    fallback = "GoodTap" if good else "BadTap"
                    try_add_note(notes, note_sample, fallback, sample_rate, preset, beat_seconds)

    if not notes and playable:
        notes.append({"hitSample": int(beat_samples[playable[0]]), "kind": "GoodTap", "laneIndex": 1})
    ensure_minimum_wheels(notes, preset, sample_rate, beat_seconds)
    return notes


def validate_chart(chart):
    beats = chart["beatSamples"]
    uses_direct_onsets = chart.get("notePlacementMode") == "direct_audio_onsets"
    beat_grid_subdivision = int(chart.get("beatGridSubdivision", 1))
    uses_main_beats_only = beat_grid_subdivision == 1
    playable_end_sample = int(chart.get("playableEndSample", chart["totalSamples"] - 1))
    audible_end_sample = int(chart.get("audibleEndSample", chart["totalSamples"]))
    if not 0 <= playable_end_sample < audible_end_sample <= chart["totalSamples"]:
        raise ValueError("Playable and audible endings are outside the chart range")
    automatic_anchors = chart.get("automaticBeatAnchors", [])
    if automatic_anchors:
        timing_analysis_beats = int(chart.get("timingAnalysisBeats", 0))
        timing_anchor_beats = int(chart.get("timingAnchorBeats", 0))
        timing_context_weight = float(chart.get("timingContextWeight", -1.0))
        if timing_anchor_beats <= 0 or timing_analysis_beats < timing_anchor_beats:
            raise ValueError("Automatic timing analysis span is invalid")
        if not 0.0 <= timing_context_weight <= 1.0:
            raise ValueError("Automatic timing context weight is invalid")
        for first, second in zip(automatic_anchors, automatic_anchors[1:]):
            if second["beatIndex"] - first["beatIndex"] != timing_anchor_beats:
                raise ValueError("Automatic timing anchors do not use the configured span")
            if second["sample"] <= first["sample"]:
                raise ValueError("Automatic timing anchor samples must increase")
    if uses_direct_onsets:
        if chart.get("beatGridMode") != "none" or beats or chart.get("gridSamples", []):
            raise ValueError("Direct onset charts must not contain a beat grid")
        if int(chart.get("rhythmGroupingBeats", 0)) != DIRECT_ONSET_GROUP_BEATS:
            raise ValueError("Direct onset rhythm grouping must use four beats")
        if float(chart.get("rhythmGroupPeriodSamples", 0.0)) <= 0.0:
            raise ValueError("Direct onset rhythm group period is missing")
        valid_samples = set(chart.get("directOnsetSamples", []))
        if not valid_samples:
            raise ValueError("Direct onset samples are missing")
    else:
        grid_samples = chart.get("gridSamples", [])
        valid_samples = set(grid_samples)
        if uses_main_beats_only:
            if grid_samples != beats:
                raise ValueError("Main-beat chart grid must exactly match beatSamples")
            valid_samples = set(beats)
        elif not valid_samples:
            valid_samples = set(beats)
            valid_samples.update(
                midpoint_sample(beats[i], beats[i + 1])
                for i in range(len(beats) - 1)
            )
        if not set(beats).issubset(valid_samples):
            raise ValueError("Strict beat samples are missing from the note grid")
        if beat_grid_subdivision not in (1, 2):
            raise ValueError("Beat charts may use only main-beat or half-beat grids")
        if beat_grid_subdivision == 2:
            exact_half_samples = set()
            for first, second in zip(beats, beats[1:]):
                half = midpoint_sample(first, second)
                exact_half_samples.update((half - 1, half, half + 1))
            if len(beats) >= 2:
                trailing_half = int(round(
                    beats[-1] + (beats[-1] - beats[-2]) * 0.5
                ))
                exact_half_samples.update(
                    (trailing_half - 1, trailing_half, trailing_half + 1)
                )
            unknown_grid_samples = (
                valid_samples - set(beats) - exact_half_samples
            )
            if unknown_grid_samples:
                raise ValueError("Half-beat grid contains a non-half-beat sample")
    for difficulty in chart["charts"]:
        previous = -1
        minimum_gap_samples = int(difficulty.get("minimumGapSamples", 0))
        if int(difficulty.get("finalNoteCount", -1)) != len(difficulty["notes"]):
            raise ValueError(
                f"Final note count metadata mismatch in {difficulty['difficulty']}"
            )
        for note in difficulty["notes"]:
            sample = note["hitSample"]
            if sample not in valid_samples:
                raise ValueError(f"Unknown timing sample {sample} in {difficulty['difficulty']}")
            if sample <= previous or sample >= chart["totalSamples"]:
                raise ValueError(f"Invalid sample order/range {sample} in {difficulty['difficulty']}")
            if previous >= 0 and sample - previous < minimum_gap_samples:
                raise ValueError(f"Note gap is too short in {difficulty['difficulty']}")
            if sample > playable_end_sample:
                raise ValueError(f"Note exceeds playable ending in {difficulty['difficulty']}")
            if note.get("kind") not in {
                "GoodTap",
                "BadTap",
                "GoodWheelUp",
                "BadWheelDown",
            }:
                raise ValueError(f"Unknown note kind in {difficulty['difficulty']}")
            if int(note.get("laneIndex", -1)) != 1:
                raise ValueError(f"Invalid note lane in {difficulty['difficulty']}")
            previous = sample
        actual_wheel_count = sum(
            "Wheel" in note.get("kind", "") for note in difficulty["notes"]
        )
        if actual_wheel_count != int(difficulty.get("wheelNoteCount", -1)):
            raise ValueError(
                f"Long-note count metadata mismatch in {difficulty['difficulty']}"
            )
        if not uses_direct_onsets:
            actual_half_beat_count = sum(
                note["hitSample"] not in set(beats)
                for note in difficulty["notes"]
            )
            if actual_half_beat_count != int(
                difficulty.get("burstNoteCount", -1)
            ):
                raise ValueError(
                    f"Half-beat count metadata mismatch in {difficulty['difficulty']}"
                )
        wheel_lead_clearance = int(
            difficulty.get("wheelLeadClearanceSamples", 0)
        )
        wheel_recovery_clearance = int(
            difficulty.get("wheelRecoveryClearanceSamples", 0)
        )
        preserve_wheel_main_beats = bool(
            difficulty.get("wheelMainBeatPreservation", False)
        )
        main_beat_samples = set(beats)
        for index, note in enumerate(difficulty["notes"]):
            if "Wheel" not in note.get("kind", ""):
                continue
            if (
                index > 0
                and note["hitSample"]
                - difficulty["notes"][index - 1]["hitSample"]
                < wheel_lead_clearance
                and not (
                    preserve_wheel_main_beats
                    and difficulty["notes"][index - 1]["hitSample"]
                    in main_beat_samples
                )
            ):
                raise ValueError(
                    f"Long-note lead clearance failed in {difficulty['difficulty']}"
                )
            if (
                index + 1 < len(difficulty["notes"])
                and difficulty["notes"][index + 1]["hitSample"]
                - note["hitSample"]
                < wheel_recovery_clearance
                and not (
                    preserve_wheel_main_beats
                    and difficulty["notes"][index + 1]["hitSample"]
                    in main_beat_samples
                )
            ):
                raise ValueError(
                    f"Long-note recovery clearance failed in {difficulty['difficulty']}"
                )
        if difficulty["notes"] and uses_direct_onsets:
            if difficulty.get("finalOnsetSample") != difficulty["notes"][-1]["hitSample"]:
                raise ValueError(f"Final note mismatch in {difficulty['difficulty']}")
            if not difficulty["notes"][-1]["kind"].endswith("Tap"):
                raise ValueError(f"Final note must be a tap in {difficulty['difficulty']}")
        elif difficulty["notes"] and "finalCadenceSample" in difficulty:
            if difficulty["finalCadenceSample"] != difficulty["notes"][-1]["hitSample"]:
                raise ValueError(f"Final note mismatch in {difficulty['difficulty']}")

    if not uses_direct_onsets:
        chart_by_name = {
            difficulty["difficulty"]: difficulty
            for difficulty in chart["charts"]
        }
        if set(chart_by_name) != {"EASY", "NORMAL", "HARD"}:
            raise ValueError("Beat charts require EASY, NORMAL, and HARD")
        for difficulty in chart_by_name.values():
            if int(difficulty.get("quarterBurstNoteCount", 0)) != 0:
                raise ValueError("Quarter-beat notes are not allowed")
            if difficulty["notes"] and not difficulty["notes"][-1]["kind"].endswith("Tap"):
                raise ValueError(
                    f"Final note must be a tap in {difficulty['difficulty']}"
                )


def resolve_chart_key(entry, index):
    output_name = str(entry.get("chartKey", "")).strip()
    if not output_name and index < len(OUTPUT_NAMES):
        output_name = OUTPUT_NAMES[index]
    return output_name or f"song_{index + 1:03d}"


def generate_all(project, resume=False):
    music = project / "Assets" / "Resources" / "Music"
    output = project / "Assets" / "Resources" / "NoteCharts"
    unity_pcm = load_unity_pcm_export(project / "Temp" / "NoteChartPcm")
    if not unity_pcm:
        raise FileNotFoundError(
            "Unity PCM export is missing. Run Tools/Rhythm/Export Imported PCM For Note Charts first."
        )
    manifest = json.loads((music / "bpm_manifest.json").read_text(encoding="utf-8"))
    songs = manifest.get("songs", [])
    if not songs:
        raise ValueError("Music manifest contains no songs")
    manual_anchor_maps = load_manual_anchor_maps(project)
    output.mkdir(parents=True, exist_ok=True)

    reference_entry = None
    for index, entry in enumerate(songs):
        if resolve_chart_key(entry, index) == SNOWFLAKE_WALTZ_REFERENCE["chart_key"]:
            reference_entry = entry
            break
    if reference_entry is None:
        raise ValueError("Snowflake Waltz reference chart is missing from the manifest")
    reference_pcm_key = reference_entry["name"].strip().lower()
    if reference_pcm_key not in unity_pcm:
        raise KeyError("Unity PCM export is missing for the Snowflake Waltz reference")
    reference_pcm = unity_pcm[reference_pcm_key]
    reference_generated = generate_onbeat_difficulty_charts(
        reference_pcm["audio"],
        reference_pcm["sample_rate"],
        STRICT_V6_BUILD_TIMING.get(SNOWFLAKE_WALTZ_REFERENCE["chart_key"]),
        manual_anchor_maps.get(SNOWFLAKE_WALTZ_REFERENCE["chart_key"]),
        None,
    )
    reference_pattern_profile = build_snowflake_pattern_profile(
        reference_generated[1],
        reference_generated[3],
    )
    print(
        "Snowflake Waltz placement grammar: "
        + ", ".join(
            f"{difficulty}={profile['unique_pattern_count']} patterns"
            for difficulty, profile in reference_pattern_profile.items()
        ),
        flush=True,
    )

    output_names = set()
    for index, entry in enumerate(songs):
        output_name = resolve_chart_key(entry, index)
        if output_name in output_names:
            raise ValueError(f"Duplicate chartKey: {output_name}")
        output_names.add(output_name)

        audio_path = music / f"{entry['name']}.mp3"
        if not audio_path.exists():
            raise FileNotFoundError(audio_path)
        audio_sha256 = hashlib.sha256(audio_path.read_bytes()).hexdigest()
        target = output / f"{output_name}.json"
        if resume and target.exists():
            try:
                existing = json.loads(target.read_text(encoding="utf-8"))
                if (
                    existing.get("generator") == GENERATOR_VERSION
                    and existing.get("audioSha256") == audio_sha256
                ):
                    validate_chart(existing)
                    entry["chartKey"] = output_name
                    entry["bpm"] = round(float(existing["bpm"]), 6)
                    entry["firstBeat"] = round(
                        int(existing["firstBeatSample"])
                        / int(existing["sampleRate"]),
                        6,
                    )
                    print(f"{output_name}: verified existing {GENERATOR_VERSION}", flush=True)
                    continue
            except (KeyError, TypeError, ValueError, json.JSONDecodeError):
                pass
        pcm_key = entry["name"].strip().lower()
        if pcm_key not in unity_pcm:
            raise KeyError(f"Unity PCM export missing for {output_name}")
        pcm = unity_pcm[pcm_key]
        sample_rate = pcm["sample_rate"]
        timing_profile = STRICT_V6_BUILD_TIMING.get(output_name)
        manual_anchor_map = manual_anchor_maps.get(output_name)
        if manual_anchor_map is not None:
            anchor_audio_hash = str(manual_anchor_map.get("audioSha256", "")).strip()
            if anchor_audio_hash and anchor_audio_hash != audio_sha256:
                raise ValueError(
                    f"Manual anchor audio hash mismatch for {output_name}: "
                    f"anchor={anchor_audio_hash} audio={audio_sha256}"
                )
            anchor_song_name = str(manual_anchor_map.get("songName", "")).strip()
            if anchor_song_name and anchor_song_name != entry["name"]:
                raise ValueError(
                    f"Manual anchor song mismatch for {output_name}: "
                    f"anchor={anchor_song_name} manifest={entry['name']}"
                )
        if output_name == SNOWFLAKE_WALTZ_REFERENCE["chart_key"]:
            features, beats, grid_samples, charts = reference_generated
        else:
            features, beats, grid_samples, charts = generate_onbeat_difficulty_charts(
                pcm["audio"],
                sample_rate,
                timing_profile,
                manual_anchor_map,
                reference_pattern_profile,
            )
        if len(beats) < PHRASE_BEATS or not grid_samples:
            raise ValueError(f"Strict beat grid is too short for {output_name}")
        counts = [len(chart["notes"]) for chart in charts]
        beat_grid_mode = features.get(
            "beat_grid_mode",
            "manual_anchors" if manual_anchor_map is not None else "adaptive_bar_anchors",
        )
        timing_sources = {
            "constant": "Unity PCM calibrated constant musical beat grid",
            "manual_anchors": "Unity PCM manual first-beat and bar-anchor map",
            "adaptive_bar_anchors": (
                "Unity PCM adaptive bar anchors from calibrated musical beat grid"
            ),
        }
        document = {
            "version": 1,
            "generator": GENERATOR_VERSION,
            "pcmSource": "Unity AudioClip.GetData",
            "timingSource": timing_sources[beat_grid_mode],
            "noteTimingPolicy": "BPM main beats first; exact half-beats are added in highlights, where equalizer, loudness, or local BPS rises, and on HARD where steady-groove audio strongly supports an offbeat; no quarter-beat notes",
            "difficultyPolicy": "each song keeps its own BPM, loudness-driven density, highlight intensity, and audio-supported HARD groove accents; Snowflake Waltz is used only as a bar-level note-placement and long-note-position grammar",
            "generationPipeline": [
                "BPM",
                "EQUALIZER",
                "BPS",
                "LOUDNESS",
                "LONG_NOTE_CLEARANCE",
                "SNOWFLAKE_PATTERN",
            ],
            "difficultyReference": {
                "chartKey": SNOWFLAKE_WALTZ_REFERENCE["chart_key"],
                "songName": SNOWFLAKE_WALTZ_REFERENCE["song_name"],
                "policy": "placement_patterns_only_not_note_count_or_density",
            },
            "halfBeatPolicy": "EASY disables half-beats; NORMAL uses highlight and rise triggers; HARD also allows one audio-supported groove half-beat in steady bars and relief after two consecutive main-only bars",
            "longNotePolicy": "no fixed time quota or minimum frequency; strong audio-ranked main beats become long notes, HARD preserves surrounding main beats, and only overlapping half-beats are cleared",
            "equalizerInputPolicy": "reuse the existing low-pulse, broad-transient, and loudness envelopes without a separate frequency-band scan",
            "colorPatternPolicy": "tap color follows the low-pulse versus broad-transient balance with a difficulty-specific maximum run length",
            "clusterPolicy": "no generic tap gap; HARD long-note clearances never remove main beats and clear only overlapping half-beats",
            "endingPolicy": (
                f"notes stop at least {ENDING_GUARD_SECONDS * 1000:.0f} ms "
                "before the measured audible ending"
            ),
            "beatGridMode": beat_grid_mode,
            "beatGridSubdivision": 2,
            "beatPeriodSamples": round(features["strict_period_samples"], 6),
            "beatPhaseSamples": round(features["strict_phase_samples"], 6),
            "manualAnchorCount": len(
                features.get("manual_anchor_map", {}).get("anchors", [])
            ),
            "automaticAnchorCount": len(features.get("auto_timing_anchors", [])),
            "adaptiveOffsetP95Ms": round(features.get("adaptive_offset_p95_ms", 0.0), 4),
            "automaticBeatAnchors": features.get("auto_timing_anchors", []),
            "manualBeatAnchors": features.get("manual_anchor_map", {}).get("anchors", []),
            "timingAnalysisBeats": TIMING_ANALYSIS_BEATS,
            "timingAnchorBeats": TIMING_ANCHOR_BEATS,
            "timingContextWeight": TIMING_CONTEXT_WEIGHT,
            "precisionHopSamples": 32,
            "precisionPeakCount": features.get("precision_peak_count", 0),
            "highlightAnalysis": features.get("highlight_summary", {}),
            "audibleEndSample": int(features["audible_end_sample"]),
            "playableEndSample": int(features["playable_end_sample"]),
            "endingGuardSeconds": ENDING_GUARD_SECONDS,
            "audibleReferenceRms": round(features["audible_reference_rms"], 8),
            "audibleThresholdRms": round(features["audible_threshold_rms"], 8),
            "activeBeatPercent": round(
                features.get("strict_grid_active_beat_percent", 0.0),
                3,
            ),
            "beatTransientMedianMs": round(
                features.get("strict_grid_transient_median_ms", 0.0),
                4,
            ),
            "beatTransientP95Ms": round(
                features.get("strict_grid_transient_p95_ms", 0.0),
                4,
            ),
            "songName": entry["name"],
            "audioFile": audio_path.name,
            "audioSha256": audio_sha256,
            "sampleRate": sample_rate,
            "totalSamples": int(len(pcm["audio"])),
            "bpm": round(features["bpm"], 6),
            "tempoSegments": [
                {
                    "timeSeconds": round(segment["timeSeconds"], 3),
                    "bpm": round(segment["bpm"], 6),
                }
                for segment in features["tempo_segments"]
            ],
            "firstBeatSample": beats[0],
            "beatSamples": beats,
            "gridSamples": grid_samples,
            "charts": charts,
        }
        validate_chart(document)
        entry["chartKey"] = output_name
        entry["bpm"] = round(features["bpm"], 6)
        entry["firstBeat"] = round(beats[0] / sample_rate, 6)
        target.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        timing_mode = {
            "constant": "constant",
            "manual_anchors": "manual",
            "adaptive_bar_anchors": "adaptive",
        }[beat_grid_mode]
        print(
            f"{output_name}: {sample_rate} Hz, "
            f"anchored={features['bpm']:.3f} BPM ({timing_mode}), "
            f"beats={len(beats)}, grid={len(grid_samples)}, E/N/H={counts}",
            flush=True,
        )

    (music / "bpm_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def main():
    parser = argparse.ArgumentParser()
    default_project = Path(__file__).resolve().parents[1] / "gmaejam2026" / "My project (1)"
    parser.add_argument("--project", type=Path, default=default_project)
    parser.add_argument(
        "--resume",
        action="store_true",
        help="Verify and skip charts already generated by the current version.",
    )
    args = parser.parse_args()
    generate_all(args.project.resolve(), resume=args.resume)


if __name__ == "__main__":
    main()
