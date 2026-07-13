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
ENDING_GUARD_SECONDS = AUDIBLE_END_WINDOW_SECONDS

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
        "density": 0.75,
        "color_max_run_length": 4,
        "wheel_window_seconds": 24.0,
        "wheel_min_gap_seconds": 18.0,
        "wheel_minimum_strength": 0.30,
    },
    "NORMAL": {
        "density": 1.00,
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

HARD_MINIMUM_NOTE_GAP_SECONDS = 0.20

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
        audio = np.fromfile(raw_path, dtype="<f4")
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


def build_adaptive_anchor_beat_grid(features, precision, total_samples, sample_rate):
    strict_beats = build_strict_beat_grid(
        features,
        precision,
        total_samples,
        sample_rate,
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
        if beat_index <= previous_beat:
            raise ValueError(f"Duplicate or reversed manual beat index: {beat_index}")
        if sample <= previous_sample or sample < 0 or sample >= total_samples:
            raise ValueError(f"Invalid manual anchor sample: {sample}")
        previous_beat = beat_index
        previous_sample = sample
    return anchors


def build_manual_beat_grid(features, precision, total_samples, sample_rate, document):
    anchors = validate_manual_anchor_map(document, sample_rate, total_samples)
    if len(anchors) == 1:
        build_strict_beat_grid(features, precision, total_samples, sample_rate)
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
    for beat_index, beat_sample in enumerate(beat_samples):
        for subdivision in (0, 2):
            expected_sample = phase_samples + (
                beat_index + subdivision / 4.0
            ) * period_samples
            if expected_sample >= total_samples:
                continue
            sample = (
                int(beat_sample)
                if subdivision == 0
                else int(clamp(round(expected_sample), 0, total_samples - 1))
            )
            grid_samples.append(sample)
            strength, energy = precision_window_values(
                precision,
                sample,
                int(round(0.028 * sample_rate)),
            )
            beat_in_bar = beat_index % BEATS_PER_BAR
            accent_bonus = 0.0
            if subdivision == 0:
                accent_bonus = 0.12 if beat_in_bar == 0 else 0.05 if beat_in_bar == 2 else 0.0
            elif subdivision == 2:
                accent_bonus = 0.025
            candidates.append({
                "hitSample": sample,
                "beatIndex": beat_index,
                "subdivision": subdivision,
                "strength": strength,
                "energy": energy,
                "score": strength * 0.72 + energy * 0.28 + accent_bonus,
            })
    return candidates, beat_samples, sorted(set(grid_samples))


def apply_onbeat_color_patterns(chart, candidates):
    difficulty = chart["difficulty"]
    pattern_bank = ONBEAT_COLOR_PATTERN_BANKS[difficulty]
    maximum_run = ONBEAT_DIFFICULTY_SETTINGS[difficulty][
        "color_max_run_length"
    ]
    candidate_by_sample = {
        candidate["hitSample"]: candidate
        for candidate in candidates
    }
    candidates_by_bar = {}
    for note in chart["notes"]:
        candidate = candidate_by_sample[note["hitSample"]]
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        candidates_by_bar.setdefault(bar_index, []).append(candidate)

    bar_patterns = {}
    previous_pattern_index = -1
    used_pattern_indices = set()
    for bar_index in sorted(candidates_by_bar):
        bar_candidates = candidates_by_bar[bar_index]
        signature = 0
        for index, candidate in enumerate(bar_candidates):
            strength = int(round(candidate["strength"] * 1000.0))
            energy = int(round(candidate["energy"] * 1000.0))
            signature += (index + 1) * (
                strength * 31
                + energy * 17
                + (candidate["subdivision"] + 1) * 97
            )
        pattern_index = (
            bar_index * 37
            + (bar_index // 4) * 53
            + signature
        ) % len(pattern_bank)
        if pattern_index == previous_pattern_index and len(pattern_bank) > 1:
            pattern_index = (
                pattern_index + 1 + signature % (len(pattern_bank) - 1)
            ) % len(pattern_bank)
        pattern = pattern_bank[pattern_index]
        phase = (
            bar_index * 11 + signature // max(1, len(pattern_bank))
        ) % len(pattern)
        bar_patterns[bar_index] = (pattern, phase)
        previous_pattern_index = pattern_index
        used_pattern_indices.add(pattern_index)

    previous_good = None
    run_length = 0
    local_indices = {}
    for note in chart["notes"]:
        candidate = candidate_by_sample[note["hitSample"]]
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        local_index = local_indices.get(bar_index, 0)
        pattern, phase = bar_patterns[bar_index]
        good = pattern[(local_index + phase) % len(pattern)]
        if good == previous_good and run_length >= maximum_run:
            good = not good

        run_length = run_length + 1 if good == previous_good else 1
        previous_good = good
        local_indices[bar_index] = local_index + 1
        is_wheel = "Wheel" in note["kind"]
        if is_wheel:
            note["kind"] = "GoodWheelUp" if good else "BadWheelDown"
        else:
            note["kind"] = "GoodTap" if good else "BadTap"

    chart["colorRunLength"] = maximum_run
    chart["colorPattern"] = "audio_seeded_variant_bank"
    chart["colorPatternVariantCount"] = len(pattern_bank)
    chart["colorPatternVariantsUsed"] = len(used_pattern_indices)


def choose_onbeat_wheel_indices(selected, settings, sample_rate):
    if not selected:
        return set()

    wheel_window_samples = int(round(settings["wheel_window_seconds"] * sample_rate))
    minimum_gap_samples = int(round(settings["wheel_min_gap_seconds"] * sample_rate))
    first_window_sample = selected[0]["hitSample"] + int(round(4.0 * sample_rate))
    last_window_sample = selected[-1]["hitSample"] - int(round(2.0 * sample_rate))
    minimum_strength = settings["wheel_minimum_strength"]
    chosen = set()
    last_wheel_sample = -minimum_gap_samples

    window_start = first_window_sample
    while window_start <= last_window_sample:
        window_end = min(last_window_sample + 1, window_start + wheel_window_samples)
        eligible = [
            (index, candidate)
            for index, candidate in enumerate(selected)
            if window_start <= candidate["hitSample"] < window_end
            and candidate["subdivision"] == 0
            and candidate["hitSample"] - last_wheel_sample >= minimum_gap_samples
            and candidate["strength"] >= minimum_strength
        ]
        if eligible:
            index, candidate = max(
                eligible,
                key=lambda item: item[1]["strength"] * 0.68
                + item[1]["energy"] * 0.22
                + (0.10 if item[1]["beatIndex"] % BEATS_PER_BAR == 0 else 0.0),
            )
            chosen.add(index)
            last_wheel_sample = candidate["hitSample"]
        window_start += wheel_window_samples
    return chosen


def select_taiko_hard_extras(candidates, active_bar_indices):
    candidates_by_bar = {}
    for candidate in candidates:
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        if bar_index in active_bar_indices:
            candidates_by_bar.setdefault(bar_index, []).append(candidate)

    extras = []
    for bar_index in sorted(active_bar_indices):
        bar_candidates = candidates_by_bar.get(bar_index, [])
        main_candidates = [
            candidate for candidate in bar_candidates
            if candidate["subdivision"] == 0
        ]
        half_candidates = [
            candidate for candidate in bar_candidates
            if candidate["subdivision"] == 2
        ]
        if not main_candidates or not half_candidates:
            continue

        mean_energy = float(np.mean([
            candidate["energy"] for candidate in main_candidates
        ]))
        peak_strength = max(
            candidate["strength"] for candidate in bar_candidates
        )
        half_target = 1
        if mean_energy >= 0.85 or peak_strength >= 1.00:
            half_target += 1
        if mean_energy >= 1.20 and peak_strength >= 1.35:
            half_target += 1
        selected_halves = sorted(
            half_candidates,
            key=lambda candidate: (candidate["score"], -candidate["beatIndex"]),
            reverse=True,
        )[: min(half_target, len(half_candidates))]
        extras.extend(selected_halves)
    return extras


def select_normal_accent_extras(candidates, active_bar_indices):
    candidates_by_bar = {}
    for candidate in candidates:
        bar_index = candidate["beatIndex"] // BEATS_PER_BAR
        if bar_index in active_bar_indices:
            candidates_by_bar.setdefault(bar_index, []).append(candidate)

    extras = []
    last_accent_bar = -100
    for bar_index in sorted(active_bar_indices):
        if bar_index - last_accent_bar < 2:
            continue
        bar_candidates = candidates_by_bar.get(bar_index, [])
        main_candidates = [
            candidate for candidate in bar_candidates
            if candidate["subdivision"] == 0
        ]
        half_candidates = [
            candidate for candidate in bar_candidates
            if candidate["subdivision"] == 2
            and candidate["strength"] >= 0.08
        ]
        if not main_candidates or not half_candidates:
            continue

        mean_energy = float(np.mean([
            candidate["energy"] for candidate in main_candidates
        ]))
        peak_strength = max(
            candidate["strength"] for candidate in bar_candidates
        )
        if mean_energy < 0.88 and peak_strength < 1.05:
            continue

        extras.append(max(
            half_candidates,
            key=lambda candidate: (candidate["score"], -candidate["beatIndex"]),
        ))
        last_accent_bar = bar_index
    return extras


def select_onbeat_difficulty_chart(candidates, difficulty, sample_rate):
    settings = ONBEAT_DIFFICULTY_SETTINGS[difficulty]
    main_candidates = [
        candidate for candidate in candidates
        if candidate["subdivision"] == 0
    ]
    selected = []
    active_beat_count = 0
    active_bar_indices = set()
    for bar_start in range(0, len(main_candidates), BEATS_PER_BAR):
        bar_candidates = main_candidates[bar_start : bar_start + BEATS_PER_BAR]
        if not bar_candidates:
            continue
        if (
            max(candidate["strength"] for candidate in bar_candidates) < 0.06
            and max(candidate["energy"] for candidate in bar_candidates) < 0.08
        ):
            continue

        active_beat_count += len(bar_candidates)
        active_bar_indices.add(bar_candidates[0]["beatIndex"] // BEATS_PER_BAR)
        target = max(1, int(math.ceil(len(bar_candidates) * settings["density"])))
        ranked = sorted(
            bar_candidates,
            key=lambda candidate: (candidate["score"], -candidate["beatIndex"]),
            reverse=True,
        )
        selected.extend(ranked[:target])

    burst_note_count = 0
    if difficulty == "NORMAL":
        selected.extend(select_normal_accent_extras(
            candidates,
            active_bar_indices,
        ))
    elif difficulty == "HARD":
        hard_extras = select_taiko_hard_extras(candidates, active_bar_indices)
        selected.extend(hard_extras)

    selected.sort(key=lambda candidate: candidate["hitSample"])
    wheel_indices = choose_onbeat_wheel_indices(selected, settings, sample_rate)
    wheel_samples = {
        selected[index]["hitSample"] for index in wheel_indices
    }
    if difficulty in ("NORMAL", "HARD") and wheel_samples:
        wheel_lead_seconds = 0.34 if difficulty == "HARD" else 0.30
        wheel_recovery_seconds = 0.42 if difficulty == "HARD" else 0.36
        wheel_lead_samples = int(round(wheel_lead_seconds * sample_rate))
        wheel_recovery_samples = int(round(wheel_recovery_seconds * sample_rate))
        selected = [
            candidate
            for candidate in selected
            if candidate["subdivision"] == 0
            or all(
                candidate["hitSample"] <= wheel_sample - wheel_lead_samples
                or candidate["hitSample"] >= wheel_sample + wheel_recovery_samples
                for wheel_sample in wheel_samples
            )
        ]
    if difficulty == "HARD":
        minimum_gap_samples = int(round(
            HARD_MINIMUM_NOTE_GAP_SECONDS * sample_rate
        ))
        spaced = []
        for candidate in selected:
            if (
                not spaced
                or candidate["hitSample"] - spaced[-1]["hitSample"]
                >= minimum_gap_samples
            ):
                spaced.append(candidate)
        selected = spaced
    burst_note_count = sum(
        candidate["subdivision"] > 0 for candidate in selected
    )
    quarter_burst_note_count = sum(
        candidate["subdivision"] in (1, 3) for candidate in selected
    )
    notes = []
    for candidate in selected:
        is_wheel = candidate["hitSample"] in wheel_samples
        if is_wheel:
            kind = "GoodWheelUp"
        else:
            kind = "GoodTap"
        notes.append({
            "hitSample": int(candidate["hitSample"]),
            "kind": kind,
            "laneIndex": 1,
        })

    actual_density = len(selected) / max(1, active_beat_count) * 100.0
    return {
        "difficulty": difficulty,
        "targetDensityPercent": int(round(settings["density"] * 100.0)),
        "actualDensityPercent": round(actual_density, 3),
        "activeBeatCount": active_beat_count,
        "colorRunLength": settings["color_max_run_length"],
        "colorPattern": "audio_seeded_variant_bank",
        "colorPatternVariantCount": len(ONBEAT_COLOR_PATTERN_BANKS[difficulty]),
        "burstNoteCount": burst_note_count,
        "quarterBurstNoteCount": quarter_burst_note_count,
        "wheelWindowSeconds": settings["wheel_window_seconds"],
        "wheelMinimumGapSeconds": settings["wheel_min_gap_seconds"],
        "notes": notes,
    }


def generate_onbeat_difficulty_charts(audio, sample_rate, timing_profile=None):
    features = (
        {}
        if timing_profile is not None
        else detect_audio_pulse_grid(audio, sample_rate)
    )
    precision = build_precision_features(audio, sample_rate)
    candidates, beat_samples, grid_samples = build_onbeat_candidates(
        features,
        precision,
        len(audio),
        sample_rate,
        timing_profile,
    )
    charts = [
        select_onbeat_difficulty_chart(candidates, difficulty, sample_rate)
        for difficulty in ("EASY", "NORMAL", "HARD")
    ]
    chart_by_difficulty = {
        chart["difficulty"]: chart for chart in charts
    }
    beat_sample_set = set(beat_samples)
    for lower_name, upper_name in (("NORMAL", "HARD"), ("EASY", "NORMAL")):
        lower_chart = chart_by_difficulty[lower_name]
        upper_samples = {
            note["hitSample"]
            for note in chart_by_difficulty[upper_name]["notes"]
        }
        previous_count = len(lower_chart["notes"])
        lower_chart["notes"] = [
            note for note in lower_chart["notes"]
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
        apply_onbeat_color_patterns(chart, candidates)
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
    playable_end_sample = int(chart.get("playableEndSample", chart["totalSamples"] - 1))
    audible_end_sample = int(chart.get("audibleEndSample", chart["totalSamples"]))
    if playable_end_sample >= audible_end_sample:
        raise ValueError("Playable ending must precede the measured audible ending")
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
        valid_samples = set(chart.get("gridSamples", []))
        if not valid_samples:
            valid_samples = set(beats)
            valid_samples.update(
                midpoint_sample(beats[i], beats[i + 1])
                for i in range(len(beats) - 1)
            )
        if not set(beats).issubset(valid_samples):
            raise ValueError("Strict beat samples are missing from the note grid")
    for difficulty in chart["charts"]:
        previous = -1
        minimum_gap_samples = int(difficulty.get("minimumGapSamples", 0))
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
            previous = sample
        if difficulty["notes"] and uses_direct_onsets:
            if difficulty.get("finalOnsetSample") != difficulty["notes"][-1]["hitSample"]:
                raise ValueError(f"Final note mismatch in {difficulty['difficulty']}")
            if not difficulty["notes"][-1]["kind"].endswith("Tap"):
                raise ValueError(f"Final note must be a tap in {difficulty['difficulty']}")
        elif difficulty["notes"] and "finalCadenceSample" in difficulty:
            if difficulty["finalCadenceSample"] != difficulty["notes"][-1]["hitSample"]:
                raise ValueError(f"Final note mismatch in {difficulty['difficulty']}")


def generate_all(project):
    music = project / "Assets" / "Resources" / "Music"
    output = project / "Assets" / "Resources" / "NoteCharts"
    unity_pcm = load_unity_pcm_export(project / "Temp" / "NoteChartPcm")
    if not unity_pcm:
        raise FileNotFoundError(
            "Unity PCM export is missing. Run Tools/Rhythm/Export Imported PCM For Note Charts first."
        )
    manifest = json.loads((music / "bpm_manifest.json").read_text(encoding="utf-8"))
    songs = manifest.get("songs", [])
    if len(songs) != len(OUTPUT_NAMES):
        raise ValueError(f"Expected {len(OUTPUT_NAMES)} songs, found {len(songs)}")
    output.mkdir(parents=True, exist_ok=True)

    for entry, output_name in zip(songs, OUTPUT_NAMES):
        audio_path = music / f"{entry['name']}.mp3"
        if not audio_path.exists():
            raise FileNotFoundError(audio_path)
        pcm_key = entry["name"].strip().lower()
        if pcm_key not in unity_pcm:
            raise KeyError(f"Unity PCM export missing for {output_name}")
        pcm = unity_pcm[pcm_key]
        sample_rate = pcm["sample_rate"]
        audio_sha256 = hashlib.sha256(audio_path.read_bytes()).hexdigest()
        features, beats, grid_samples, charts = generate_onbeat_difficulty_charts(
            pcm["audio"],
            sample_rate,
            STRICT_V6_BUILD_TIMING[output_name],
        )
        if len(beats) < PHRASE_BEATS or not grid_samples:
            raise ValueError(f"Strict beat grid is too short for {output_name}")
        counts = [len(chart["notes"]) for chart in charts]
        document = {
            "version": 1,
            "generator": "unity_pcm_nonoverlap_speed_v5",
            "pcmSource": "Unity AudioClip.GetData",
            "timingSource": "Unity PCM calibrated constant musical beat grid",
            "noteTimingPolicy": "strict main-beat and half-beat grid; no quarter-beat notes",
            "difficultyPolicy": "EASY 75% main beats; NORMAL sparse half-beat accents; HARD denser irregular half-beat patterns",
            "colorPatternPolicy": "audio-seeded per-bar pattern bank with rotated, reversed, and inverted variants",
            "clusterPolicy": "HARD minimum note gap 0.20 seconds; NORMAL/HARD wheel guards remove adjacent extras",
            "beatGridMode": "constant",
            "beatGridSubdivision": 2,
            "beatPeriodSamples": round(features["strict_period_samples"], 6),
            "beatPhaseSamples": round(features["strict_phase_samples"], 6),
            "precisionHopSamples": 32,
            "precisionPeakCount": features.get("precision_peak_count", 0),
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
        target = output / f"{output_name}.json"
        target.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(
            f"{output_name}: {sample_rate} Hz, "
            f"quantized={features['bpm']:.3f} BPM, "
            f"beats={len(beats)}, grid={len(grid_samples)}, E/N/H={counts}"
        )


def main():
    parser = argparse.ArgumentParser()
    default_project = Path(__file__).resolve().parents[1] / "gmaejam2026" / "My project (1)"
    parser.add_argument("--project", type=Path, default=default_project)
    args = parser.parse_args()
    generate_all(args.project.resolve())


if __name__ == "__main__":
    main()
