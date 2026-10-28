"""Скрипт входа для PyInstaller (он не умеет собирать от `-m worker`)."""
if __name__ == "__main__":
    # PyInstaller routes DataLoader subprocesses before worker protocol imports.
    # Without this, Windows workers>0 re-enter the job rather than spawn_main.
    from multiprocessing import freeze_support

    freeze_support()
    from worker.main import main

    raise SystemExit(main())
