"""Training report files: ultralytics images and the console log.

Not weights and not the model quality contract (metrics are in result.json), but what
a person looks at in the version passport: summary plots, confusion matrices, training
batches, console output. So collecting them is "best effort": a refusal here
gives a warning, not a failed training.
"""
from __future__ import annotations

import re
import shutil
from pathlib import Path

# File names ultralytics puts into the run folder: results.png,
# confusion_matrix*.png, labels.jpg, train_batch*.jpg, val_batch*_{labels,pred}.jpg,
# Box{F1,P,R,PR}_curve.png. Any safe png/jpg from the folder root is taken.
_PLOT_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.(png|jpg|jpeg)$", re.IGNORECASE)
# Terminal CSI sequences (ultralytics colors) and other ESC codes
_ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b[@-Z\\-_]")


def collect_plots(run_dir: Path, target_dir: Path) -> list[Path]:
    """Copy the images from the ultralytics run folder into target_dir.

    The order is stable (by name): the UI orders the gallery itself.
    There are no size or count quotas; unsupported names and empty files are skipped.
    """
    if not run_dir.is_dir():
        return []
    copied: list[Path] = []
    for source in sorted(run_dir.iterdir(), key=lambda p: p.name.lower()):
        if not source.is_file() or not _PLOT_NAME.match(source.name):
            continue
        size = source.stat().st_size
        if size == 0:
            continue
        target_dir.mkdir(parents=True, exist_ok=True)
        target = target_dir / source.name
        shutil.copyfile(source, target)
        copied.append(target)
    return copied


def clean_console_text(raw: str) -> str:
    """Raw console output → readable lines.

    tqdm progress bars redraw a line via \\r: of each line
    only the last state remains. Windows line breaks are normalized,
    ANSI colors are removed.
    """
    text = _ANSI.sub("", raw.replace("\r\n", "\n"))
    lines = []
    for line in text.split("\n"):
        if "\r" in line:
            parts = [part for part in line.split("\r") if part.strip()]
            line = parts[-1] if parts else ""
        lines.append(line.rstrip())
    while lines and not lines[-1]:
        lines.pop()
    return "\n".join(lines) + ("\n" if lines else "")


def write_console_log(raw_path: Path, target: Path) -> Path | None:
    """Clean the complete console log incrementally; preserve every completed line."""
    if not raw_path.is_file():
        return None
    target.parent.mkdir(parents=True, exist_ok=True)
    written = False
    current = ""
    pending_cr = False
    with raw_path.open(encoding="utf-8", errors="replace", newline="") as source, target.open("w", encoding="utf-8", newline="\n") as output:
        while chunk := source.read(64 * 1024):
            for character in chunk:
                if pending_cr:
                    pending_cr = False
                    if character == "\n":
                        output.write(_ANSI.sub("", current).rstrip() + "\n")
                        written = True
                        current = ""
                        continue
                    current = ""
                if character == "\r":
                    pending_cr = True
                elif character == "\n":
                    output.write(_ANSI.sub("", current).rstrip() + "\n")
                    written = True
                    current = ""
                else:
                    current += character
        if current.strip():
            output.write(_ANSI.sub("", current).rstrip() + "\n")
            written = True
    if not written:
        target.unlink(missing_ok=True)
        return None
    return target
