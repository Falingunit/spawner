#!/usr/bin/env python3
"""Generate a password hash without putting the password in shell history or argv."""
import base64
import getpass
import hashlib
import secrets

password = getpass.getpass("New administrator password (at least 16 characters): ")
if len(password) < 16 or len(password) > 1024:
    raise SystemExit("Use between 16 and 1024 characters.")
if password != getpass.getpass("Confirm password: "):
    raise SystemExit("Passwords did not match.")
salt = secrets.token_bytes(32)
digest = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 600000)
print("pbkdf2-sha256-600000:" + base64.b64encode(salt).decode() + ":" + base64.b64encode(digest).decode())
