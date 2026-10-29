"""Screen capture via mss.

The mss instance stays open for the whole loop: creating it per frame costs
tens of milliseconds. Crop coordinates are relative to the chosen monitor;
monitor 0 is all screens together (this is how mss numbers them).
"""
from __future__ import annotations

from typing import Any

from worker.protocol import ErrorCode, MonitorInfo, Region, WorkerError


class Screen:
    def __init__(self, monitor: int):
        try:
            import mss
        except Exception as exc:  # noqa: BLE001 — no package: a clear reason instead of a traceback
            raise WorkerError(ErrorCode.CAPTURE_UNAVAILABLE, "mss_missing", {"error": exc}) from None
        try:
            self._sct = (mss.MSS if hasattr(mss, "MSS") else mss.mss)()
        except Exception as exc:  # noqa: BLE001 — no screen (service session, WSL)
            raise WorkerError(ErrorCode.CAPTURE_UNAVAILABLE, "open_failed", {"error": exc}) from None
        monitors = self._sct.monitors
        if not 0 <= monitor < len(monitors):
            self.close()
            raise WorkerError(
                ErrorCode.CAPTURE_UNAVAILABLE,
                "monitor_missing",
                {"monitor": monitor, "last": len(monitors) - 1},
                {"monitors": len(monitors) - 1},
            )
        self.index = monitor
        self.bounds = monitors[monitor]

    def monitors(self) -> list[MonitorInfo]:
        return [
            MonitorInfo(index=i, left=int(m["left"]), top=int(m["top"]), width=int(m["width"]), height=int(m["height"]))
            for i, m in enumerate(self._sct.monitors)
        ]

    def check_bounds(self, expected: Any) -> None:
        """The monitor matches the one on whose screenshot the host placed the crop."""
        if expected is None:
            return
        actual = (int(self.bounds["left"]), int(self.bounds["top"]), int(self.bounds["width"]), int(self.bounds["height"]))
        wanted = (expected.left, expected.top, expected.width, expected.height)
        if actual != wanted:
            raise WorkerError(
                ErrorCode.CAPTURE_UNAVAILABLE,
                "monitor_changed",
                {
                    "monitor": self.index,
                    "actualWidth": actual[2],
                    "actualHeight": actual[3],
                    "actualX": actual[0],
                    "actualY": actual[1],
                    "width": wanted[2],
                    "height": wanted[3],
                    "x": wanted[0],
                    "y": wanted[1],
                },
                {"actual": list(actual), "expected": list(wanted)},
            )

    def check(self, region: Region | None) -> None:
        if region is None:
            return
        if region.x + region.width > self.bounds["width"] or region.y + region.height > self.bounds["height"]:
            raise WorkerError(
                ErrorCode.REGION_INVALID,
                "outside_monitor",
                {
                    "width": region.width,
                    "height": region.height,
                    "x": region.x,
                    "y": region.y,
                    "frameWidth": self.bounds["width"],
                    "frameHeight": self.bounds["height"],
                },
            )

    def grab(self, region: Region | None) -> Any:
        """A BGR frame (numpy): mss returns BGRA, alpha is dropped."""
        import numpy as np

        area = {
            "left": int(self.bounds["left"]) + (region.x if region else 0),
            "top": int(self.bounds["top"]) + (region.y if region else 0),
            "width": region.width if region else int(self.bounds["width"]),
            "height": region.height if region else int(self.bounds["height"]),
        }
        shot = self._sct.grab(area)
        return np.ascontiguousarray(np.asarray(shot)[:, :, :3])

    def close(self) -> None:
        try:
            self._sct.close()
        except Exception:  # noqa: BLE001 — closing an already closed capture does not matter
            pass

    def __enter__(self) -> "Screen":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
