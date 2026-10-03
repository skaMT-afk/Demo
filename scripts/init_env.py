"""Create a local DB password without overwriting an existing environment."""
from pathlib import Path
import secrets
path = Path(__file__).resolve().parents[1] / ".env"
try:
    with path.open("x", encoding="utf-8") as f:
        f.write("POSTGRES_PASSWORD=" + secrets.token_hex(24) + "\n")
    path.chmod(0o600)
    print("Created .env. Do not commit this file.")
except FileExistsError:
    print("Existing .env preserved.")
