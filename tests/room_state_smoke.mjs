const [baseUrl, accessToken, roomId] = process.argv.slice(2);
if (!baseUrl || !accessToken || !roomId) throw new Error("usage: room_state_smoke.mjs <base-url> <access-token> <room-id>");

const negotiation = await fetch(`${baseUrl}/v1/realtime/negotiate?negotiateVersion=1`, {
  method: "POST",
  headers: { Authorization: `Bearer ${accessToken}` },
});
if (!negotiation.ok) throw new Error(`SignalR negotiate failed: ${negotiation.status} ${await negotiation.text()}`);
const { connectionToken } = await negotiation.json();
const socketUrl = new URL(`${baseUrl.replace(/^http/, "ws")}/v1/realtime`);
socketUrl.searchParams.set("id", connectionToken);
socketUrl.searchParams.set("access_token", accessToken);
const socket = new WebSocket(socketUrl);
let buffer = "";
let ready = false;
let joined = false;
const timeout = setTimeout(() => {
  console.error("Timed out waiting for room-state synchronization");
  socket.close();
  process.exit(2);
}, 15000);

socket.addEventListener("open", () => socket.send(JSON.stringify({ protocol: "json", version: 1 }) + "\x1e"));
socket.addEventListener("message", ({ data }) => {
  buffer += typeof data === "string" ? data : Buffer.from(data).toString("utf8");
  const frames = buffer.split("\x1e");
  buffer = frames.pop() ?? "";
  for (const frame of frames) {
    if (!frame) continue;
    let message;
    try { message = JSON.parse(frame); } catch { continue; }
    if (message.type === 1 && message.target === "session.ready" && !ready) {
      ready = true;
      console.log("READY");
      socket.send(JSON.stringify({ type: 1, invocationId: "1", target: "JoinRoom", arguments: [roomId] }) + "\x1e");
      continue;
    }
    if (message.type === 1 && message.target === "room.state_changed") {
      if (!joined) {
        if (message.arguments?.[0]?.sequence !== 0) throw new Error("Unexpected initial room sequence");
        joined = true;
        console.log("JOINED");
        socket.send(JSON.stringify({ type: 1, invocationId: "2", target: "PublishRoomState", arguments: [roomId, 1, { smoke: true }] }) + "\x1e");
      } else if (message.arguments?.[0]?.sequence === 1 && message.arguments?.[0]?.state?.smoke === true) {
        clearTimeout(timeout);
        console.log("ROOM_STATE_OK");
        socket.close();
        process.exit(0);
      }
    }
    if (message.type === 3 && message.error) throw new Error(`Hub invocation failed: ${message.error}`);
    if (message.type === 7) throw new Error(`SignalR closed: ${message.error ?? "unknown error"}`);
  }
});
socket.addEventListener("error", () => {
  clearTimeout(timeout);
  console.error("WebSocket connection failed");
  process.exit(2);
});
