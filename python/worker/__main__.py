"""`python -m worker <jobDir>` — то же, что собранный maut-training-worker.exe."""
from worker.main import main

raise SystemExit(main())
