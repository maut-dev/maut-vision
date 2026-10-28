# -*- mode: python ; coding: utf-8 -*-
# PyInstaller onedir-сборка worker (docs/Python/worker.md). Собирается scripts/build-worker.ps1;
# результат — .dev/python-worker/maut-training-worker/maut-training-worker.exe.
from PyInstaller.utils.hooks import collect_all, collect_submodules, collect_dynamic_libs
from pathlib import Path

root = Path(SPECPATH)

datas, binaries, hiddenimports = [], [], []
# Копия каталога рядом с worker — запасной путь; host передаёт свой каталог в MAUT_MODEL_CATALOG.
datas.append((str(root.parent / "catalog" / "yolo-catalog.json"), "."))

# ultralytics читает свои cfg/*.yaml (архитектуры, default.yaml) и assets с
# диска во время работы — без них чекпоинт не соберётся в модель.
# onnx/onnxslim импортируются экспортом лениво, анализатор их не увидит.
for package in ("ultralytics", "onnx", "onnxslim"):
    d, b, h = collect_all(package)
    datas += d
    binaries += b
    hiddenimports += h
hiddenimports += collect_submodules("worker")
# mss импортируется лениво (screenshot/live) — анализатор его не увидит
hiddenimports += collect_submodules("mss")
# torchvision 0.29 loads _C_stable/image_stable via torch.ops.load_library.
# The upstream hook still names torchvision._C; indirect .pyd loads are not
# import statements and default collect_dynamic_libs patterns omit .pyd.
binaries += collect_dynamic_libs("torchvision", search_patterns=["*.pyd", "*.dll"])

# onnxruntime (~223 MB, CUDA provider) enters the analysis through lazy imports of onnxslim, torch.onnx and
# ultralytics.nn.backends.onnx. The worker does not use it: checks run on best.pt, and the ONNX export produces
# the same graph without it (docs/Python/worker.md, section Build).
excludes = ["tkinter", "IPython", "jupyter", "notebook", "tensorboard", "wandb", "mlflow", "clearml", "comet_ml", "onnxruntime"]

a = Analysis(
    [str(root / "worker_entry.py")],
    pathex=[str(root)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    excludes=excludes,
    noarchive=False,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="maut-training-worker",
    # консольное приложение: stdout — канал событий; окна нет, потому что
    # host запускает процесс с CreateNoWindow
    console=True,
    upx=False,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    name="maut-training-worker",
)
