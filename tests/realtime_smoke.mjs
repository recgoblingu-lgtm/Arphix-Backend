const [baseUrl, accessToken, expectedEvent] = process.argv.slice(2);
if (!baseUrl || !accessToken || !expectedEvent) throw new Error("usage: realtime_smoke.mjs <base-url> <access-token> <event-name>");

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
const timeout = setTimeout(() => {
  console.error(`Timed out waiting for ${expectedEvent}`);
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
      continue;
    }
    if (message.type === 1 && message.target === expectedEvent) {
      clearTimeout(timeout);
      console.log(`EVENT ${message.target}`);
      socket.close();
      process.exit(0);
    }
    if (message.type === 7) throw new Error(`SignalR closed: ${message.error ?? "unknown error"}`);
  }
});
socket.addEventListener("error", () => {
  clearTimeout(timeout);
  console.error("WebSocket connection failed");
  process.exit(2);
});
