#!/usr/bin/env python3
"""
Stores the marketplace token CI publishes with. Run once, by the publisher.

The marketplace's publisher API accepts only the publisher's own Keycloak login,
so CI needs a token of that account that outlives a browser session: a Keycloak
offline token. This logs in through the browser (the publisher's own login, with
its second factor), trades the answer for an offline token and stores it as the
MARKETPLACE_REFRESH_TOKEN Actions secret of this repository on Forgejo.

The token is never printed or written to disk. It is the publisher's account,
so treat the secret like a password: removing it from Settings -> Actions ->
Secrets, or signing out of all sessions in the Keycloak account console, ends it.

Usage:
    python scripts/marketplace-login.py

Reads the Forgejo token from the git credential store, as the other scripts do.
"""

import base64
import hashlib
import json
import secrets
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import webbrowser

REALM = "https://auth.nomercy.tv/realms/NoMercyTV"
CLIENT = "nomercy-ui"
REDIRECT = "https://app.nomercy.tv/silent-check-sso.html"
FORGEJO = "https://forgejo.phillippepelzer.me"
REPO = "FiLL/nomercy-torrent-plugin"
SECRET = "MARKETPLACE_REFRESH_TOKEN"


def post_form(url: str, fields: dict) -> dict:
    data = urllib.parse.urlencode(fields).encode()
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=data)) as answer:
            return json.load(answer)
    except urllib.error.HTTPError as error:
        try:
            return json.load(error)
        except ValueError:
            return {"error": str(error.code), "error_description": error.reason}


def refresh(token: str) -> dict:
    return post_form(
        f"{REALM}/protocol/openid-connect/token",
        {"grant_type": "refresh_token", "client_id": CLIENT, "refresh_token": token},
    )


def claims(jwt: str) -> dict:
    payload = jwt.split(".")[1]
    payload += "=" * (-len(payload) % 4)
    return json.loads(base64.urlsafe_b64decode(payload))


def forgejo_token() -> str:
    answer = subprocess.run(
        ["git", "credential", "fill"],
        input=f"protocol=https\nhost={FORGEJO.split('://')[1]}\n\n",
        capture_output=True,
        text=True,
        check=True,
    ).stdout
    for line in answer.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1]
    sys.exit("No Forgejo token in the git credential store.")


def store_secret(value: str) -> None:
    request = urllib.request.Request(
        f"{FORGEJO}/api/v1/repos/{REPO}/actions/secrets/{SECRET}",
        data=json.dumps({"data": value}).encode(),
        method="PUT",
        headers={
            "Authorization": f"token {forgejo_token()}",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(request) as answer:
            status = answer.status
    except urllib.error.HTTPError as error:
        sys.exit(f"Forgejo would not store the secret: {error.code} {error.read().decode(errors='replace')}")
    if status not in (201, 204):
        sys.exit(f"Forgejo answered {status} when storing the secret.")


def main() -> None:
    verifier = base64.urlsafe_b64encode(secrets.token_bytes(48)).rstrip(b"=").decode()
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    state = secrets.token_hex(8)

    url = f"{REALM}/protocol/openid-connect/auth?" + urllib.parse.urlencode(
        {
            "client_id": CLIENT,
            "redirect_uri": REDIRECT,
            "response_type": "code",
            "scope": "openid offline_access",
            "state": state,
            "code_challenge": challenge,
            "code_challenge_method": "S256",
        }
    )

    print("A browser window opens on the NoMercy login. Log in with the publisher's account.")
    print("It ends on an empty page. Copy that page's whole address and paste it here")
    print("within a minute: the code in it is only valid that long.\n")
    print(url, "\n")
    webbrowser.open(url)

    pasted = input("Address of the empty page: ").strip()
    query = urllib.parse.parse_qs(urllib.parse.urlparse(pasted).query)

    if query.get("state", [""])[0] != state:
        sys.exit("That address is not the answer to this login (the state does not match). Run it again.")
    if "code" not in query:
        sys.exit(f"The login did not end with a code: {query.get('error_description', query.get('error', ['?']))[0]}")

    tokens = post_form(
        f"{REALM}/protocol/openid-connect/token",
        {
            "grant_type": "authorization_code",
            "client_id": CLIENT,
            "code": query["code"][0],
            "redirect_uri": REDIRECT,
            "code_verifier": verifier,
        },
    )
    offline = tokens.get("refresh_token")
    if not offline:
        sys.exit(f"Keycloak gave no token: {tokens.get('error')}: {tokens.get('error_description')}")

    kind = claims(offline).get("typ")
    if kind != "Offline":
        sys.exit(f"Keycloak gave a {kind} token, not an Offline one: offline_access is not granted to {CLIENT}.")

    # Used twice, as CI will use it on two releases. If the second use fails,
    # Keycloak replaces the token on every use, and one stored once dies the
    # first time CI uses it.
    first = refresh(offline)
    second = refresh(offline)
    if "access_token" not in first or "access_token" not in second:
        why = second.get("error_description") or first.get("error_description") or "no reason given"
        sys.exit(
            "The token works once and not twice: this realm revokes a refresh token when it is used "
            f"({why}). CI would need to store the new one after every run. Nothing was stored."
        )

    store_secret(offline)
    print(f"\nStored as {SECRET} on {REPO}. CI publishes to the marketplace from the next tag on.")


if __name__ == "__main__":
    main()
