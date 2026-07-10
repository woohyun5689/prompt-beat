import argparse
import math
import os
import subprocess
import tempfile

import imageio.v2 as imageio
import imageio_ffmpeg
import numpy as np
from PIL import Image, ImageDraw

import make_eq_note_video as base


def clamp(value, low, high):
    return max(low, min(high, value))


def sample_envelope(envelope, rate, time):
    index = int(clamp(round(time * rate), 0, len(envelope) - 1))
    return float(envelope[index])


def get_beat_feel_target_slots(difficulty, density_scale):
    if difficulty == "easy":
        return 3 if density_scale >= 1.1 else 2
    if difficulty == "hard":
        return 4
    return 3 if density_scale >= 1.0 else 2


def build_bpm_grid_chart(seed_name, analysis, max_duration, preset, difficulty, bpm, first_beat):
    energy = analysis["energy"]
    onset = analysis["onset"]
    rate = analysis["rate"]
    duration = analysis["duration"] if max_duration <= 0 else min(analysis["duration"], max_duration)
    beat_duration = 60.0 / bpm
    beat_times = []
    t = max(0.0, first_beat)
    while t < duration:
        beat_times.append(t)
        t += beat_duration

    first_playable = max(0.85, first_beat)
    audible_end = base.find_last_audible(energy, rate, duration)
    final_playable = max(first_playable, min(duration - 1.1, audible_end - 0.35))
    song_average_energy = max(0.05, base.average_envelope(energy, rate, first_playable, final_playable))
    playable_beats = [i for i, beat in enumerate(beat_times) if first_playable <= beat <= final_playable]

    rng = base.random.Random(abs(base.create_stable_seed(seed_name) ^ (3 * 7919)))
    chart = []
    beat_grid = []
    lane = 0
    last_good = True
    color_run = 0
    last_wheel_beat = -100
    phrase_beats = base.BEATS_PER_BAR * 4
    slots_per_bar = base.BEATS_PER_BAR * 2

    for phrase_start in range(0, len(playable_beats), phrase_beats):
        phrase_length = min(phrase_beats, len(playable_beats) - phrase_start)
        if phrase_length <= 0:
            continue

        phrase_start_time = beat_times[playable_beats[phrase_start]]
        phrase_end_time = beat_times[playable_beats[phrase_start + phrase_length - 1]] + beat_duration
        slot_strength = [0.0] * slots_per_bar
        slot_samples = [0] * slots_per_bar

        for b in range(phrase_length):
            beat_index = playable_beats[phrase_start + b]
            beat_time = beat_times[beat_index]
            half_time = (beat_time + beat_times[beat_index + 1]) * 0.5 if beat_index + 1 < len(beat_times) else beat_time + beat_duration * 0.5
            slot_base = (b % base.BEATS_PER_BAR) * 2
            slot_strength[slot_base] += sample_envelope(onset, rate, beat_time)
            slot_samples[slot_base] += 1
            slot_strength[slot_base + 1] += sample_envelope(onset, rate, half_time)
            slot_samples[slot_base + 1] += 1

        for s in range(slots_per_bar):
            slot_strength[s] = slot_strength[s] / max(1, slot_samples[s]) if slot_samples[s] else 0.0

        section_energy = base.average_envelope(energy, rate, phrase_start_time, phrase_end_time)
        density_scale = clamp(section_energy / song_average_energy, 0.7, 1.2)
        target_slots = get_beat_feel_target_slots(difficulty, density_scale)

        chosen_slots = [False] * slots_per_bar
        chosen_slots[0] = True
        chosen_count = 1
        fill_slot = -1
        on_beat_target_slots = min(target_slots, base.BEATS_PER_BAR)
        while chosen_count < target_slots or fill_slot < 0:
            best = -1
            best_strength = -1.0
            for s in range(1, slots_per_bar):
                if chosen_slots[s] or s == fill_slot:
                    continue
                if chosen_count < on_beat_target_slots and s % 2 == 1:
                    continue
                if slot_strength[s] > best_strength:
                    best_strength = slot_strength[s]
                    best = s
            if best < 0:
                break
            if chosen_count < target_slots:
                chosen_slots[best] = True
                chosen_count += 1
            else:
                fill_slot = best

        for b in range(phrase_length):
            global_beat = phrase_start + b
            beat_index = playable_beats[global_beat]
            beat_time = beat_times[beat_index]
            half_time = (beat_time + beat_times[beat_index + 1]) * 0.5 if beat_index + 1 < len(beat_times) else beat_time + beat_duration * 0.5
            fill_bar = b >= phrase_length - base.BEATS_PER_BAR and phrase_length == phrase_beats
            slot_base = (b % base.BEATS_PER_BAR) * 2

            for half in range(2):
                slot = slot_base + half
                slot_center = beat_time if half == 0 else half_time
                beat_grid.append({"time": slot_center, "strong": chosen_slots[slot] or (fill_bar and slot == fill_slot)})
                if not chosen_slots[slot] and not (fill_bar and slot == fill_slot):
                    continue
                if slot_center > final_playable:
                    continue

                onset_strength = sample_envelope(onset, rate, slot_center)
                energy_strength = sample_envelope(energy, rate, slot_center)
                if energy_strength < 0.06 and onset_strength < 0.05:
                    continue

                lane = base.choose_next_lane(lane, rng, preset)
                is_good, color_run = base.pick_chart_color(rng, last_good, color_run, preset)
                last_good = is_good

                is_long = False
                beat_in_bar = b % base.BEATS_PER_BAR
                if half == 0 and beat_in_bar in (0, 2) and global_beat - last_wheel_beat >= base.BEATS_PER_BAR / 2:
                    sustained = base.average_envelope(energy, rate, slot_center, min(final_playable, slot_center + beat_duration * 1.5))
                    wheel_chance = preset["long_note_chance"]
                    if b == 0:
                        wheel_chance *= 2.4
                    elif beat_in_bar == 0:
                        wheel_chance *= 1.5
                    else:
                        wheel_chance *= 0.8
                    wheel_chance *= 0.24
                    is_long = sustained >= preset["long_energy_threshold"] and rng.random() <= min(0.92, wheel_chance)

                kind = ("Good" if is_good else "Bad") + ("Wheel" if is_long else "Tap")
                if base.try_add_note(chart, slot_center, kind, lane, preset):
                    if is_long:
                        last_wheel_beat = global_beat
                elif is_long:
                    base.try_add_note(chart, slot_center, "GoodTap" if is_good else "BadTap", lane, preset)

    if not chart:
        chart.append({"time": first_playable, "kind": "GoodTap", "lane": 0})

    base.ensure_minimum_wheels(chart, preset)
    return chart, beat_grid, first_playable, final_playable, beat_duration


def draw_frame(t, duration, analysis, chart, beat_grid, bpm, first_beat, beat_duration, song_name, difficulty_label, fonts):
    width = base.WIDTH
    height = base.HEIGHT
    img = Image.new("RGB", (width, height), (14, 10, 28))
    draw = ImageDraw.Draw(img)
    font_big = fonts["big"]
    font_med = fonts["med"]
    font_small = fonts["small"]
    font_tiny = fonts["tiny"]
    energy = analysis["energy"]
    onset = analysis["onset"]
    rate = analysis["rate"]

    draw.rectangle((0, 0, width, height), fill=(14, 10, 28))
    draw.rectangle((0, 0, width, 78), fill=(36, 13, 63))
    draw.text((32, 20), "BPM / beat-grid note generation", fill=(255, 245, 255), font=font_big)
    draw.text((548, 28), f"{song_name}  |  {difficulty_label}  |  123 BPM  |  1 lane  |  {len(chart)} notes", fill=(198, 233, 255), font=font_small)
    draw.text((1090, 16), f"{t:05.2f}s", fill=(255, 241, 97), font=font_big)

    timeline = (58, 108, 1222, 276)
    x0, y0, x1, y1 = timeline
    draw.rounded_rectangle(timeline, radius=8, fill=(22, 20, 42), outline=(94, 67, 137), width=2)
    draw.text((x0 + 14, y0 + 10), "1. BPM grid: first beat + n * beat duration", fill=(255, 255, 255), font=font_med)
    plot_left, plot_top, plot_right, plot_bottom = x0 + 20, y0 + 52, x1 - 20, y1 - 24

    samples = 600
    times = np.linspace(0, duration, samples)
    energy_points = []
    onset_points = []
    for tt in times:
        idx = int(np.clip(round(tt * rate), 0, len(onset) - 1))
        x = plot_left + int((tt / duration) * (plot_right - plot_left))
        energy_points.append((x, plot_bottom - int(float(energy[min(idx, len(energy) - 1)]) * 0.58 * (plot_bottom - plot_top))))
        onset_points.append((x, plot_bottom - int(float(onset[idx]) * 0.85 * (plot_bottom - plot_top))))
    draw.line(energy_points, fill=(47, 110, 184), width=2)
    draw.line(onset_points, fill=(0, 232, 255), width=2)

    for grid in beat_grid:
        if 0 <= grid["time"] <= duration:
            x = plot_left + int((grid["time"] / duration) * (plot_right - plot_left))
            color = (255, 255, 255) if grid["strong"] else (86, 68, 122)
            draw.line((x, plot_top, x, plot_bottom), fill=color, width=1)

    for note in chart:
        if 0 <= note["time"] <= duration:
            x = plot_left + int((note["time"] / duration) * (plot_right - plot_left))
            color = (75, 156, 255) if note["kind"].startswith("Good") else (255, 75, 91)
            draw.line((x, plot_top, x, plot_bottom), fill=color, width=2)

    cursor_x = plot_left + int((t / duration) * (plot_right - plot_left))
    draw.line((cursor_x, plot_top - 8, cursor_x, plot_bottom + 8), fill=(255, 235, 80), width=3)
    draw.text((plot_left, plot_bottom + 6), f"firstBeat {first_beat:.2f}s  |  beat {beat_duration:.3f}s", fill=(210, 220, 255), font=font_tiny)

    eq_rect = (58, 308, 1222, 474)
    draw.rounded_rectangle(eq_rect, radius=8, fill=(18, 23, 45), outline=(61, 105, 156), width=2)
    draw.text((eq_rect[0] + 14, eq_rect[1] + 10), "2. Current beat window: notes stay on grid, EQ only picks stronger slots", fill=(255, 255, 255), font=font_med)
    bars = 64
    bar_gap = 4
    bar_area = (eq_rect[0] + 26, eq_rect[1] + 58, eq_rect[2] - 26, eq_rect[3] - 24)
    bar_w = (bar_area[2] - bar_area[0] - bar_gap * (bars - 1)) / bars
    window = 3.8
    for i in range(bars):
        frac = i / (bars - 1)
        st = t + (frac - 0.5) * window
        idx = int(np.clip(round(st * rate), 0, len(onset) - 1))
        value = float(onset[idx]) * 0.72 + float(energy[min(idx, len(energy) - 1)]) * 0.20
        h = max(4, int(min(1.0, value) * (bar_area[3] - bar_area[1])))
        x = int(bar_area[0] + i * (bar_w + bar_gap))
        y = bar_area[3] - h
        nearest_grid = abs(((st - first_beat + beat_duration * 0.5) % (beat_duration * 0.5)) - beat_duration * 0.25)
        on_grid = nearest_grid < 0.018
        color = (255, 95, 183) if on_grid else (48, 194, 235)
        draw.rounded_rectangle((x, y, int(x + bar_w), bar_area[3]), radius=3, fill=color)
    cursor_grid_phase = ((t - first_beat) / beat_duration) if beat_duration > 0 else 0
    draw.text((bar_area[0], bar_area[1] - 24), f"beat phase {cursor_grid_phase % 1.0:.2f}", fill=(255, 235, 80), font=font_tiny)

    lane_rect = (58, 506, 1222, 676)
    draw.rounded_rectangle(lane_rect, radius=8, fill=(15, 18, 35), outline=(116, 92, 174), width=2)
    draw.text((lane_rect[0] + 14, lane_rect[1] + 10), "3. Accepted grid notes enter the single game lane", fill=(255, 255, 255), font=font_med)
    lane_y = [lane_rect[1] + 96]
    blue_wheel_y = lane_y[0] + 52
    hit_x = lane_rect[0] + 130
    spawn_x = lane_rect[2] - 42
    speed = 250.0
    draw.line((hit_x, lane_rect[1] + 42, hit_x, lane_rect[3] - 18), fill=(255, 241, 97), width=4)
    draw.text((hit_x - 42, lane_rect[3] - 20), "HIT", fill=(255, 241, 97), font=font_tiny)
    draw.line((lane_rect[0] + 30, lane_y[0], lane_rect[2] - 30, lane_y[0]), fill=(70, 74, 113), width=2)
    draw.line((lane_rect[0] + 30, blue_wheel_y, lane_rect[2] - 30, blue_wheel_y), fill=(44, 64, 108), width=1)

    for note in chart:
        dt = note["time"] - t
        x = hit_x + dt * speed
        if x < lane_rect[0] + 20 or x > spawn_x:
            continue
        y = blue_wheel_y if note["kind"] == "GoodWheel" else lane_y[0]
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
    return np.asarray(img)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--song", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--duration", type=float, default=0.0)
    parser.add_argument("--title", default="")
    parser.add_argument("--difficulty", choices=sorted(base.PRESETS.keys()), default="hard")
    parser.add_argument("--bpm", type=float, default=123.0)
    parser.add_argument("--first-beat", type=float, default=0.94)
    parser.add_argument("--stats-only", action="store_true")
    args = parser.parse_args()

    seed_name = os.path.splitext(os.path.basename(args.song))[0]
    song_name = args.title.strip() or seed_name
    preset = base.PRESETS[args.difficulty]
    difficulty_label = args.difficulty.upper()
    analysis = base.analyze_audio(args.song)
    duration = analysis["duration"] if args.duration <= 0 else min(args.duration, analysis["duration"])
    chart, beat_grid, _, _, beat_duration = build_bpm_grid_chart(seed_name, analysis, args.duration, preset, args.difficulty, args.bpm, args.first_beat)
    note_count = len([note for note in chart if note["time"] <= duration])
    wheel_count = len([note for note in chart if note["time"] <= duration and "Wheel" in note["kind"]])
    blue_wheel_count = len([note for note in chart if note["time"] <= duration and note["kind"] == "GoodWheel"])
    if args.stats_only:
        print(f"difficulty={difficulty_label} bpm={args.bpm:.2f} firstBeat={args.first_beat:.2f}s beat={beat_duration:.3f}s duration={duration:.2f}s notes={note_count} wheels={wheel_count} blue_wheels={blue_wheel_count} grid_slots={len(beat_grid)}")
        return

    fonts = {
        "big": base.load_font(32, True),
        "med": base.load_font(22, True),
        "small": base.load_font(16),
        "tiny": base.load_font(13),
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        temp_video = os.path.join(tmp, "video_no_audio.mp4")
        writer = imageio.get_writer(temp_video, fps=base.FPS, codec="libx264", quality=8, macro_block_size=16)
        frame_count = int(duration * base.FPS)
        for frame in range(frame_count):
            t = frame / base.FPS
            writer.append_data(draw_frame(t, duration, analysis, chart, beat_grid, args.bpm, args.first_beat, beat_duration, song_name, difficulty_label, fonts))
        writer.close()

        subprocess.check_call([
            imageio_ffmpeg.get_ffmpeg_exe(),
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
    print(f"difficulty={difficulty_label} bpm={args.bpm:.2f} firstBeat={args.first_beat:.2f}s beat={beat_duration:.3f}s duration={duration:.2f}s notes={note_count} wheels={wheel_count} blue_wheels={blue_wheel_count} grid_slots={len(beat_grid)}")


if __name__ == "__main__":
    main()
