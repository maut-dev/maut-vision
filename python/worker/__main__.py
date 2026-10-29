"""`python -m worker <jobDir>` — the same as the packaged maut-training-worker.exe."""
from worker.main import main

raise SystemExit(main())
