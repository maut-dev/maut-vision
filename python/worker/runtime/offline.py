"""Offline mode: the worker never goes to the network under any conditions.

Ultralytics tends to download along the way: base weights by name, a network for
the AMP check, the Arial font for plots, missing pip packages
(AutoUpdate), telemetry. Environment flags do not cover everything — check_font,
for example, does not read them. So there are two layers:

1. environment variables before the ultralytics import — so that it knows itself that
   there is no network and does not try (YOLO_OFFLINE, YOLO_AUTOINSTALL);
2. a ban on outgoing connections at the socket level — the last line: any
   attempt to go outside gets OSError, and stderr keeps a trace of who
   tried. Loopback is allowed — torch itself uses it.
"""
from __future__ import annotations

import os
import socket
import sys
import traceback
from pathlib import Path

_LOOPBACK_HOSTS = {"localhost", "127.0.0.1", "::1", "0.0.0.0", ""}


def configure_environment(config_dir: Path) -> None:
    """Call before the ultralytics import: some flags are read at import."""
    config_dir.mkdir(parents=True, exist_ok=True)
    os.environ["YOLO_OFFLINE"] = "true"
    os.environ["YOLO_AUTOINSTALL"] = "false"
    # Ultralytics settings (settings.json, weights_dir, datasets_dir) go into
    # the job draft, not %APPDATA%\Ultralytics: the worker does not write outside
    # the job folder and does not depend on what someone else left there.
    os.environ["YOLO_CONFIG_DIR"] = str(config_dir)
    os.environ.setdefault("YOLO_VERBOSE", "true")
    os.environ["PYTHONUTF8"] = "1"
    os.environ["PYTHONIOENCODING"] = "utf-8"
    # torch/hf hub clients must not go outside either
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"


def _host_of(address: object) -> str:
    if isinstance(address, tuple) and address:
        return str(address[0])
    return str(address)


def _blocked(what: str, host: str) -> OSError:
    # the stack is deep: the first frames are urllib3/requests, the culprit is lower
    caller = "".join(traceback.format_stack(limit=25)[:-2])
    print(f"[offline] blocked {what} {host!r}:\n{caller}", file=sys.stderr, flush=True)
    return OSError(f"The training worker is offline: network access to {host!r} is forbidden")


def block_network() -> None:
    real_connect = socket.socket.connect
    real_connect_ex = socket.socket.connect_ex
    real_getaddrinfo = socket.getaddrinfo

    def connect(self: socket.socket, address: object) -> None:
        if self.family == getattr(socket, "AF_UNIX", None) or _host_of(address) in _LOOPBACK_HOSTS:
            return real_connect(self, address)
        raise _blocked("connection to", _host_of(address))

    def connect_ex(self: socket.socket, address: object) -> int:
        if self.family == getattr(socket, "AF_UNIX", None) or _host_of(address) in _LOOPBACK_HOSTS:
            return real_connect_ex(self, address)
        raise _blocked("connection to", _host_of(address))

    def getaddrinfo(host, *args, **kwargs):  # type: ignore[no-untyped-def]
        if host is None or str(host) in _LOOPBACK_HOSTS:
            return real_getaddrinfo(host, *args, **kwargs)
        raise socket.gaierror(socket.EAI_NONAME, str(_blocked("name resolution of", str(host))))

    socket.socket.connect = connect  # type: ignore[method-assign]
    socket.socket.connect_ex = connect_ex  # type: ignore[method-assign]
    socket.getaddrinfo = getaddrinfo  # type: ignore[assignment]
