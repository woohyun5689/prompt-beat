import argparse
import math
import os
import random
import subprocess
import tempfile

import imageio.v2 as imageio
import imageio_ffmpeg
import librosa
import numpy as np
from PIL import Image, ImageDraw, ImageFont


WIDTH = 1280
HEIGHT = 720
FPS = 18
ANALYSIS_TARGET_RATE = 100.0
BEATS_PER_BAR = 4
MIN_LONG_GAP = 0.78
WHEEL_LEAD_IN_GAP = 0.34
USE_SINGLE_LANE_CHART = True


PRESETS = {
    "normal": {
        "analysis_strong_base": 0.26,
        "analysis_strong_onset": 0.62,
        "analysis_strong_energy": 0.12,
        "analysis_high_onset_threshold": 0.78,
        "analysis_high_onset_min_chance": 0.94,
        "long_energy_threshold": 0.22,
        "long_note_chance": 0.72,
        "tap_gap": 0.30,
        "long_gap": MIN_LONG_GAP,
        "lane_change_chance": 0.90,
        "wide_lane_jump_chance": 0.35,
        "color_switch_chance": 0.50,
        "good_note_chance": 0.55,
    },
    "hard": {
        "analysis_strong_base": 0.44,
        "analysis_strong_onset": 0.78,
        "analysis_strong_energy": 0.16,
        "analysis_high_onset_threshold": 0.66,
        "analysis_high_onset_min_chance": 0.98,
        "long_energy_threshold": 0.28,
        "long_note_chance": 0.55,
        "tap_gap": 0.24,
        "long_gap": 0.78,
        "lane_change_chance": 1.0,
        "wide_lane_jump_chance": 0.70,
        "color_switch_chance": 0.82,
        "good_note_chance": 0.52,
    },
}

PEAK_THRESHOLDS = {
    "normal": 0.46,
    "hard": 0.46,
}


def normalize_envelope(source, percentile):
    if len(source) == 0:
        return source
    reference = max(0.000001, float(np.percentile(source, percentile * 100.0)))
    return np.clip(source / reference, 0.0, 1.0)


def average_envelope(envelope, rate, start_time, end_time):
    if len(envelope) == 0 or rate <= 0:
        return 0.0
    first = int(np.clip(math.floor(start_time * rate), 0, len(envelope) - 1))
    last = int(np.clip(math.ceil(end_time * rate), first, len(envelope) - 1))
    return float(np.mean(envelope[first:last + 1]))


def find_first_audible(energy, rate, fallback):
    for i, value in enumerate(energy):
        if value >= 0.05:
            return i / rate
    return fallback


def find_last_audible(energy, rate, fallback):
    for i in range(len(energy) - 1, -1, -1):
        if energy[i] >= 0.05:
            return i / rate
    return fallback


def create_stable_seed(value):
    h = 17
    for ch in value:
        h = ((h * 31 + ord(ch)) + 2**31) % 2**32 - 2**31
    return 2**31 - 1 if h == -2**31 else abs(h)


def analyze_audio(path):
    audio, sr = librosa.load(path, sr=None, mono=True)
    hop = max(128, int(round(sr / ANALYSIS_TARGET_RATE)))
    total_hops = max(1, len(audio) // hop)
    audio = audio[:total_hops * hop]
    frames = audio.reshape(total_hops, hop)
    energy = np.sqrt(np.mean(frames * frames, axis=1))
    transient = np.mean(np.abs(np.diff(frames, axis=1)), axis=1)
    norm_energy = normalize_envelope(energy, 0.92)
    norm_transient = normalize_envelope(transient, 0.92)

    onset = np.zeros_like(norm_energy)
    mean_window = 8
    for i in range(1, len(norm_energy)):
        first = max(0, i - mean_window)
        energy_mean = float(np.mean(norm_energy[first:i])) if i > first else 0.0
        transient_mean = float(np.mean(norm_transient[first:i])) if i > first else 0.0
        energy_flux = max(0.0, float(norm_energy[i]) - energy_mean * 0.92)
        transient_flux = max(0.0, float(norm_transient[i]) - transient_mean * 0.9)
        onset[i] = energy_flux * 0.68 + transient_flux * 0.32

    onset = normalize_envelope(onset, 0.94)
    return {
        "audio": audio,
        "sample_rate": sr,
        "rate": sr / float(hop),
        "energy": norm_energy,
        "onset": onset,
        "duration": len(audio) / float(sr),
    }


def peak_score(onset, energy, index, average_energy):
    i = int(np.clip(index, 0, len(onset) - 1))
    e_i = int(np.clip(index, 0, len(energy) - 1))
    energy_lift = min(1.0, float(energy[e_i]) / max(0.05, average_energy))
    return min(1.0, float(onset[i]) * 0.72 + float(energy[e_i]) * 0.20 + max(0.0, energy_lift - 0.75) * 0.12)


def build_score_array(onset, energy, average_energy):
    scores = np.zeros(len(onset), dtype=np.float32)
    for i in range(len(onset)):
        scores[i] = peak_score(onset, energy, i, average_energy)
    return scores


def choose_next_lane(current, rng, preset):
    if USE_SINGLE_LANE_CHART:
        return 0

    lane_count = 3
    clamped = max(0, min(lane_count - 1, current))
    if rng.random() > preset["lane_change_chance"]:
        return clamped
    wide_jump = rng.random() <= preset["wide_lane_jump_chance"]
    if wide_jump:
        if clamped == 0:
            return lane_count - 1
        if clamped == lane_count - 1:
            return 0
    if clamped == 0:
        return 1
    if clamped == lane_count - 1:
        return lane_count - 2
    return clamped + (-1 if rng.randrange(2) == 0 else 1)


def pick_good(rng, last_good, preset):
    is_good = (not last_good) if rng.random() <= preset["color_switch_chance"] else last_good
    if rng.random() <= 0.14:
        is_good = rng.random() <= preset["good_note_chance"]
    return is_good


def pick_chart_color(rng, last_good, run_length, preset):
    previous = last_good
    is_good = pick_good(rng, last_good, preset)
    if is_good == previous:
        run_length += 1
        if run_length >= 3:
            is_good = not is_good
            run_length = 0
    else:
        run_length = 0
    return is_good, run_length


def note_gap(kind, preset):
    return preset["long_gap"] if "Wheel" in kind else preset["tap_gap"]


def try_add_note(chart, time, kind, lane, preset):
    if chart:
        previous = chart[-1]
        gap_after_previous = note_gap(previous["kind"], preset)
        lead_in_for_next = max(preset["tap_gap"], WHEEL_LEAD_IN_GAP) if "Wheel" in kind else note_gap(kind, preset)
        if time - previous["time"] < max(gap_after_previous, lead_in_for_next):
            return False
    chart.append({"time": time, "kind": kind, "lane": 0 if USE_SINGLE_LANE_CHART else max(0, min(2, lane))})
    return True


def ensure_minimum_wheels(chart, preset):
    if len(chart) < 3:
        return
    duration = chart[-1]["time"] - chart[0]["time"]
    desired = max(2, int(math.floor(duration / 9.0)))
    wheel_count = sum(1 for note in chart if "Wheel" in note["kind"])
    if wheel_count >= desired:
        return
    last_wheel_time = -1e9
    for i in range(1, len(chart) - 1):
        if wheel_count >= desired:
            break
        note = chart[i]
        if "Wheel" in note["kind"]:
            last_wheel_time = note["time"]
            continue
        if (
            note["time"] - last_wheel_time < 6.0
            or note["time"] - chart[i - 1]["time"] < max(preset["tap_gap"], WHEEL_LEAD_IN_GAP)
            or chart[i + 1]["time"] - note["time"] < preset["long_gap"]
        ):
            continue
        note["kind"] = "GoodWheel" if note["kind"] == "GoodTap" else "BadWheel"
        last_wheel_time = note["time"]
        wheel_count += 1


def build_eq_chart(seed_name, analysis, max_duration, preset, threshold):
    onset = analysis["onset"]
    energy = analysis["energy"]
    rate = analysis["rate"]
    song_duration = analysis["duration"] if max_duration <= 0 else min(analysis["duration"], max_duration)
    seed = create_stable_seed(seed_name) ^ (2 * 7919)
    rng = random.Random(abs(seed))
    chart = []
    lane = 0 if USE_SINGLE_LANE_CHART else 1
    last_good = True
    run_length = 0
    first_audible = find_first_audible(energy, rate, 0.0)
    first_playable = max(0.85, first_audible + 0.08)
    audible_end = find_last_audible(energy, rate, song_duration)
    final_playable = max(first_playable, min(song_duration - 1.1, audible_end - 0.25))
    average_energy = max(0.05, average_envelope(energy, rate, first_playable, final_playable))
    first_index = int(np.clip(math.ceil(first_playable * rate), 1, len(onset) - 2))
    last_index = int(np.clip(math.floor(final_playable * rate), first_index, len(onset) - 2))
    candidates = []
    scores = build_score_array(onset, energy, average_energy)

    for i in range(first_index, last_index + 1):
        score = float(scores[i])
        if score < threshold:
            continue
        previous = float(scores[i - 1])
        nxt = float(scores[i + 1])
        if score < previous or score <= nxt:
            continue
        onset_strength = float(onset[i])
        energy_strength = float(energy[min(i, len(energy) - 1)])
        if energy_strength < 0.05 and onset_strength < threshold + 0.16:
            continue
        candidates.append({"time": i / rate, "score": score, "onset": onset_strength, "energy": energy_strength})
        certainty = float(np.interp(score, [threshold, 1.0], [0.0, 1.0]))
        chance = min(1.0, preset["analysis_strong_base"] + score * preset["analysis_strong_onset"] + energy_strength * preset["analysis_strong_energy"])
        if onset_strength >= preset["analysis_high_onset_threshold"]:
            chance = max(chance, preset["analysis_high_onset_min_chance"])
        chance = chance + (1.0 - chance) * certainty * 0.45
        if rng.random() > chance:
            continue
        note_time = i / rate
        lane = choose_next_lane(lane, rng, preset)
        is_good, run_length = pick_chart_color(rng, last_good, run_length, preset)
        last_good = is_good
        sustained = average_envelope(energy, rate, note_time, min(final_playable, note_time + 0.72))
        is_long = (
            sustained >= preset["long_energy_threshold"]
            and score >= threshold + 0.10
            and rng.random() <= min(0.88, preset["long_note_chance"] * (0.55 + (1.2 - 0.55) * certainty))
        )
        kind = ("Good" if is_good else "Bad") + ("Wheel" if is_long else "Tap")
        if not try_add_note(chart, note_time, kind, lane, preset) and is_long:
            try_add_note(chart, note_time, "GoodTap" if is_good else "BadTap", lane, preset)

    if not chart:
        strongest = max(range(first_index, last_index + 1), key=lambda idx: float(scores[idx]))
        chart.append({"time": strongest / rate, "kind": "GoodTap", "lane": 0})

    ensure_minimum_wheels(chart, preset)
    return chart, candidates, threshold, average_energy, scores, first_playable, final_playable


def load_font(size, bold=False):
    candidates = [
        r"C:\Windows\Fonts\segoeuib.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
    ]
    for path in candidates:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def draw_frame(t, duration, analysis, chart, candidates, threshold, score, song_name, difficulty_label, fonts):
    img = Image.new("RGB", (WIDTH, HEIGHT), (14, 10, 28))
    draw = ImageDraw.Draw(img)
    font_big = fonts["big"]
    font_med = fonts["med"]
    font_small = fonts["small"]
    font_tiny = fonts["tiny"]

    draw.rectangle((0, 0, WIDTH, HEIGHT), fill=(14, 10, 28))
    draw.rectangle((0, 0, WIDTH, 78), fill=(36, 13, 63))
    draw.text((32, 20), "EQ-driven note generation", fill=(255, 245, 255), font=font_big)
    draw.text((470, 28), f"{song_name}  |  {difficulty_label}  |  1 lane  |  {len(chart)} notes", fill=(198, 233, 255), font=font_small)

    onset = analysis["onset"]
    energy = analysis["energy"]
    rate = analysis["rate"]

    timeline = (58, 108, 1222, 276)
    x0, y0, x1, y1 = timeline
    draw.rounded_rectangle(timeline, radius=8, fill=(22, 20, 42), outline=(94, 67, 137), width=2)
    draw.text((x0 + 14, y0 + 10), "1. Analyze EQ / onset score", fill=(255, 255, 255), font=font_med)
    plot_left, plot_top, plot_right, plot_bottom = x0 + 20, y0 + 52, x1 - 20, y1 - 24
    draw.line((plot_left, plot_bottom - threshold * (plot_bottom - plot_top), plot_right, plot_bottom - threshold * (plot_bottom - plot_top)), fill=(255, 102, 177), width=2)
    draw.text((plot_right - 130, plot_bottom - threshold * (plot_bottom - plot_top) - 22), "threshold", fill=(255, 136, 195), font=font_tiny)

    samples = 600
    times = np.linspace(0, duration, samples)
    points = []
    energy_points = []
    for tt in times:
        idx = int(np.clip(round(tt * rate), 0, len(score) - 1))
        x = plot_left + int((tt / duration) * (plot_right - plot_left))
        points.append((x, plot_bottom - int(score[idx] * (plot_bottom - plot_top))))
        energy_points.append((x, plot_bottom - int(float(energy[min(idx, len(energy) - 1)]) * 0.62 * (plot_bottom - plot_top))))
    if len(energy_points) > 1:
        draw.line(energy_points, fill=(47, 110, 184), width=2)
    if len(points) > 1:
        draw.line(points, fill=(0, 232, 255), width=3)

    for candidate in candidates:
        if 0 <= candidate["time"] <= duration:
            x = plot_left + int((candidate["time"] / duration) * (plot_right - plot_left))
            y = plot_bottom - int(candidate["score"] * (plot_bottom - plot_top))
            draw.ellipse((x - 3, y - 3, x + 3, y + 3), fill=(255, 255, 255))

    for note in chart:
        if 0 <= note["time"] <= duration:
            x = plot_left + int((note["time"] / duration) * (plot_right - plot_left))
            color = (75, 156, 255) if note["kind"].startswith("Good") else (255, 75, 91)
            draw.line((x, plot_top, x, plot_bottom), fill=color, width=2)

    cursor_x = plot_left + int((t / duration) * (plot_right - plot_left))
    draw.line((cursor_x, plot_top - 8, cursor_x, plot_bottom + 8), fill=(255, 235, 80), width=3)

    eq_rect = (58, 308, 1222, 474)
    draw.rounded_rectangle(eq_rect, radius=8, fill=(18, 23, 45), outline=(61, 105, 156), width=2)
    draw.text((eq_rect[0] + 14, eq_rect[1] + 10), "2. Current EQ window: tall bright peaks become note candidates", fill=(255, 255, 255), font=font_med)
    bars = 64
    bar_gap = 4
    bar_area = (eq_rect[0] + 26, eq_rect[1] + 58, eq_rect[2] - 26, eq_rect[3] - 24)
    bar_w = (bar_area[2] - bar_area[0] - bar_gap * (bars - 1)) / bars
    window = 3.8
    for i in range(bars):
        frac = i / (bars - 1)
        st = t + (frac - 0.5) * window
        idx = int(np.clip(round(st * rate), 0, len(score) - 1))
        value = score[idx]
        h = max(4, int(value * (bar_area[3] - bar_area[1])))
        x = int(bar_area[0] + i * (bar_w + bar_gap))
        y = bar_area[3] - h
        hot = value >= threshold
        color = (255, 95, 183) if hot else (48, 194, 235)
        draw.rounded_rectangle((x, y, int(x + bar_w), bar_area[3]), radius=3, fill=color)
    threshold_y = bar_area[3] - int(threshold * (bar_area[3] - bar_area[1]))
    draw.line((bar_area[0], threshold_y, bar_area[2], threshold_y), fill=(255, 235, 80), width=2)

    lane_rect = (58, 506, 1222, 676)
    draw.rounded_rectangle(lane_rect, radius=8, fill=(15, 18, 35), outline=(116, 92, 174), width=2)
    draw.text((lane_rect[0] + 14, lane_rect[1] + 10), "3. Accepted notes enter the single game lane", fill=(255, 255, 255), font=font_med)
    lane_y = [lane_rect[1] + 96] if USE_SINGLE_LANE_CHART else [lane_rect[1] + 58, lane_rect[1] + 96, lane_rect[1] + 134]
    hit_x = lane_rect[0] + 130
    spawn_x = lane_rect[2] - 42
    speed = 250.0
    draw.line((hit_x, lane_rect[1] + 42, hit_x, lane_rect[3] - 18), fill=(255, 241, 97), width=4)
    draw.text((hit_x - 42, lane_rect[3] - 20), "HIT", fill=(255, 241, 97), font=font_tiny)
    for y in lane_y:
        draw.line((lane_rect[0] + 30, y, lane_rect[2] - 30, y), fill=(70, 74, 113), width=2)
    if USE_SINGLE_LANE_CHART:
        blue_wheel_y = lane_y[0] + 52
        draw.line((lane_rect[0] + 30, blue_wheel_y, lane_rect[2] - 30, blue_wheel_y), fill=(44, 64, 108), width=1)

    for note in chart:
        dt = note["time"] - t
        x = hit_x + dt * speed
        if x < lane_rect[0] + 20 or x > spawn_x:
            continue
        y = lane_y[note["lane"]]
        if note["kind"] == "GoodWheel":
            y += 52
        color = (69, 143, 255) if note["kind"].startswith("Good") else (255, 67, 81)
        outline = (215, 238, 255) if note["kind"].startswith("Good") else (255, 220, 225)
        if "Wheel" in note["kind"]:
            draw.rounded_rectangle((x - 16, y - 18, x + 16, y + 18), radius=8, fill=color, outline=outline, width=3)
            draw.line((x - 8, y, x + 8, y), fill=(255, 255, 255), width=3)
        else:
            draw.ellipse((x - 14, y - 14, x + 14, y + 14), fill=color, outline=outline, width=3)

    progress = min(1.0, t / duration)
    draw.rounded_rectangle((58, 690, 1222, 700), radius=5, fill=(45, 43, 66))
    draw.rounded_rectangle((58, 690, 58 + int((1222 - 58) * progress), 700), radius=5, fill=(255, 235, 80))
    draw.text((1090, 16), f"{t:05.2f}s", fill=(255, 241, 97), font=font_big)
    return np.asarray(img)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--song", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--duration", type=float, default=0.0)
    parser.add_argument("--title", default="")
    parser.add_argument("--difficulty", choices=sorted(PRESETS.keys()), default="normal")
    parser.add_argument("--stats-only", action="store_true")
    args = parser.parse_args()

    seed_name = os.path.splitext(os.path.basename(args.song))[0]
    song_name = args.title.strip() or seed_name
    preset = PRESETS[args.difficulty]
    threshold = PEAK_THRESHOLDS[args.difficulty]
    difficulty_label = args.difficulty.upper()
    analysis = analyze_audio(args.song)
    duration = analysis["duration"] if args.duration <= 0 else min(args.duration, analysis["duration"])
    chart, candidates, threshold, average_energy, scores, _, _ = build_eq_chart(seed_name, analysis, args.duration, preset, threshold)
    note_count = len([note for note in chart if note["time"] <= duration])
    wheel_count = len([note for note in chart if note["time"] <= duration and "Wheel" in note["kind"]])
    blue_wheel_count = len([note for note in chart if note["time"] <= duration and note["kind"] == "GoodWheel"])

    if args.stats_only:
        print(f"difficulty={difficulty_label} duration={duration:.2f}s notes={note_count} wheels={wheel_count} blue_wheels={blue_wheel_count} candidates={len(candidates)}")
        return

    fonts = {
        "big": load_font(32, True),
        "med": load_font(22, True),
        "small": load_font(16),
        "tiny": load_font(13),
    }

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        temp_video = os.path.join(tmp, "video_no_audio.mp4")
        writer = imageio.get_writer(temp_video, fps=FPS, codec="libx264", quality=8, macro_block_size=16)
        frame_count = int(duration * FPS)
        for frame in range(frame_count):
            t = frame / FPS
            writer.append_data(draw_frame(t, duration, analysis, chart, candidates, threshold, scores, song_name, difficulty_label, fonts))
        writer.close()

        ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
        subprocess.check_call([
            ffmpeg,
            "-y",
            "-i",
            temp_video,
            "-i",
            args.song,
            "-t",
            f"{duration:.3f}",
            "-map",
            "0:v:0",
            "-map",
            "1:a:0",
            "-c:v",
            "copy",
            "-c:a",
            "aac",
            "-shortest",
            args.out,
        ])

    print(f"created={args.out}")
    print(f"difficulty={difficulty_label} duration={duration:.2f}s notes={note_count} wheels={wheel_count} blue_wheels={blue_wheel_count} candidates={len(candidates)}")


if __name__ == "__main__":
    main()
