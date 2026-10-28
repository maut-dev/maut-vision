"""Захват экрана через mss.

Экземпляр mss держится открытым на весь цикл: создание на каждый кадр стоит
десятки миллисекунд. Координаты рамки — относительно выбранного монитора;
монитор 0 — все экраны вместе (так их нумерует mss).
"""
from __future__ import annotations

from typing import Any

from worker.protocol import ErrorCode, MonitorInfo, Region, WorkerError


class Screen:
    def __init__(self, monitor: int):
        try:
            import mss
        except Exception as exc:  # noqa: BLE001 — нет пакета: понятная причина вместо трейсбека
            raise WorkerError(ErrorCode.CAPTURE_UNAVAILABLE, f"Захват экрана недоступен: не установлен mss ({exc})") from None
        try:
            self._sct = (mss.MSS if hasattr(mss, "MSS") else mss.mss)()
        except Exception as exc:  # noqa: BLE001 — нет экрана (сервисная сессия, WSL)
            raise WorkerError(ErrorCode.CAPTURE_UNAVAILABLE, f"Не удалось открыть захват экрана: {exc}") from None
        monitors = self._sct.monitors
        if not 0 <= monitor < len(monitors):
            self.close()
            raise WorkerError(
                ErrorCode.CAPTURE_UNAVAILABLE,
                f"Монитора {monitor} нет: доступны 0..{len(monitors) - 1}",
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
        """Монитор совпадает с тем, на снимке которого host ставил рамку."""
        if expected is None:
            return
        actual = (int(self.bounds["left"]), int(self.bounds["top"]), int(self.bounds["width"]), int(self.bounds["height"]))
        wanted = (expected.left, expected.top, expected.width, expected.height)
        if actual != wanted:
            raise WorkerError(
                ErrorCode.CAPTURE_UNAVAILABLE,
                f"Монитор {self.index} изменился после снимка: сейчас {actual[2]}×{actual[3]} в ({actual[0]}, {actual[1]}), "
                f"а рамка выбрана на {wanted[2]}×{wanted[3]} в ({wanted[0]}, {wanted[1]}). Снимите экран заново.",
                {"actual": list(actual), "expected": list(wanted)},
            )

    def check(self, region: Region | None) -> None:
        if region is None:
            return
        if region.x + region.width > self.bounds["width"] or region.y + region.height > self.bounds["height"]:
            raise WorkerError(
                ErrorCode.REGION_INVALID,
                f"Рамка {region.width}×{region.height} в точке ({region.x}, {region.y}) выходит за монитор "
                f"{self.bounds['width']}×{self.bounds['height']}",
            )

    def grab(self, region: Region | None) -> Any:
        """Кадр BGR (numpy): mss отдаёт BGRA, альфа отбрасывается."""
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
        except Exception:  # noqa: BLE001 — закрытие уже закрытого захвата не важно
            pass

    def __enter__(self) -> "Screen":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
