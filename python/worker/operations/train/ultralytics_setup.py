"""Приведение ultralytics к офлайн-работе внутри папки задачи.

Вызывается после runtime.offline.configure_environment и импорта ultralytics.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path
from typing import Any

# Интеграции, которые при наличии пакета сами подключаются к обучению и
# шлют данные наружу (или хотя бы пытаются). Работнику они не нужны.
_INTEGRATIONS_OFF = ("sync", "clearml", "comet", "dvc", "mlflow", "raytune", "tensorboard", "wandb",
                     "vscode_msg", "openvino_msg")

# GPU, на которых AMP даёт NaN; тот же список, что в ultralytics.utils.checks.check_amp
_AMP_BAD_GPU = re.compile(
    r"(nvidia|geforce|quadro|tesla).*?(1660|1650|1630|t400|t550|t600|t1000|t1200|t2000|k40m)", re.IGNORECASE
)


def configure(work_dir: Path) -> None:
    from ultralytics.utils import SETTINGS

    SETTINGS.update(
        {
            **{k: False for k in _INTEGRATIONS_OFF if k in SETTINGS},
            "weights_dir": str(work_dir / "weights-cache"),
            "datasets_dir": str(work_dir / "datasets-cache"),
            "runs_dir": str(work_dir / "runs"),
        }
    )
    _replace_amp_check()
    _replace_font_check()


def _offline_check_amp(model: Any) -> bool:
    """Замена ultralytics check_amp без скачивания.

    Оригинал сравнивает fp32 и AMP на посторонней сети yolo26n.pt, которую
    качает из интернета («not used for training» в его же исходниках).
    Офлайн это невозможно, поэтому оставляем только известный чёрный
    список GPU. Проверка срабатывает лишь при amp=true в запросе (так по
    умолчанию); при amp=false trainer её не вызывает.
    """
    import torch

    device = next(model.parameters()).device
    if device.type != "cuda":
        return False
    gpu = torch.cuda.get_device_name(device)
    if _AMP_BAD_GPU.search(gpu):
        print(f"[amp] {gpu}: известны NaN при AMP — AMP выключен", file=sys.stderr, flush=True)
        return False
    print(f"[amp] {gpu}: AMP включён без эталонной проверки (офлайн)", file=sys.stderr, flush=True)
    return True


def _replace_amp_check() -> None:
    import ultralytics.engine.trainer as trainer_module

    trainer_module.check_amp = _offline_check_amp


def _offline_check_font(font: str = "Arial.ttf") -> str | None:
    """Замена ultralytics check_font без скачивания.

    check_det_dataset зовёт её безусловно, а Annotator — для подписей на
    картинках обучения (plots=True), и, не найдя шрифт (сравнение имени
    регистрозависимое, а в Windows он arial.ttf), идёт за ним на github.com.
    Ищем только среди локальных шрифтов, иначе None: Annotator тогда берёт
    встроенный шрифт PIL (его try/except ловит str(None)), картинки
    рисуются, подписи попроще.
    """
    from matplotlib import font_manager

    name = Path(font).name.lower()
    for f in font_manager.fontManager.ttflist:
        if Path(f.fname).name.lower() == name and Path(f.fname).exists():
            return f.fname
    return None


def _replace_font_check() -> None:
    import ultralytics.data.utils as data_utils
    import ultralytics.utils.checks as checks
    import ultralytics.utils.plotting as plotting

    # Каждый модуль держит свою ссылку (from ... import check_font) —
    # подменять нужно во всех, иначе картинки обучения пойдут в сеть.
    data_utils.check_font = _offline_check_font
    checks.check_font = _offline_check_font
    plotting.check_font = _offline_check_font
