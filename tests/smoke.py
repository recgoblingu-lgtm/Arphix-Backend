#!/usr/bin/env python3
"""Start an isolated backend and exercise its core project-owned HTTP contract."""
from __future__ import annotations

import json
import os
import selectors
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "Revival.Api" / "Revival.Api.csproj"


def reserve_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="revival-smoke-") as temp:
        port = reserve_port()
        base = f"http://127.0.0.1:{port}"
        env = os.environ.copy()
        dotnet = shutil.which("dotnet", path=env.get("PATH"))
        if dotnet is None:
            local_dotnet = Path.home() / ".dotnet" / "dotnet"
            if local_dotnet.exists():
                dotnet = str(local_dotnet)
                env["PATH"] = f"{local_dotnet.parent}:{env.get('PATH', '')}"
                env.setdefault("DOTNET_ROOT", str(local_dotnet.parent))
        node = shutil.which("node", path=env.get("PATH"))
        if dotnet is None or node is None:
            raise RuntimeError("The smoke suite requires the .NET 8 SDK and Node.js with built-in WebSocket support.")
        env.update({
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "ASPNETCORE_URLS": base,
            "ASPNETCORE_ENVIRONMENT": "Development",
            "Database__Provider": "Sqlite",
            "Database__SqlitePath": str(Path(temp) / "test.db"),
            "Storage__AssetRoot": str(Path(temp) / "assets"),
            "Storage__BackupRoot": str(Path(temp) / "backups"),
            "Security__AdminBootstrapSecret": "test-bootstrap-secret",
            "RateLimits__AuthPerMinute": "1000",
            "RateLimits__RequestsPerMinute": "10000",
            "Logging__LogLevel__Default": "Warning",
        })
        log_path = Path(temp) / "server.log"
        log = log_path.open("w+")
        process = subprocess.Popen(
            [dotnet, "run", "--project", str(PROJECT), "--no-build"],
            cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
        )
        realtime = None
        room_realtime = None

        def call(method: str, path: str, payload=None, *, token: str | None = None,
                 headers: dict[str, str] | None = None, raw: bytes | None = None):
            request_headers = dict(headers or {})
            body = raw
            if payload is not None:
                body = json.dumps(payload).encode()
                request_headers["Content-Type"] = "application/json"
            if token:
                request_headers["Authorization"] = f"Bearer {token}"
            request = urllib.request.Request(base + path, data=body, headers=request_headers, method=method)
            try:
                with urllib.request.urlopen(request, timeout=10) as response:
                    data = response.read()
                    content_type = response.headers.get("Content-Type", "")
                    parsed = json.loads(data) if data and "json" in content_type else data
                    return response.status, parsed, response.headers
            except urllib.error.HTTPError as response:
                data = response.read()
                try:
                    parsed = json.loads(data)
                except Exception:
                    parsed = data
                return response.code, parsed, response.headers

        def check(condition: bool, label: str, details=None) -> None:
            if not condition:
                log.flush()
                diagnostics = log_path.read_text()[-8000:]
                raise AssertionError(f"{label}: {details!r}\n--- server log ---\n{diagnostics}")
            print(f"PASS {label}")

        try:
            for _ in range(120):
                if process.poll() is not None:
                    log.flush()
                    raise RuntimeError(f"API exited during startup:\n{log_path.read_text()}")
                try:
                    status, health, _ = call("GET", "/health")
                    if status == 200:
                        break
                except Exception:
                    time.sleep(0.25)
            else:
                log.flush()
                raise RuntimeError(f"API did not become ready:\n{log_path.read_text()}")
            check(health["database"] == "connected", "health and SQLite migrations")
            status, protocol, _ = call("GET", "/v1")
            check(status == 200 and protocol["protocolVersion"] == 1, "project protocol discovery")
            status, unauthorized, _ = call("GET", "/v1/inventory")
            check(status == 401, "unauthorized route rejection")

            status, alice, _ = call("POST", "/v1/auth/register", {"userName": "alice", "displayName": "Alice", "password": "Alice-Strong-Password-1"})
            check(status == 201, "registration")
            aid, atoken = alice["player"]["id"], alice["accessToken"]
            status, bob, _ = call("POST", "/v1/auth/register", {"userName": "bob", "displayName": "Bob", "password": "Bob-Strong-Password-1"})
            check(status == 201, "second player registration")
            bid, btoken = bob["player"]["id"], bob["accessToken"]
            status, duplicate, _ = call("POST", "/v1/auth/register", {"userName": "Alice", "displayName": "Collision", "password": "Another-Strong-Password-1"})
            check(status == 409, "case-insensitive username uniqueness")

            status, current, _ = call("GET", "/v1/auth/me", token=atoken)
            check(status == 200 and current["id"] == aid, "authenticated session", {"status": status, "body": current})
            status, rotated, _ = call("POST", "/v1/auth/refresh", {"refreshToken": alice["refreshToken"]})
            check(status == 200 and rotated["accessToken"] != atoken, "refresh token rotation")
            status, _, _ = call("GET", "/v1/auth/me", token=atoken)
            check(status == 401, "old access token revoked on rotation")
            atoken = rotated["accessToken"]
            status, replay_user, _ = call("POST", "/v1/auth/register", {"userName": "replaytest", "displayName": "Replay Test", "password": "Replay-Strong-Password-1"})
            check(status == 201, "refresh replay fixture registration")
            status, replay_rotation, _ = call("POST", "/v1/auth/refresh", {"refreshToken": replay_user["refreshToken"]})
            check(status == 200, "refresh replay fixture rotation")
            status, _, _ = call("POST", "/v1/auth/refresh", {"refreshToken": replay_user["refreshToken"]})
            check(status == 401, "refresh token replay rejected")
            status, _, _ = call("GET", "/v1/auth/me", token=replay_rotation["accessToken"])
            check(status == 401, "refresh replay revokes rotated session")
            status, profile, _ = call("PATCH", f"/v1/players/{aid}", {"displayName": "Alice One", "bio": "Independent client tester"}, token=atoken)
            check(status == 200 and profile["displayName"] == "Alice One", "self profile update")
            status, settings, _ = call("PUT", f"/v1/players/{aid}/settings", {"locale": "en", "voiceEnabled": True}, token=atoken)
            check(status == 200 and settings["locale"] == "en", "player settings persistence")
            status, settings, _ = call("GET", f"/v1/players/{aid}/settings", token=atoken)
            check(status == 200 and settings["voiceEnabled"] is True, "settings readback")

            realtime = subprocess.Popen(
                [node, str(ROOT / "tests" / "realtime_smoke.mjs"), base, btoken, "friend.requested"],
                cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            )
            selector = selectors.DefaultSelector()
            selector.register(realtime.stdout, selectors.EVENT_READ)
            ready_line = ""
            deadline = time.time() + 15
            while time.time() < deadline and not ready_line:
                if realtime.poll() is not None:
                    break
                for key, _ in selector.select(timeout=0.25):
                    ready_line = key.fileobj.readline().strip()
                    if ready_line != "READY": break
            selector.close()
            check(ready_line == "READY", "SignalR bearer handshake", {"line": ready_line, "exit": realtime.poll()})
            status, friend_request, _ = call("POST", "/v1/friends/requests", {"playerId": bid}, token=atoken)
            check(status == 201, "friend request creation")
            realtime_stdout, realtime_stderr = realtime.communicate(timeout=15)
            check(realtime.returncode == 0 and "EVENT friend.requested" in realtime_stdout,
                  "SignalR friend-request event", {"stdout": realtime_stdout, "stderr": realtime_stderr})
            fid = friend_request["id"]
            status, accepted, _ = call("POST", f"/v1/friends/requests/{fid}/accept", {}, token=btoken)
            check(status == 200 and accepted["status"] == "accepted", "friend request acceptance")
            status, friends, _ = call("GET", "/v1/friends", token=atoken)
            check(status == 200 and any(x["id"] == bid for x in friends["friends"]), "friend list")
            status, notes, _ = call("GET", "/v1/notifications?unreadOnly=true", token=btoken)
            check(status == 200 and any(n["type"] == "friend.requested" for n in notes), "notification persistence")
            note = next(n for n in notes if n["type"] == "friend.requested")
            status, _, _ = call("POST", f"/v1/notifications/{note['id']}/read", {}, token=btoken)
            check(status == 200, "notification read marker")

            status, party, _ = call("POST", "/v1/parties", {"maxMembers": 4}, token=atoken)
            check(status == 201, "party creation")
            pid = party["id"]
            status, invite, _ = call("POST", f"/v1/parties/{pid}/invites", {"playerId": bid}, token=atoken)
            check(status == 201, "party invitation")
            status, joined_party, _ = call("POST", f"/v1/parties/{pid}/invites/{invite['id']}/accept", {}, token=btoken)
            check(status == 200 and joined_party["status"] == "joined", "party invite acceptance")
            status, party_info, _ = call("GET", f"/v1/parties/{pid}", token=atoken)
            check(status == 200 and len(party_info["members"]) == 2, "party membership read")

            status, world, _ = call("POST", "/v1/worlds", {"name": "Test Room", "description": "A project-owned test world", "tags": ["smoke", "social"], "maxPlayers": 8}, token=atoken)
            check(status == 201 and not world["isPublished"], "world creation defaults private")
            wid = world["id"]
            status, published, _ = call("POST", f"/v1/worlds/{wid}/publish", {}, token=atoken)
            check(status == 200 and published["isPublished"], "world publishing")
            status, worlds, _ = call("GET", "/v1/worlds?q=Test", token=btoken)
            check(status == 200 and any(row["world"]["id"] == wid for row in worlds), "published world discovery")
            status, _, _ = call("POST", f"/v1/worlds/{wid}/favorites", {}, token=btoken)
            check(status == 200, "world favorite")
            status, review, _ = call("POST", f"/v1/worlds/{wid}/reviews", {"rating": 5, "text": "works"}, token=btoken)
            check(status == 200 and review["rating"] == 5, "world review")

            status, room_response, _ = call("POST", "/v1/rooms", {"worldId": wid, "capacity": 3}, token=atoken)
            check(status == 201 and room_response["joinTicket"], "room allocation and join ticket")
            rid = room_response["room"]["id"]
            status, joined, _ = call("POST", f"/v1/rooms/{rid}/join", {}, token=btoken)
            check(status == 200 and joined["roomId"] == rid, "room join")
            status, room_players, _ = call("GET", f"/v1/rooms/{rid}/players", token=atoken)
            check(status == 200 and len(room_players) == 2, "room player list")
            room_realtime = subprocess.Popen(
                [node, str(ROOT / "tests" / "room_state_smoke.mjs"), base, atoken, rid],
                cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            )
            selector = selectors.DefaultSelector()
            selector.register(room_realtime.stdout, selectors.EVENT_READ)
            room_ready = ""
            deadline = time.time() + 15
            while time.time() < deadline and not room_ready:
                if room_realtime.poll() is not None:
                    break
                for key, _ in selector.select(timeout=0.25):
                    room_ready = key.fileobj.readline().strip()
                    if room_ready != "READY": break
            selector.close()
            check(room_ready == "READY", "SignalR room-state client handshake", {"line": room_ready, "exit": room_realtime.poll()})
            room_stdout, room_stderr = room_realtime.communicate(timeout=15)
            check(room_realtime.returncode == 0 and "ROOM_STATE_OK" in room_stdout,
                  "SignalR room join and state synchronization", {"stdout": room_stdout, "stderr": room_stderr})
            room_realtime = None
            status, voice, _ = call("POST", f"/v1/voice/rooms/{rid}/join", {}, token=btoken)
            check(status == 200 and voice["voiceToken"], "voice authorization session")
            status, _, _ = call("POST", f"/v1/voice/rooms/{rid}/leave", {}, token=btoken)
            check(status == 204, "voice session end")
            status, match, _ = call("POST", "/v1/matchmaking/requests", {"worldId": wid}, token=atoken)
            check(status == 200 and match["status"] == "matched", "matchmaking room reservation")
            status, queue_world, _ = call("POST", "/v1/worlds", {"name": "Queue Test", "description": "Filtered queue"}, token=atoken)
            check(status == 201, "matchmaking world creation")
            qwid = queue_world["id"]
            call("POST", f"/v1/worlds/{qwid}/publish", {}, token=atoken)
            status, wrong_region_room, _ = call("POST", "/v1/rooms", {"worldId": qwid, "capacity": 4, "region": "na", "mode": "chill"}, token=atoken)
            check(status == 201, "filtered room allocation")
            status, pending, _ = call("POST", "/v1/matchmaking/requests", {"worldId": qwid, "region": "eu", "mode": "chill"}, token=btoken)
            check(status == 202 and pending["status"] == "searching", "region filter keeps request queued")
            status, matching_room, _ = call("POST", "/v1/rooms", {"worldId": qwid, "capacity": 4, "region": "eu", "mode": "chill"}, token=atoken)
            check(status == 201, "matching region room creation")
            status, matched_queue, _ = call("GET", f"/v1/matchmaking/requests/{pending['id']}", token=btoken)
            check(status == 200 and matched_queue["status"] == "matched" and matched_queue["roomId"] == matching_room["room"]["id"], "waiting request auto-allocation")
            status, queue_join, _ = call("POST", f"/v1/rooms/{matched_queue['roomId']}/join", {}, token=btoken)
            check(status == 200 and queue_join["joinTicket"], "matched room join ticket")

            status, reward, _ = call("POST", "/v1/rewards/claim", {"rewardId": "welcome"}, token=atoken)
            check(status == 200 and reward["coinsGranted"] == 100, "welcome reward claim")
            status, _, _ = call("POST", "/v1/rewards/claim", {"rewardId": "welcome"}, token=atoken)
            check(status == 409, "duplicate reward prevented")
            status, inventory, _ = call("GET", "/v1/inventory", token=atoken)
            check(status == 200 and any(x["definitionId"] == "welcome-badge" for x in inventory), "reward inventory grant")
            badge = next(x for x in inventory if x["definitionId"] == "welcome-badge")
            status, equipped, _ = call("POST", "/v1/inventory/equip", {"inventoryId": badge["id"]}, token=atoken)
            check(status == 200 and equipped["equipped"], "inventory equip")
            status, _, _ = call("POST", "/v1/inventory/unequip", {"inventoryId": badge["id"]}, token=atoken)
            check(status == 200, "inventory unequip")
            status, entitlements, _ = call("GET", "/v1/entitlements", token=atoken)
            check(status == 200 and any(x["definitionId"] == "welcome-badge" for x in entitlements), "entitlement persistence")

            status, conversation, _ = call("POST", "/v1/chat/conversations", {"playerIds": [bid]}, token=atoken)
            check(status == 201, "conversation creation")
            cid = conversation["id"]
            status, message, _ = call("POST", f"/v1/chat/conversations/{cid}/messages", {"text": "hello from an independent client"}, token=atoken)
            check(status == 201, "chat message send")
            mid = message["id"]
            status, messages, _ = call("GET", f"/v1/chat/conversations/{cid}/messages", token=btoken)
            check(status == 200 and any(x["id"] == mid for x in messages), "chat message read")

            status, report, _ = call("POST", "/v1/reports", {"targetPlayerId": bid, "category": "conduct", "description": "Smoke test report"}, token=atoken)
            check(status == 201, "moderation report submission")
            status, boot, _ = call("POST", "/v1/admin/bootstrap", {"playerId": aid}, headers={"X-Admin-Bootstrap": "test-bootstrap-secret"})
            check(status == 200 and boot["isAdmin"], "one-time administrator bootstrap")
            status, report_list, _ = call("GET", "/v1/admin/reports", token=atoken)
            check(status == 200 and any(x["id"] == report["id"] for x in report_list), "admin report queue")
            status, resolved, _ = call("POST", f"/v1/admin/reports/{report['id']}/resolve", {"resolution": "Reviewed by smoke test"}, token=atoken)
            check(status == 200 and resolved["status"] == "resolved", "report resolution")
            status, created_item, _ = call("POST", "/v1/admin/catalog/items", {"id": "smoke-hat", "name": "Smoke Hat", "description": "Test item", "equipmentSlot": "head"}, token=atoken)
            check(status == 201, "admin catalog item creation")
            status, grant, _ = call("POST", f"/v1/admin/players/{bid}/inventory", {"definitionId": "smoke-hat", "quantity": 1, "source": "smoke"}, token=atoken)
            check(status == 200, "admin inventory grant")
            status, _, _ = call("POST", f"/v1/admin/players/{bid}/suspend", {"reason": "test"}, token=atoken)
            check(status == 200, "admin suspension")
            status, _, _ = call("GET", "/v1/auth/me", token=btoken)
            check(status == 401, "suspension revokes sessions")
            status, _, _ = call("POST", f"/v1/admin/players/{bid}/unsuspend", {}, token=atoken)
            check(status == 200, "admin unsuspension")
            status, bob_login, _ = call("POST", "/v1/auth/login", {"userName": "bob", "password": "Bob-Strong-Password-1"})
            check(status == 200, "login after unsuspension")
            btoken = bob_login["accessToken"]
            status, logs, _ = call("GET", "/v1/admin/audit-events", token=atoken)
            check(status == 200 and len(logs) > 0, "moderation audit log")

            boundary = "revival-smoke-boundary"
            file_bytes = b"project-owned test asset\n"
            multipart = (
                f"--{boundary}\r\nContent-Disposition: form-data; name=\"File\"; filename=\"asset.txt\"\r\nContent-Type: text/plain\r\n\r\n".encode()
                + file_bytes
                + f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"Provenance\"\r\n\r\ncreated for smoke test\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"AuthorizationReference\"\r\n\r\nproject-owned smoke test fixture\r\n--{boundary}--\r\n".encode()
            )
            status, asset, _ = call("POST", f"/v1/worlds/{wid}/assets", headers={"Content-Type": f"multipart/form-data; boundary={boundary}"}, token=atoken, raw=multipart)
            check(status == 201, "authorized asset upload")
            status, manifest, _ = call("GET", f"/v1/assets/manifests/{asset['manifestId']}", token=btoken)
            check(status == 200 and asset["id"] in manifest["assets"], "asset manifest delivery")
            status, downloaded, _ = call("GET", f"/v1/assets/{asset['id']}", token=btoken)
            check(status == 200 and downloaded == file_bytes, "authorized asset download")
            status, revoked, _ = call("DELETE", f"/v1/worlds/{wid}/assets/{asset['id']}", token=atoken)
            check(status == 200 and revoked["revoked"], "asset revocation")
            status, _, _ = call("GET", f"/v1/assets/{asset['id']}", token=btoken)
            check(status == 404, "revoked asset no longer served")
            status, _, _ = call("POST", f"/v1/worlds/{wid}/unpublish", {}, token=atoken)
            check(status == 200, "world unpublish")
            status, _, _ = call("GET", f"/v1/rooms/{rid}/players", token=btoken)
            check(status == 404, "private room roster hidden after unpublish")

            status, backup, _ = call("POST", "/v1/admin/backups", {}, token=atoken)
            check(status == 201 and backup["sizeBytes"] > 0, "SQLite online backup")
            status, _, _ = call("POST", "/v1/auth/logout", {}, token=btoken)
            check(status == 204, "logout")
            status, _, _ = call("GET", "/v1/auth/me", token=btoken)
            check(status == 401, "logout revokes bearer session")
            failed_logins = [call("POST", "/v1/auth/login", {"userName": "locktest", "password": "wrong-password"})[0] for _ in range(8)]
            check(all(code == 401 for code in failed_logins), "invalid login attempts counted")
            status, lock_response, _ = call("POST", "/v1/auth/login", {"userName": "locktest", "password": "wrong-password"})
            check(status == 429 and lock_response["error"] == "account_temporarily_locked", "timed account lockout")
            process.terminate()
            process.wait(timeout=10)
            process = subprocess.Popen(
                [dotnet, "run", "--project", str(PROJECT), "--no-build"],
                cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
            )
            restarted = False
            for _ in range(80):
                if process.poll() is not None:
                    break
                try:
                    status, health, _ = call("GET", "/health")
                    if status == 200 and health["database"] == "connected":
                        restarted = True
                        break
                except Exception:
                    pass
                time.sleep(0.25)
            check(restarted, "restart and migration re-application")
            status, alice_after_restart, _ = call("POST", "/v1/auth/login", {"userName": "alice", "password": "Alice-Strong-Password-1"})
            check(status == 200, "password/session store persistence after restart")
            status, friends_after_restart, _ = call("GET", "/v1/friends", token=alice_after_restart["accessToken"])
            check(status == 200 and any(row["id"] == bid for row in friends_after_restart["friends"]), "friendship persistence after restart")
            status, world_after_restart, _ = call("GET", f"/v1/worlds/{wid}", token=alice_after_restart["accessToken"])
            persisted_world = world_after_restart.get("world", world_after_restart) if isinstance(world_after_restart, dict) else {}
            check(status == 200 and persisted_world.get("id") == wid, "world persistence after restart", {"status": status, "body": world_after_restart})
            status, lock_after_restart, _ = call("POST", "/v1/auth/login", {"userName": "locktest", "password": "wrong-password"})
            check(status == 429 and lock_after_restart["error"] == "account_temporarily_locked", "account lockout persistence after restart")
            print("All smoke tests passed.")
        finally:
            if room_realtime is not None and room_realtime.poll() is None:
                room_realtime.terminate()
                try:
                    room_realtime.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    room_realtime.kill()
            if realtime is not None and realtime.poll() is None:
                realtime.terminate()
                try:
                    realtime.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    realtime.kill()
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            log.close()


if __name__ == "__main__":
    main()
