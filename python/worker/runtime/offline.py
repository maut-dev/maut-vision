"""Офлайн-режим: работник не ходит в сеть ни при каких условиях.

Ultralytics по дороге норовит скачать: базовые веса по имени, сеть для
проверки AMP, шрифт Arial для графиков, недостающие pip-пакеты
(AutoUpdate), телеметрию. Флагов окружения на всё не хватает — check_font,
например, их не читает. Поэтому два слоя:

1. переменные окружения до импорта ultralytics — чтобы он сам знал, что
   сети нет, и не пытался (YOLO_OFFLINE, YOLO_AUTOINSTALL);
2. запрет исходящих соединений на уровне socket — последняя линия: любая
   попытка выйти наружу получает OSError, а в stderr остаётся след, кто
   пытался. Loopback разрешён — им пользуется сам torch.
"""
from __future__ import annotations

import os
import socket
import sys
import traceback
from pathlib import Path

_LOOPBACK_HOSTS = {"localhost", "127.0.0.1", "::1", "0.0.0.0", ""}


def configure_environment(config_dir: Path) -> None:
    """Вызывать до импорта ultralytics: часть флагов читается при импорте."""
    config_dir.mkdir(parents=True, exist_ok=True)
    os.environ["YOLO_OFFLINE"] = "true"
    os.environ["YOLO_AUTOINSTALL"] = "false"
    # Настройки ultralytics (settings.json, weights_dir, datasets_dir) — в
    # черновик задачи, а не в %APPDATA%\Ultralytics: работник не пишет за
    # пределы папки задачи и не зависит от того, что там оставил кто-то ещё.
    os.environ["YOLO_CONFIG_DIR"] = str(config_dir)
    os.environ.setdefault("YOLO_VERBOSE", "true")
    os.environ["PYTHONUTF8"] = "1"
    os.environ["PYTHONIOENCODING"] = "utf-8"
    # hub-клиенты torch/hf тоже не должны лезть наружу
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"


def _host_of(address: object) -> str:
    if isinstance(address, tuple) and address:
        return str(address[0])
    return str(address)


def _blocked(what: str, host: str) -> OSError:
    # стек глубокий: первые кадры — urllib3/requests, виновник ниже
    caller = "".join(traceback.format_stack(limit=25)[:-2])
    print(f"[offline] заблокировано {what} {host!r}:\n{caller}", file=sys.stderr, flush=True)
    return OSError(f"Работник обучения офлайн: сетевой доступ к {host!r} запрещён")


def block_network() -> None:
    real_connect = socket.socket.connect
    real_connect_ex = socket.socket.connect_ex
    real_getaddrinfo = socket.getaddrinfo

    def connect(self: socket.socket, address: object) -> None:
        if self.family == getattr(socket, "AF_UNIX", None) or _host_of(address) in _LOOPBACK_HOSTS:
            return real_connect(self, address)
        raise _blocked("соединение с", _host_of(address))

    def connect_ex(self: socket.socket, address: object) -> int:
        if self.family == getattr(socket, "AF_UNIX", None) or _host_of(address) in _LOOPBACK_HOSTS:
            return real_connect_ex(self, address)
        raise _blocked("соединение с", _host_of(address))

    def getaddrinfo(host, *args, **kwargs):  # type: ignore[no-untyped-def]
        if host is None or str(host) in _LOOPBACK_HOSTS:
            return real_getaddrinfo(host, *args, **kwargs)
        raise socket.gaierror(socket.EAI_NONAME, str(_blocked("разрешение имени", str(host))))

    socket.socket.connect = connect  # type: ignore[method-assign]
    socket.socket.connect_ex = connect_ex  # type: ignore[method-assign]
    socket.getaddrinfo = getaddrinfo  # type: ignore[assignment]
