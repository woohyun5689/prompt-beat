from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

from playwright.sync_api import TimeoutError as PlaywrightTimeoutError
from playwright.sync_api import sync_playwright


CREATE_BUTTON_RE = re.compile(r"(make|create|generate|\ub9cc\ub4e4\uae30|\uc0dd\uc131)", re.IGNORECASE)
CUSTOM_TAB_RE = re.compile(r"(custom|\ucee4\uc2a4\ud140)", re.IGNORECASE)
NEW_SONG_RE = re.compile(r"(new song|\uc0c8\ub85c\uc6b4\s*\ub178\ub798)", re.IGNORECASE)
STYLE_FIELD_RE = re.compile(r"(style|genre|\uc2a4\ud0c0\uc77c)", re.IGNORECASE)
LYRICS_FIELD_RE = re.compile(r"(lyrics|\uac00\uc0ac)", re.IGNORECASE)
LYRICS_GENERATE_BUTTON_RE = re.compile(r"^\s*(lyrics?\s*(generate|create)|generate\s*lyrics|\uac00\uc0ac\s*\uc0dd\uc131)\s*$", re.IGNORECASE)
LYRICS_PROMPT_FIELD_RE = re.compile(r"(lyrics?|theme|topic|\ucc3e\uace0\s*\uc788\ub294\s*\uac00\uc0ac|\uc8fc\uc81c|\ud14c\ub9c8)", re.IGNORECASE)
RANDOM_LYRICS_BUTTON_RE = re.compile(r"(\ubb34\uc791\uc704\s*\uac00\uc0ac\s*\uc0dd\uc131|\uac00\uc0ac\s*\uc0dd\uc131|\ub79c\ub364\s*\uac00\uc0ac|random\s*lyrics?|random\s*lyric\s*generate|generate\s*lyrics?)", re.IGNORECASE)
RANDOM_LYRICS_EXACT_BUTTON_RE = re.compile(r"^\s*(\ubb34\uc791\uc704\s*\uac00\uc0ac\s*\uc0dd\uc131|\ub79c\ub364\s*\uac00\uc0ac|random\s*lyrics?|random\s*lyric\s*generate)\s*$", re.IGNORECASE)
USE_LYRICS_BUTTON_RE = re.compile(r"(\uc774\s*\uac00\uc0ac(?:\ub97c)?\s*\uc0ac\uc6a9(?:\ud558\uc138\uc694)?|\uac00\uc0ac(?:\ub97c)?\s*\uc0ac\uc6a9|use\s*this\s*lyrics?|use\s*lyrics?|apply\s*lyrics?)", re.IGNORECASE)
USE_LYRICS_EXACT_TEXT_RE = re.compile(r"^\s*(\uc774\s*\uac00\uc0ac(?:\ub97c)?\s*\uc0ac\uc6a9(?:\ud558\uc138\uc694)?|\uac00\uc0ac(?:\ub97c)?\s*\uc0ac\uc6a9|use\s*this\s*lyrics?|use\s*lyrics?|apply\s*lyrics?)\s*$", re.IGNORECASE)
OPTIMIZE_GENERATED_LYRICS_RE = re.compile(r"^\s*(\uac00\uc0ac\s*\ucd5c\uc801\ud654|optimi[sz]e\s*lyrics?)\s*$", re.IGNORECASE)
OPTIMIZE_LYRICS_CREATE_RE = re.compile(r"(\uac00\uc0ac\s*\ucd5c\uc801\ud654\s*\ud6c4\s*\uc0dd\uc131|optimi[sz]e\s*lyrics?.*generate|generate.*optimi[sz]ed\s*lyrics?)", re.IGNORECASE)
AUDIO_URL_RE = re.compile(r"\.(mp3|wav|ogg|flac|m4a)(\?|$)", re.IGNORECASE)
DOWNLOAD_OPTION_RE = re.compile(r"(mp3|wav)\s*(download|\ub2e4\uc6b4\ub85c\ub4dc)", re.IGNORECASE)
SONG_DURATION_RE = re.compile(r"\b\d{2}:\d{2}\b")
LOGIN_PAGE_RE = re.compile(
    r"(login|log in|sign in|sign up|continue with google|google\ub85c|\ub85c\uadf8\uc778|\ud68c\uc6d0\uac00\uc785|\uacc4\uc815)",
    re.IGNORECASE,
)


def coerce_float(value, default: float) -> float:
    try:
        if value is None or value == "":
            return float(default)
        return float(value)
    except (TypeError, ValueError):
        return float(default)


def env_float(names: tuple[str, ...], default: float) -> float:
    for name in names:
        if name in os.environ:
            return coerce_float(os.environ.get(name), default)
    return float(default)


def job_float(job: dict, key: str, default: float, env_names: tuple[str, ...] = ()) -> float:
    if key in job:
        return coerce_float(job.get(key), default)
    return env_float(env_names, default) if env_names else float(default)


DEFAULT_AUTOMATION_TIMEOUT_SECONDS = env_float(
    ("MUREKA_AUTOMATION_TIMEOUT_SECONDS", "AI_RHYTHM_MUREKA_TIMEOUT_SECONDS"),
    600.0,
)
DEFAULT_LOGIN_WAIT_SECONDS = env_float(
    ("MUREKA_LOGIN_WAIT_SECONDS", "AI_RHYTHM_MUREKA_LOGIN_WAIT_SECONDS"),
    180.0,
)
DEFAULT_DOWNLOAD_WAIT_SECONDS = env_float(
    ("MUREKA_WAIT_SECONDS", "AI_RHYTHM_MUREKA_WAIT_SECONDS"),
    180.0,
)
DEFAULT_POLL_SECONDS = env_float(
    ("MUREKA_POLL_SECONDS", "AI_RHYTHM_MUREKA_POLL_SECONDS"),
    2.0,
)


def browser_paths() -> list[str]:
    candidates = [
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    ]
    found = []
    for candidate in candidates:
        if Path(candidate).exists():
            found.append(candidate)
    return found


def chrome_path() -> str:
    paths = browser_paths()
    return paths[0] if paths else ""


def cleanup_stale_profile_locks(profile_dir: Path) -> None:
    # Chrome can leave these behind after a forced close, causing the next
    # persistent-context launch to exit immediately before showing a window.
    for name in ("SingletonLock", "SingletonSocket", "SingletonCookie", "DevToolsActivePort"):
        try:
            path = profile_dir / name
            if path.exists():
                path.unlink()
        except OSError:
            pass


def open_browser_for_manual_login(executable: str, profile_dir: Path, create_url: str, start_time: float) -> None:
    try:
        subprocess.Popen(
            [
                executable,
                f"--user-data-dir={profile_dir}",
                "--new-window",
                create_url,
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        log(start_time, "Opened a visible browser window for Mureka login. Log in there, then retry generation.")
    except Exception as exc:
        log(start_time, f"Could not open manual login browser: {exc}")


def log(start_time: float, message: str) -> None:
    elapsed = time.time() - start_time
    line = f"[{elapsed:7.1f}s] {message}"
    try:
        print(line, flush=True)
    except UnicodeEncodeError:
        safe_line = line.encode("utf-8", errors="replace").decode("utf-8", errors="replace")
        try:
            sys.stdout.buffer.write((safe_line + "\n").encode("utf-8", errors="replace"))
            sys.stdout.flush()
        except Exception:
            print(line.encode("ascii", errors="replace").decode("ascii"), flush=True)


def truthy(value) -> bool:
    if isinstance(value, bool):
        return value
    return str(value or "").strip().lower() in {"1", "true", "yes", "on"}


def looks_like_login_page(page) -> bool:
    try:
        url = (page.url or "").lower()
        if any(token in url for token in ("login", "sign-in", "signin", "sign-up", "signup", "clerk")):
            return True
    except Exception:
        pass

    try:
        body_text = page.locator("body").inner_text(timeout=1000)
        return bool(LOGIN_PAGE_RE.search(body_text[:2500]))
    except Exception:
        return False


def click_matching_text(page, pattern: re.Pattern[str], label: str, timeout: int = 3000) -> bool:
    for locator in (
        page.get_by_role("tab", name=pattern),
        page.get_by_role("button", name=pattern),
        page.get_by_text(pattern),
    ):
        try:
            count = locator.count()
        except Exception:
            continue
        for index in range(min(count, 8)):
            item = locator.nth(index)
            try:
                if item.is_visible():
                    item.click(timeout=timeout)
                    page.wait_for_timeout(700)
                    return True
            except Exception:
                continue
    return False


def click_button_matching_text(page, pattern: re.Pattern[str], label: str, timeout: int = 3000) -> bool:
    for locator in (
        page.get_by_role("button", name=pattern),
        page.get_by_role("link", name=pattern),
    ):
        try:
            count = locator.count()
        except Exception:
            continue
        for index in range(min(count, 8)):
            item = locator.nth(index)
            try:
                if item.is_visible():
                    item.click(timeout=timeout)
                    page.wait_for_timeout(700)
                    return True
            except Exception:
                continue
    return False


def click_new_song_if_available(page) -> bool:
    return click_matching_text(page, NEW_SONG_RE, "new song", timeout=2000)


def click_custom_tab(page) -> bool:
    return click_matching_text(page, CUSTOM_TAB_RE, "custom tab", timeout=3000)


def field_context_text(item) -> str:
    try:
        return item.evaluate(
            """
            (el) => {
              const parts = [];
              const attrs = ['placeholder', 'aria-label', 'name', 'id', 'class'];
              for (const attr of attrs) parts.push(el.getAttribute(attr) || '');
              let node = el;
              for (let i = 0; i < 4 && node; i++, node = node.parentElement) {
                const text = (node.innerText || '').trim();
                if (text) parts.push(text.slice(0, 500));
              }
              return parts.join(' ');
            }
            """
        )
    except Exception:
        return ""


def fill_text_item(item, prompt: str) -> bool:
    try:
        item.scroll_into_view_if_needed(timeout=2000)
        item.click(timeout=3000)
        try:
            item.press("Control+A", timeout=1000)
            item.press("Backspace", timeout=1000)
        except Exception:
            pass
        try:
            item.fill(prompt, timeout=5000)
        except Exception:
            item.evaluate(
                """
                (el, value) => {
                  if (el.isContentEditable) {
                    el.textContent = value;
                  } else {
                    const proto = el.tagName === 'TEXTAREA'
                      ? window.HTMLTextAreaElement.prototype
                      : window.HTMLInputElement.prototype;
                    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                    if (setter) {
                      setter.call(el, value);
                    } else {
                      el.value = value;
                    }
                  }
                }
                """,
                prompt,
            )
        try:
            item.evaluate(
                """
                (el) => {
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                  el.dispatchEvent(new Event('change', { bubbles: true }));
                }
                """
            )
        except Exception:
            pass
        return True
    except Exception:
        return False


def collect_text_fields(page):
    fields = []
    for selector in ("textarea", "[contenteditable='true']", "input[type='text']"):
        locator = page.locator(selector)
        try:
            count = locator.count()
        except PlaywrightTimeoutError:
            continue
        for index in range(count):
            item = locator.nth(index)
            try:
                if not item.is_visible():
                    continue
                box = item.bounding_box() or {}
                width = float(box.get("width", 0))
                height = float(box.get("height", 0))
                if width < 80 or height < 20:
                    continue
                fields.append(
                    {
                        "area": width * height,
                        "y": float(box.get("y", 0)),
                        "x": float(box.get("x", 0)),
                        "item": item,
                        "context": field_context_text(item),
                        "selector": selector,
                    }
                )
            except PlaywrightTimeoutError:
                continue
    return fields


def fill_custom_style_field(page, prompt: str) -> bool:
    fields = collect_text_fields(page)
    if not fields:
        return False

    style_fields = [
        field for field in fields
        if STYLE_FIELD_RE.search(field["context"]) and not LYRICS_FIELD_RE.search(field["context"])
    ]
    if style_fields:
        candidates = sorted(style_fields, key=lambda field: field["area"], reverse=True)
    else:
        textareas = [field for field in fields if field["selector"] == "textarea"]
        if len(textareas) >= 2:
            candidates = [sorted(textareas, key=lambda field: field["y"])[1]]
        else:
            candidates = sorted(fields, key=lambda field: field["area"], reverse=True)

    for field in candidates:
        item = field["item"]
        if fill_text_item(item, prompt):
            return True
    return False


def text_field_value(item) -> str:
    try:
        value = item.evaluate(
            """
            (el) => {
              if ('value' in el) return el.value || '';
              return el.innerText || el.textContent || '';
            }
            """
        )
        return str(value or "")
    except Exception:
        return ""


def fill_lyrics_prompt_field(page, prompt: str) -> bool:
    fields = collect_text_fields(page)
    if not fields:
        return False

    prompt_fields = [
        field for field in fields
        if LYRICS_PROMPT_FIELD_RE.search(field["context"]) and not STYLE_FIELD_RE.search(field["context"])
    ]
    if prompt_fields:
        candidates = sorted(prompt_fields, key=lambda field: field["area"], reverse=True)
    else:
        candidates = sorted(fields, key=lambda field: field["area"], reverse=True)

    for field in candidates:
        item = field["item"]
        if fill_text_item(item, prompt):
            return True
    return False


def generated_lyrics_present(page, prompt: str) -> bool:
    for field in collect_text_fields(page):
        value = text_field_value(field["item"]).strip()
        if len(value) < max(40, min(len(prompt) + 10, 120)):
            continue
        if value.strip() == prompt.strip():
            continue
        if "\n" in value or LYRICS_FIELD_RE.search(field["context"]):
            return True
    return False


def click_random_lyrics_generate(page, start_time: float) -> bool:
    if click_matching_text(page, RANDOM_LYRICS_BUTTON_RE, "random lyrics generate", timeout=3000):
        return True

    try:
        candidates = page.evaluate(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('button,[role="button"],a,div,span'));
              const out = [];
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 20 || rect.height < 18) continue;
                const text = (el.innerText || el.textContent || '').trim();
                const aria = el.getAttribute('aria-label') || '';
                const title = el.getAttribute('title') || '';
                const label = `${text} ${aria} ${title}`.trim();
                if (!label) continue;
                const lower = label.toLowerCase();
                let score = 0;
                if (label.includes('무작위')) score += 8;
                if (label.includes('가사')) score += 5;
                if (label.includes('생성')) score += 3;
                if (lower.includes('random')) score += 8;
                if (lower.includes('lyric')) score += 5;
                if (lower.includes('generate') || lower.includes('create')) score += 3;
                if (score <= 0) continue;
                const button = el.closest('button,[role="button"],a') || el;
                const b = button.getBoundingClientRect();
                out.push({
                  score,
                  label: label.slice(0, 120),
                  x: b.left + b.width / 2,
                  y: b.top + b.height / 2,
                  width: b.width,
                  height: b.height
                });
              }
              return out.sort((a, b) => b.score - a.score || b.width * b.height - a.width * a.height).slice(0, 8);
            }
            """
        )
    except Exception:
        candidates = []

    if candidates:
        best = candidates[0]
        log(start_time, f"Clicking lyrics button candidate: {best.get('label', '')}")
        try:
            page.mouse.click(float(best["x"]), float(best["y"]))
            page.wait_for_timeout(800)
            return True
        except Exception:
            pass

    try:
        snapshot = page.evaluate(
            """
            () => Array.from(document.querySelectorAll('button,[role="button"],a'))
              .filter((el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 20 && rect.height > 18;
              })
              .map((el) => (el.innerText || el.textContent || el.getAttribute('aria-label') || '').trim())
              .filter(Boolean)
              .slice(0, 30)
            """
        )
        log(start_time, "Visible button labels: " + " | ".join(str(label)[:40] for label in snapshot))
    except Exception:
        pass

    return False


def click_random_lyrics_generate(page, start_time: float) -> bool:
    if click_button_matching_text(page, RANDOM_LYRICS_EXACT_BUTTON_RE, "random lyrics generate in modal", timeout=3000):
        return True

    try:
        candidates = page.evaluate(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('button,[role="button"],a,div,span'));
              const out = [];
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 20 || rect.height < 18) continue;
                const text = (el.innerText || el.textContent || '').trim();
                const aria = el.getAttribute('aria-label') || '';
                const title = el.getAttribute('title') || '';
                const label = `${text} ${aria} ${title}`.trim();
                if (!label) continue;
                const lower = label.toLowerCase();
                const hasLyrics = label.includes('\\uAC00\\uC0AC') || lower.includes('lyric');
                const hasGenerate = label.includes('\\uC0DD\\uC131') || lower.includes('generate') || lower.includes('create');
                const hasRandom = label.includes('\\uBB34\\uC791\\uC704') || lower.includes('random');
                if (!((hasLyrics && hasGenerate) || (hasRandom && hasLyrics))) continue;
                const target = el.closest('button,[role="button"],a') || el;
                const b = target.getBoundingClientRect();
                if (b.width < 40 || b.height < 24) continue;
                let score = 10;
                if (hasRandom) score += 8;
                if (b.top > window.innerHeight * 0.45) score += 6;
                if (b.left > window.innerWidth * 0.45) score += 4;
                if (label.includes('\\uC785\\uB825\\uD558\\uC138\\uC694')) score -= 8;
                out.push({
                  score,
                  label: label.slice(0, 120),
                  x: b.left + b.width / 2,
                  y: b.top + b.height / 2,
                  width: b.width,
                  height: b.height,
                  top: b.top,
                  left: b.left
                });
              }
              return out.sort((a, b) => b.score - a.score || b.top - a.top || b.left - a.left).slice(0, 8);
            }
            """
        )
    except Exception:
        candidates = []

    if candidates:
        best = candidates[0]
        log(start_time, f"Clicking lyrics generate button candidate: {best.get('label', '')} @ {best.get('x')},{best.get('y')}")
        try:
            page.mouse.click(float(best["x"]), float(best["y"]))
            page.wait_for_timeout(800)
            return True
        except Exception:
            pass

    try:
        snapshot = page.evaluate(
            """
            () => Array.from(document.querySelectorAll('button,[role="button"],a'))
              .filter((el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 20 && rect.height > 18;
              })
              .map((el) => (el.innerText || el.textContent || el.getAttribute('aria-label') || '').trim())
              .filter(Boolean)
              .slice(0, 40)
            """
        )
        log(start_time, "Visible button labels: " + " | ".join(str(label)[:40] for label in snapshot))
    except Exception:
        pass

    return False


def click_optimize_generated_lyrics(page, start_time: float, timeout: int = 3000, log_missing: bool = True) -> bool:
    for locator in (
        page.get_by_role("button", name=OPTIMIZE_GENERATED_LYRICS_RE),
        page.get_by_text(OPTIMIZE_GENERATED_LYRICS_RE),
    ):
        try:
            count = locator.count()
        except Exception:
            continue
        for index in range(min(count, 8)):
            item = locator.nth(index)
            try:
                if not item.is_visible():
                    continue
                box = item.bounding_box() or {}
                x = float(box.get("x", 0)) + float(box.get("width", 0)) / 2
                y = float(box.get("y", 0)) + float(box.get("height", 0)) / 2
                log(start_time, f"Clicking generated lyrics optimize locator @ {x},{y}")
                item.scroll_into_view_if_needed(timeout=timeout)
                item.click(timeout=timeout, force=True)
                page.wait_for_timeout(1800)
                return True
            except Exception:
                continue

    try:
        candidates = page.evaluate(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('button,[role="button"],a,div,span'));
              const out = [];
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 40 || rect.height < 24) continue;
                const text = (el.innerText || el.textContent || '').trim();
                const aria = el.getAttribute('aria-label') || '';
                const title = el.getAttribute('title') || '';
                const label = `${text} ${aria} ${title}`.trim();
                const compact = label.replace(/\\s+/g, '');
                const lower = label.toLowerCase();
                const exactOptimize =
                  compact === '\\uAC00\\uC0AC\\uCD5C\\uC801\\uD654' ||
                  lower === 'optimize lyrics' ||
                  lower === 'optimise lyrics';
                if (!exactOptimize) continue;
                const target = el.closest('button,[role="button"],a') || el;
                const b = target.getBoundingClientRect();
                if (b.width < 40 || b.height < 24) continue;
                let score = 20;
                if (b.top > window.innerHeight * 0.45) score += 8;
                if (b.left < window.innerWidth * 0.5) score += 8;
                if (label.includes('\\n')) score -= 20;
                if (label.length > 30) score -= 20;
                out.push({
                  score,
                  label: label.slice(0, 80),
                  x: b.left + b.width / 2,
                  y: b.top + b.height / 2,
                  width: b.width,
                  height: b.height,
                  top: b.top,
                  left: b.left
                });
              }
              return out.sort((a, b) => b.score - a.score || a.left - b.left || b.top - a.top).slice(0, 8);
            }
            """
        )
    except Exception:
        candidates = []

    if candidates:
        best = candidates[0]
        log(start_time, f"Clicking generated lyrics optimize candidate: {best.get('label', '')} @ {best.get('x')},{best.get('y')}")
        try:
            page.mouse.click(float(best["x"]), float(best["y"]))
            page.wait_for_timeout(1800)
            return True
        except Exception:
            pass

    if log_missing:
        try:
            snapshot = page.evaluate(
                """
                () => Array.from(document.querySelectorAll('button,[role="button"],a'))
                  .filter((el) => {
                    const rect = el.getBoundingClientRect();
                    const style = getComputedStyle(el);
                    return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 20 && rect.height > 18;
                  })
                  .map((el) => (el.innerText || el.textContent || el.getAttribute('aria-label') || '').trim())
                  .filter(Boolean)
                  .slice(0, 40)
                """
            )
            log(start_time, "Visible button labels before lyrics optimize: " + " | ".join(str(label)[:40] for label in snapshot))
        except Exception:
            pass

    return False


def use_generated_lyrics_button_visible(page) -> bool:
    try:
        return bool(page.evaluate(
            """
            () => {
              const isVisible = (el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return style.visibility !== 'hidden' && style.display !== 'none' && rect.width >= 30 && rect.height >= 20;
              };
              const isUseLyrics = (label) => {
                const compact = (label || '').replace(/\\s+/g, '');
                const lower = (label || '').toLowerCase();
                return compact === '\\uC774\\uAC00\\uC0AC\\uC0AC\\uC6A9' ||
                  compact === '\\uC774\\uAC00\\uC0AC\\uB97C\\uC0AC\\uC6A9' ||
                  compact === '\\uC774\\uAC00\\uC0AC\\uB97C\\uC0AC\\uC6A9\\uD558\\uC138\\uC694' ||
                  compact === '\\uAC00\\uC0AC\\uC0AC\\uC6A9' ||
                  compact === '\\uAC00\\uC0AC\\uB97C\\uC0AC\\uC6A9' ||
                  lower === 'use this lyrics' ||
                  lower === 'use lyrics' ||
                  lower === 'apply lyrics';
              };
              return Array.from(document.querySelectorAll('button,[role="button"],a,div,span'))
                .some((el) => isVisible(el) && isUseLyrics((el.innerText || el.textContent || el.getAttribute('aria-label') || '').trim()));
            }
            """
        ))
    except Exception:
        return False


def use_generated_lyrics_applied(page) -> bool:
    for _ in range(8):
        page.wait_for_timeout(350)
        if not use_generated_lyrics_button_visible(page):
            return True
    return False


def click_use_generated_lyrics(page, start_time: float, timeout: int = 3000, log_missing: bool = True) -> bool:
    for locator in (
        page.get_by_role("button", name=USE_LYRICS_BUTTON_RE),
        page.get_by_text(USE_LYRICS_EXACT_TEXT_RE),
    ):
        try:
            count = locator.count()
        except Exception:
            continue
        for index in range(min(count, 8)):
            item = locator.nth(index)
            try:
                if not item.is_visible():
                    continue
                box = item.bounding_box() or {}
                x = float(box.get("x", 0)) + float(box.get("width", 0)) / 2
                y = float(box.get("y", 0)) + float(box.get("height", 0)) / 2
                log(start_time, f"Clicking use lyrics locator @ {x},{y}")
                item.scroll_into_view_if_needed(timeout=timeout)
                item.click(timeout=timeout, force=True)
                if use_generated_lyrics_applied(page):
                    return True
                page.mouse.click(x, y)
                if use_generated_lyrics_applied(page):
                    return True
            except Exception:
                continue

    try:
        candidates = page.evaluate(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('button,[role="button"],a,div,span'));
              const out = [];
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 40 || rect.height < 24) continue;
                const text = (el.innerText || el.textContent || '').trim();
                const aria = el.getAttribute('aria-label') || '';
                const title = el.getAttribute('title') || '';
                const label = `${text} ${aria} ${title}`.trim();
                if (!label) continue;
                const lower = label.toLowerCase();
                const hasLyrics = label.includes('\\uAC00\\uC0AC') || lower.includes('lyric');
                const hasUse = label.includes('\\uC0AC\\uC6A9') || lower.includes('use') || lower.includes('apply');
                if (!(hasLyrics && hasUse)) continue;
                const target = el.closest('button,[role="button"],a') || el;
                const b = target.getBoundingClientRect();
                if (b.width < 40 || b.height < 24) continue;
                const compact = label.replace(/\\s+/g, '');
                const exactUse =
                  compact === '\\uC774\\uAC00\\uC0AC\\uC0AC\\uC6A9' ||
                  compact === '\\uC774\\uAC00\\uC0AC\\uB97C\\uC0AC\\uC6A9' ||
                  compact === '\\uC774\\uAC00\\uC0AC\\uB97C\\uC0AC\\uC6A9\\uD558\\uC138\\uC694' ||
                  compact === '\\uAC00\\uC0AC\\uC0AC\\uC6A9' ||
                  compact === '\\uAC00\\uC0AC\\uB97C\\uC0AC\\uC6A9';
                let score = 10;
                if (exactUse) score += 60;
                if (b.top > window.innerHeight * 0.45) score += 4;
                if (b.left > window.innerWidth * 0.5) score += 8;
                if (label.includes('\\n')) score -= 25;
                if (label.length > 40) score -= Math.min(40, Math.floor(label.length / 3));
                if (b.width > 260 || b.height > 90) score -= 30;
                out.push({
                  score,
                  label: label.slice(0, 120),
                  x: b.left + b.width / 2,
                  y: b.top + b.height / 2,
                  width: b.width,
                  height: b.height,
                  top: b.top,
                  left: b.left
                });
              }
              return out.sort((a, b) => b.score - a.score || b.left - a.left || b.top - a.top).slice(0, 8);
            }
            """
        )
    except Exception:
        candidates = []

    if candidates:
        best = candidates[0]
        if float(best.get("score", 0)) < 20:
            if log_missing:
                log(start_time, f"Skipping weak use lyrics candidate: {best.get('label', '')} @ {best.get('x')},{best.get('y')}")
            return False
        log(start_time, f"Clicking use lyrics button candidate: {best.get('label', '')} @ {best.get('x')},{best.get('y')}")
        try:
            page.mouse.click(float(best["x"]), float(best["y"]))
            if use_generated_lyrics_applied(page):
                return True
        except Exception:
            pass

    if not log_missing:
        return False

    try:
        snapshot = page.evaluate(
            """
            () => Array.from(document.querySelectorAll('button,[role="button"],a'))
              .filter((el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 20 && rect.height > 18;
              })
              .map((el) => (el.innerText || el.textContent || el.getAttribute('aria-label') || '').trim())
              .filter(Boolean)
              .slice(0, 40)
            """
        )
        log(start_time, "Visible button labels after lyrics generation: " + " | ".join(str(label)[:40] for label in snapshot))
    except Exception:
        pass

    return False


def generate_lyrics_from_prompt(
    page,
    prompt: str,
    start_time: float,
    timeout_seconds: float,
    poll_seconds: float,
) -> bool | None:
    if not click_matching_text(page, LYRICS_GENERATE_BUTTON_RE, "lyrics generate", timeout=2500):
        return None

    log(start_time, "Lyrics generation panel opened.")
    page.wait_for_timeout(800)

    if not fill_lyrics_prompt_field(page, prompt):
        log(start_time, "Lyrics prompt field was not found.")
        return False

    log(start_time, "Prompt filled into the lyrics prompt field.")
    if not click_random_lyrics_generate(page, start_time):
        log(start_time, "Random lyrics generate button was not found.")
        return False

    log(start_time, "Random lyrics generate button clicked.")
    deadline = time.time() + max(30.0, timeout_seconds)
    optimized_lyrics = False
    poll_ms = int(max(0.5, poll_seconds) * 1000)
    while time.time() < deadline:
        if not optimized_lyrics:
            if click_optimize_generated_lyrics(page, start_time, timeout=1000, log_missing=False):
                optimized_lyrics = True
                log(start_time, "Generated lyrics optimize button clicked.")
                continue
        elif click_use_generated_lyrics(page, start_time, timeout=1000, log_missing=False):
            log(start_time, "Optimized lyrics detected and use button clicked.")
            return True
        page.wait_for_timeout(poll_ms)

    if not optimized_lyrics:
        log(start_time, "Timed out while waiting for the generated lyrics optimize button.")
        click_optimize_generated_lyrics(page, start_time, timeout=2000, log_missing=True)
    else:
        log(start_time, "Timed out while waiting for the use generated lyrics button after lyrics optimize.")
        click_use_generated_lyrics(page, start_time, timeout=2000, log_missing=True)
    return False


def fill_largest_text_field(page, prompt: str) -> bool:
    fields = []
    for selector in ("textarea", "[contenteditable='true']", "input[type='text']"):
        locator = page.locator(selector)
        try:
            count = locator.count()
        except PlaywrightTimeoutError:
            continue
        for index in range(count):
            item = locator.nth(index)
            try:
                if not item.is_visible():
                    continue
                box = item.bounding_box() or {}
                area = float(box.get("width", 0)) * float(box.get("height", 0))
                fields.append((area, item))
            except PlaywrightTimeoutError:
                continue

    for _area, item in sorted(fields, key=lambda pair: pair[0], reverse=True):
        try:
            item.click(timeout=3000)
            item.fill(prompt, timeout=5000)
            return True
        except Exception:
            continue
    return False


def click_easy_tab(page) -> None:
    for locator in (
        page.get_by_text(EASY_TAB_RE),
        page.get_by_role("tab", name=EASY_TAB_RE),
        page.get_by_role("button", name=EASY_TAB_RE),
    ):
        try:
            if locator.count() > 0:
                locator.first.click(timeout=2000)
                page.wait_for_timeout(700)
                return
        except Exception:
            continue


def click_create(page) -> bool:
    for locator in (
        page.get_by_role("button", name=CREATE_BUTTON_RE),
        page.get_by_text(CREATE_BUTTON_RE),
    ):
        try:
            if locator.count() > 0:
                locator.last.click(timeout=5000)
                return True
        except Exception:
            continue
    return False


def find_audio_urls(page) -> list[str]:
    try:
        urls = page.evaluate(
            """
            () => Array.from(document.querySelectorAll('audio,source,video,a'))
              .map((el) => el.src || el.href || '')
              .filter(Boolean)
            """
        )
    except Exception:
        return []
    seen = []
    for url in urls:
        if isinstance(url, str) and AUDIO_URL_RE.search(url) and url not in seen:
            seen.append(url)
    return seen


def song_signatures(page) -> list[str]:
    try:
        text = page.locator("body").inner_text(timeout=2000)
    except Exception:
        return []

    lines = [line.strip() for line in text.splitlines() if line.strip()]
    signatures: list[str] = []
    for index, line in enumerate(lines):
        if not SONG_DURATION_RE.search(line):
            continue
        title = ""
        for prior in range(index - 1, max(-1, index - 5), -1):
            candidate = lines[prior].replace("V9", "").strip()
            if candidate and candidate not in {"\ub77c\uc774\ube0c\ub7ec\ub9ac"}:
                title = candidate
                break
        signature = f"{title}|{line[:120]}"
        if signature not in signatures:
            signatures.append(signature)
    return signatures[:12]


def save_job_screenshot(page, job_path: Path, label: str) -> None:
    try:
        page.screenshot(path=str(job_path.with_suffix(f".{label}.png")), full_page=False)
    except Exception:
        pass


def click_optimize_lyrics_warning_if_available(page, start_time: float) -> bool:
    for locator in (
        page.get_by_role("button", name=OPTIMIZE_LYRICS_CREATE_RE),
        page.get_by_text(OPTIMIZE_LYRICS_CREATE_RE),
    ):
        try:
            count = locator.count()
        except Exception:
            continue
        for index in range(min(count, 8)):
            item = locator.nth(index)
            try:
                if not item.is_visible():
                    continue
                box = item.bounding_box() or {}
                x = float(box.get("x", 0)) + float(box.get("width", 0)) / 2
                y = float(box.get("y", 0)) + float(box.get("height", 0)) / 2
                log(start_time, f"Clicking lyrics optimize then generate button @ {x},{y}")
                item.scroll_into_view_if_needed(timeout=2000)
                item.click(timeout=3000, force=True)
                page.wait_for_timeout(1200)
                return True
            except Exception:
                continue

    try:
        candidates = page.evaluate(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('button,[role="button"],a,div,span'));
              const out = [];
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 80 || rect.height < 24) continue;
                const text = (el.innerText || el.textContent || '').trim();
                const aria = el.getAttribute('aria-label') || '';
                const title = el.getAttribute('title') || '';
                const label = `${text} ${aria} ${title}`.trim();
                const compact = label.replace(/\\s+/g, '');
                if (!compact.includes('\\uAC00\\uC0AC\\uCD5C\\uC801\\uD654\\uD6C4\\uC0DD\\uC131')) continue;
                const target = el.closest('button,[role="button"],a') || el;
                const b = target.getBoundingClientRect();
                if (b.width < 80 || b.height < 24) continue;
                out.push({
                  label: label.slice(0, 120),
                  x: b.left + b.width / 2,
                  y: b.top + b.height / 2,
                  width: b.width,
                  height: b.height,
                  top: b.top,
                  left: b.left
                });
              }
              return out.sort((a, b) => a.top - b.top || a.left - b.left).slice(0, 4);
            }
            """
        )
    except Exception:
        candidates = []

    if candidates:
        best = candidates[0]
        log(start_time, f"Clicking lyrics optimize then generate candidate: {best.get('label', '')} @ {best.get('x')},{best.get('y')}")
        try:
            page.mouse.click(float(best["x"]), float(best["y"]))
            page.wait_for_timeout(1200)
            return True
        except Exception:
            pass
    return False


def complete_post_warning_lyrics_flow(
    page,
    start_time: float,
    timeout_seconds: float,
    poll_seconds: float,
) -> bool:
    deadline = time.time() + max(30.0, timeout_seconds)
    lyrics_generation_clicked = False
    optimized_lyrics = False
    poll_ms = int(max(0.5, poll_seconds) * 1000)
    while time.time() < deadline:
        if not optimized_lyrics:
            if not lyrics_generation_clicked and click_random_lyrics_generate(page, start_time):
                lyrics_generation_clicked = True
                log(start_time, "Post-warning lyrics generate button clicked.")
                continue

            if click_optimize_generated_lyrics(page, start_time, timeout=1000, log_missing=False):
                optimized_lyrics = True
                log(start_time, "Post-warning generated lyrics optimize button clicked.")
                continue
        elif click_use_generated_lyrics(page, start_time, timeout=1000, log_missing=False):
            log(start_time, "Post-warning optimized lyrics use button clicked.")
            if click_create(page):
                log(start_time, "Mureka create button clicked after post-warning lyrics flow.")
                return True
            log(start_time, "Post-warning lyrics were applied, but create button was not found.")
            return False

        page.wait_for_timeout(poll_ms)

    if optimized_lyrics:
        log(start_time, "Timed out waiting for post-warning use lyrics button.")
    elif lyrics_generation_clicked:
        log(start_time, "Timed out waiting for post-warning generated lyrics optimize button after lyrics generation.")
    else:
        log(start_time, "Timed out waiting for post-warning lyrics generate button or generated lyrics optimize button.")
    return False


def wait_for_new_song(
    page,
    baseline: list[str],
    start_time: float,
    timeout_seconds: float,
    job_path: Path,
    post_warning_timeout_seconds: float,
    poll_seconds: float,
) -> None:
    deadline = time.time() + max(1.0, timeout_seconds)
    last_log = 0.0
    poll_ms = int(max(0.5, poll_seconds) * 1000)
    while time.time() < deadline:
        if click_optimize_lyrics_warning_if_available(page, start_time):
            save_job_screenshot(page, job_path, "lyrics_optimized_create")
            complete_post_warning_lyrics_flow(page, start_time, post_warning_timeout_seconds, poll_seconds)
            last_log = 0.0
            continue

        current = song_signatures(page)
        new_items = [item for item in current if item not in baseline]
        if new_items:
            log(start_time, f"Possible generated song detected: {new_items[0]}")
            save_job_screenshot(page, job_path, "generated")
            return

        elapsed = time.time() - start_time
        if elapsed - last_log >= 30.0:
            log(start_time, "Waiting for Mureka generation to appear in the library.")
            last_log = elapsed
        page.wait_for_timeout(poll_ms)


def save_audio_from_url(context, url: str, download_dir: Path) -> Path | None:
    try:
        response = context.request.get(url, timeout=60000)
        if not response.ok:
            return None
        suffix = Path(url.split("?", 1)[0]).suffix.lower() or ".mp3"
        if suffix not in {".mp3", ".wav", ".ogg", ".flac", ".m4a"}:
            suffix = ".mp3"
        out_path = download_dir / f"mureka_auto_{int(time.time())}{suffix}"
        out_path.write_bytes(response.body())
        return out_path
    except Exception:
        return None


def save_download(download, download_dir: Path) -> Path:
    suggested = download.suggested_filename or f"mureka_auto_{int(time.time())}.mp3"
    target = download_dir / suggested
    stem = target.stem
    suffix = target.suffix or ".mp3"
    counter = 1
    while target.exists():
        target = download_dir / f"{stem}_{counter}{suffix}"
        counter += 1
    download.save_as(str(target))
    try:
        source = Path(download.path())
        if source.exists() and source.resolve() != target.resolve() and source.parent.resolve() == download_dir.resolve():
            source.unlink()
    except Exception:
        pass
    return target


def first_row_download_points(page) -> list[dict]:
    try:
        points = page.evaluate(
            """
            () => {
              const candidates = [];
              const nodes = Array.from(document.querySelectorAll('button,[role="button"],a,svg'));
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 6 || rect.height < 6) continue;
                if (rect.left < 730 || rect.left > 980 || rect.top < 250 || rect.top > 420) continue;
                const text = (el.innerText || el.textContent || '').trim();
                const aria = el.getAttribute('aria-label') || '';
                const title = el.getAttribute('title') || '';
                candidates.push({
                  x: rect.left + rect.width / 2,
                  y: rect.top + rect.height / 2,
                  w: rect.width,
                  h: rect.height,
                  text,
                  aria,
                  title,
                  tag: el.tagName
                });
              }
              return candidates;
            }
            """
        )
    except Exception:
        return []

    labeled = [
        point for point in points
        if re.search(
            r"(download|\ub2e4\uc6b4\ub85c\ub4dc)",
            " ".join(str(point.get(key, "")) for key in ("text", "aria", "title")),
            re.IGNORECASE,
        )
    ]
    if labeled:
        return sorted(labeled, key=lambda point: (float(point["y"]), float(point["x"])))

    lower_icon_row = [
        point for point in points
        if 330 <= float(point.get("y", 0)) <= 375 and 840 <= float(point.get("x", 0)) <= 930
    ]
    lower_icon_row = sorted(lower_icon_row, key=lambda point: float(point["x"]))
    if len(lower_icon_row) >= 3:
        return [lower_icon_row[2]]
    return lower_icon_row


def _legacy_direct_try_click_download(page, download_dir: Path) -> Path | None:
    selectors = [
        "a[download]",
        "button[aria-label*='download' i]",
        "button[title*='download' i]",
        "[aria-label*='download' i]",
        "[title*='download' i]",
        "button[aria-label*='다운' i]",
        "[aria-label*='다운' i]",
    ]
    for selector in selectors:
        locator = page.locator(selector)
        try:
            count = min(locator.count(), 8)
        except Exception:
            continue
        for index in range(count):
            item = locator.nth(index)
            try:
                if not item.is_visible():
                    continue
                with page.expect_download(timeout=7000) as download_info:
                    item.click(timeout=3000)
                download = download_info.value
                suggested = download.suggested_filename or f"mureka_auto_{int(time.time())}.mp3"
                target = download_dir / suggested
                download.save_as(str(target))
                return target
            except Exception:
                continue
    return None


def first_row_more_points(page) -> list[dict]:
    try:
        return page.evaluate(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('button,[role="button"],a'));
              const rows = [];
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 16 || rect.height < 16) continue;
                if (rect.top < 245 || rect.top > 385) continue;
                const text = (el.innerText || el.textContent || '').trim();
                const aria = el.getAttribute('aria-label') || '';
                const title = el.getAttribute('title') || '';
                const label = `${text} ${aria} ${title}`;
                if (label.includes('...') || label.includes('⋯') || label.includes('더보기') || label.toLowerCase().includes('more') || rect.left > window.innerWidth - 100) {
                  rows.push({
                    x: rect.left + rect.width / 2,
                    y: rect.top + rect.height / 2,
                    left: rect.left,
                    top: rect.top,
                    text,
                    aria,
                    title
                  });
                }
              }
              return rows.sort((a, b) => (a.top - b.top) || (b.left - a.left));
            }
            """
        )
    except Exception:
        return []


def _legacy_click_menu_download_parent(page) -> bool:
    download_re = re.compile(r"^\s*(download|\ub2e4\uc6b4\ub85c\ub4dc)\s*$", re.IGNORECASE)
    for locator in (
        page.get_by_role("menuitem", name=download_re),
        page.get_by_role("button", name=download_re),
        page.get_by_text(download_re),
    ):
        try:
            count = locator.count()
        except Exception:
            continue
        for index in range(min(count, 8)):
            item = locator.nth(index)
            try:
                if not item.is_visible():
                    continue
                item.click(timeout=3000)
                page.wait_for_timeout(700)
                return True
            except Exception:
                try:
                    item.hover(timeout=3000)
                    page.wait_for_timeout(700)
                    return True
                except Exception:
                    continue
    return False


def click_more_menu_download(page, download_dir: Path) -> Path | None:
    for point in first_row_more_points(page):
        try:
            page.mouse.click(float(point["x"]), float(point["y"]))
            page.wait_for_timeout(700)

            if not click_menu_download_parent(page):
                continue

            downloaded = click_visible_download_option(page, download_dir)
            if downloaded:
                return downloaded
        except Exception:
            continue
    return None


def try_click_download(page, download_dir: Path) -> Path | None:
    # Mureka currently hides file formats under: first song ... menu -> Download -> MP3/WAV.
    downloaded = click_visible_download_option(page, download_dir)
    if downloaded:
        return downloaded

    downloaded = click_more_menu_download(page, download_dir)
    if downloaded:
        return downloaded

    # Fallback for older layouts where a direct download icon opens the format menu.
    for point in first_row_download_points(page):
        try:
            page.mouse.click(float(point["x"]), float(point["y"]))
            page.wait_for_timeout(700)
            downloaded = click_visible_download_option(page, download_dir)
            if downloaded:
                return downloaded
        except Exception:
            continue
    return None


def visible_text_points(page, pattern: str, min_x: float = 0.0) -> list[dict]:
    try:
        return page.evaluate(
            """
            ([pattern, minX]) => {
              const re = new RegExp(pattern, 'i');
              const nodes = Array.from(document.querySelectorAll('*'));
              const points = [];
              for (const el of nodes) {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') continue;
                if (rect.width < 20 || rect.height < 12 || rect.height > 52) continue;
                if (rect.left < minX) continue;
                const text = (el.innerText || el.textContent || '').trim();
                if (!text) continue;
                const lines = text.split(/\\n+/).map((line) => line.trim()).filter(Boolean);
                if (!lines.some((line) => re.test(line))) continue;
                points.push({
                  x: rect.left + rect.width / 2,
                  y: rect.top + rect.height / 2,
                  left: rect.left,
                  top: rect.top,
                  width: rect.width,
                  height: rect.height,
                  text
                });
              }
              return points.sort((a, b) => (a.top - b.top) || (b.width - a.width));
            }
            """,
            [pattern, min_x],
        )
    except Exception:
        return []


def click_menu_download_parent(page) -> bool:
    points = visible_text_points(page, r"^(download|\ub2e4\uc6b4\ub85c\ub4dc)$", min_x=1000.0)
    for point in points:
        try:
            page.mouse.move(float(point["x"]), float(point["y"]))
            page.wait_for_timeout(900)
            return True
        except Exception:
            continue
    return False


def click_visible_download_option(page, download_dir: Path) -> Path | None:
    # Prefer MP3 for the game import path; WAV remains a fallback if Mureka hides MP3.
    option_patterns = [
        r"^mp3\s*(download|\ub2e4\uc6b4\ub85c\ub4dc)$",
        r"^wav\s*(download|\ub2e4\uc6b4\ub85c\ub4dc)$",
    ]
    for pattern in option_patterns:
        for point in visible_text_points(page, pattern, min_x=0.0):
            try:
                with page.expect_download(timeout=30000) as download_info:
                    page.mouse.click(float(point["x"]), float(point["y"]))
                return save_download(download_info.value, download_dir)
            except Exception:
                continue
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--job", required=True)
    args = parser.parse_args()
    start_time = time.time()

    job_path = Path(args.job)
    job = json.loads(job_path.read_text(encoding="utf-8-sig"))
    prompt = str(job["prompt"])
    create_url = str(job.get("create_url") or "https://mureka.ai/ko/create")
    download_dir = Path(job["download_dir"]).expanduser()
    profile_dir = Path(job["profile_dir"]).expanduser()
    timeout_seconds = job_float(
        job,
        "timeout_seconds",
        DEFAULT_AUTOMATION_TIMEOUT_SECONDS,
        ("MUREKA_AUTOMATION_TIMEOUT_SECONDS", "AI_RHYTHM_MUREKA_TIMEOUT_SECONDS"),
    )
    login_wait_seconds = job_float(
        job,
        "login_wait_seconds",
        DEFAULT_LOGIN_WAIT_SECONDS,
        ("MUREKA_LOGIN_WAIT_SECONDS", "AI_RHYTHM_MUREKA_LOGIN_WAIT_SECONDS"),
    )
    download_wait_seconds = job_float(
        job,
        "download_wait_seconds",
        DEFAULT_DOWNLOAD_WAIT_SECONDS,
        ("MUREKA_WAIT_SECONDS", "AI_RHYTHM_MUREKA_WAIT_SECONDS"),
    )
    poll_seconds = job_float(
        job,
        "poll_seconds",
        DEFAULT_POLL_SECONDS,
        ("MUREKA_POLL_SECONDS", "AI_RHYTHM_MUREKA_POLL_SECONDS"),
    )
    lyrics_wait_seconds = job_float(
        job,
        "lyrics_wait_seconds",
        login_wait_seconds,
        ("MUREKA_LYRICS_WAIT_SECONDS", "AI_RHYTHM_MUREKA_LYRICS_WAIT_SECONDS"),
    )
    post_warning_lyrics_wait_seconds = job_float(
        job,
        "post_warning_lyrics_wait_seconds",
        lyrics_wait_seconds,
        ("MUREKA_POST_WARNING_LYRICS_WAIT_SECONDS", "AI_RHYTHM_MUREKA_POST_WARNING_LYRICS_WAIT_SECONDS"),
    )
    headless = truthy(job.get("headless", os.environ.get("AI_RHYTHM_MUREKA_HEADLESS", "0")))
    poll_ms = int(max(0.5, poll_seconds) * 1000)
    download_dir.mkdir(parents=True, exist_ok=True)
    profile_dir.mkdir(parents=True, exist_ok=True)

    executables = browser_paths()
    if not executables:
        raise RuntimeError("Chrome or Edge executable was not found.")

    cleanup_stale_profile_locks(profile_dir)

    with sync_playwright() as p:
        context = None
        launch_errors = []
        for executable in executables:
            try:
                log(start_time, f"Launching browser: {executable}")
                context = p.chromium.launch_persistent_context(
                    str(profile_dir),
                    executable_path=executable,
                    headless=headless,
                    accept_downloads=True,
                    downloads_path=str(download_dir),
                    args=[
                        "--disable-blink-features=AutomationControlled",
                        "--no-first-run",
                        "--no-default-browser-check",
                        "--start-maximized",
                        "--window-position=60,60",
                        "--window-size=1500,920",
                    ],
                )
                break
            except Exception as exc:
                launch_errors.append(f"{executable}: {exc}")
                cleanup_stale_profile_locks(profile_dir)

        if context is None:
            open_browser_for_manual_login(executables[0], profile_dir, create_url, start_time)
            raise RuntimeError("Could not launch Chrome or Edge for Mureka automation. " + " | ".join(launch_errors))

        page = context.pages[0] if context.pages else context.new_page()
        if not headless:
            page.bring_to_front()
        log(start_time, "Opening Mureka create page.")
        page.goto(create_url, wait_until="domcontentloaded", timeout=60000)
        page.wait_for_timeout(2500)
        if not headless:
            page.bring_to_front()
        log(start_time, f"Page opened: {page.url}")
        baseline_songs = song_signatures(page)
        if baseline_songs:
            log(start_time, f"Library baseline: {baseline_songs[0]}")

        filled_prompt = False
        generated_lyrics = False
        prompted_login = False
        login_prompt_logged = False
        fill_deadline = time.time() + max(30.0, login_wait_seconds)
        while time.time() < fill_deadline:
            if not headless:
                try:
                    page.bring_to_front()
                except Exception:
                    pass

            if looks_like_login_page(page):
                if headless:
                    log(start_time, "Mureka login is required, but the browser is headless. Set AI_RHYTHM_MUREKA_HEADLESS=0 and retry.")
                    context.close()
                    return 3
                if not login_prompt_logged:
                    log(start_time, f"Mureka login appears required. The visible browser is open; log in within {int(login_wait_seconds)} seconds.")
                    login_prompt_logged = True
                if not prompted_login:
                    log(start_time, f"Waiting for Mureka login before filling lyrics/style fields. Log in within {int(login_wait_seconds)} seconds.")
                    prompted_login = True
                page.wait_for_timeout(poll_ms)
                continue

            click_new_song_if_available(page)
            if click_custom_tab(page):
                log(start_time, "Custom tab selected.")

            if looks_like_login_page(page):
                if headless:
                    log(start_time, "Mureka login is required, but the browser is headless. Set AI_RHYTHM_MUREKA_HEADLESS=0 and retry.")
                    context.close()
                    return 3
                if not login_prompt_logged:
                    log(start_time, f"Mureka login appears required. The visible browser is open; log in within {int(login_wait_seconds)} seconds.")
                    login_prompt_logged = True
                if not prompted_login:
                    log(start_time, f"Waiting for Mureka login before filling lyrics/style fields. Log in within {int(login_wait_seconds)} seconds.")
                    prompted_login = True
                page.wait_for_timeout(poll_ms)
                continue

            if not generated_lyrics:
                lyrics_result = generate_lyrics_from_prompt(
                    page,
                    prompt,
                    start_time,
                    lyrics_wait_seconds,
                    poll_seconds,
                )
                if lyrics_result is True:
                    generated_lyrics = True
                elif lyrics_result is False:
                    log(start_time, "Lyrics generation flow did not complete. Check the visible Mureka window, then retry.")
                    save_job_screenshot(page, job_path, "lyrics_failed")
                    context.close()
                    return 4

            if generated_lyrics and fill_custom_style_field(page, prompt):
                filled_prompt = True
                log(start_time, "Prompt filled into the custom style field.")
                break

            if looks_like_login_page(page):
                if headless:
                    log(start_time, "Mureka login is required, but the browser is headless. Set AI_RHYTHM_MUREKA_HEADLESS=0 and retry.")
                    context.close()
                    return 3
                if not login_prompt_logged:
                    log(start_time, f"Mureka login appears required. The visible browser is open; log in within {int(login_wait_seconds)} seconds.")
                    login_prompt_logged = True

            if not prompted_login:
                log(start_time, f"Could not complete lyrics/style setup yet. If a login page is open, log in; waiting up to {int(login_wait_seconds)} seconds.")
                prompted_login = True
            page.wait_for_timeout(poll_ms)

        if not filled_prompt:
            log(start_time, f"Could not complete lyrics generation and style setup after {int(login_wait_seconds)} seconds. Log in or switch to Mureka create page, then retry.")
            save_job_screenshot(page, job_path, "style_failed")
            context.close()
            return 2

        if not click_create(page):
            log(start_time, "Prompt was filled, but create button was not found. Click Create manually; Unity will still watch Downloads.")
        else:
            log(start_time, "Mureka create button clicked.")
            wait_for_new_song(
                page,
                baseline_songs,
                start_time,
                timeout_seconds,
                job_path,
                post_warning_lyrics_wait_seconds,
                poll_seconds,
            )

        deadline = time.time() + max(1.0, download_wait_seconds)
        while time.time() < deadline:
            downloaded = try_click_download(page, download_dir)
            if downloaded:
                log(start_time, f"Downloaded: {downloaded}")
                context.close()
                return 0

            for url in find_audio_urls(page):
                downloaded = save_audio_from_url(context, url, download_dir)
                if downloaded:
                    log(start_time, f"Saved audio URL: {downloaded}")
                    context.close()
                    return 0

            page.wait_for_timeout(poll_ms)

        log(start_time, "Timed out before automatic download. If the song is ready, click Download manually in the open Mureka window.")
        context.close()
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
