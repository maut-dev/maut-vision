"""Python Training worker Maut Vision.

Исполняет одну ML-операцию над папкой задачи training/jobs/<jobId>/ и ничего
не знает о приложении: ни о SQLite, ни о host, ни о доменах. Контракт —
только файлы задачи и JSON Lines в stdout (см. README.md).
"""
