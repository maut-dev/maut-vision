"""Python Training worker Maut Vision.

Runs one ML operation on the job folder training/jobs/<jobId>/ and knows nothing
about the application: neither SQLite, nor the host, nor the domains. The contract is
only the job files and JSON Lines in stdout (see README.md).
"""
