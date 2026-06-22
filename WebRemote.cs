using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TheaterDim;

// Tiny LAN remote: raw TcpListener HTTP (no admin/urlacl needed, unlike HttpListener http.sys).
// Every request must carry ?t=<token>. Serves one mobile page + /cmd/<name> endpoints.
class WebRemote
{
    readonly Settings cfg;
    TcpListener? server;
    Thread? thread;
    volatile bool running;

    public WebRemote(Settings cfg) { this.cfg = cfg; }

    public bool Running => running;

    public void Start()
    {
        Stop();
        server = new TcpListener(IPAddress.Any, cfg.Port);
        server.Start();
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "TheaterDim-Web" };
        thread.Start();
    }

    public void Stop()
    {
        running = false;
        try { server?.Stop(); } catch { /* ignore */ }
        server = null;
    }

    void Loop()
    {
        while (running)
        {
            TcpClient client;
            try { client = server!.AcceptTcpClient(); }
            catch { break; } // server stopped/disposed
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    void Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                client.ReceiveTimeout = 4000;
                var (path, query) = ReadRequest(stream);
                if (path == null) return;

                // Page (also token-gated, so the shared URL must include ?t=).
                if (path == "/" || path == "/index.html")
                {
                    if (!TokenOk(query)) { Send(stream, 403, "text/plain", "forbidden"); return; }
                    Send(stream, 200, "text/html; charset=utf-8", Html());
                    return;
                }

                // Commands: /cmd/<name>
                if (path.StartsWith("/cmd/"))
                {
                    if (!TokenOk(query)) { SendJson(stream, 403, "{\"ok\":false,\"err\":\"token\"}"); return; }
                    string name = path.Substring("/cmd/".Length);
                    if (PotPlayer.Commands.ContainsKey(name))
                    {
                        bool found = PotPlayer.Send(name);
                        SendJson(stream, 200, $"{{\"ok\":true,\"found\":{(found ? "true" : "false")}}}");
                    }
                    else SendJson(stream, 404, "{\"ok\":false,\"err\":\"unknown\"}");
                    return;
                }

                Send(stream, 404, "text/plain", "not found");
            }
        }
        catch { /* per-connection errors are non-fatal */ }
    }

    // Reads request line + headers, returns (path, query). Minimal HTTP/1.1.
    static (string? path, string query) ReadRequest(NetworkStream stream)
    {
        var bytes = new List<byte>(256);
        var buf = new byte[1];
        int matched = 0; // count of consecutive CRLFCRLF bytes
        while (matched < 4)
        {
            int n;
            try { n = stream.Read(buf, 0, 1); } catch { break; }
            if (n <= 0) break;
            byte b = buf[0];
            bytes.Add(b);
            char c = (char)b;
            bool expected = (matched == 0 && c == '\r') || (matched == 1 && c == '\n')
                         || (matched == 2 && c == '\r') || (matched == 3 && c == '\n');
            if (expected) matched++;
            else matched = (c == '\r') ? 1 : 0;
            if (bytes.Count > 8192) break; // guard against oversized headers
        }

        string text = Encoding.ASCII.GetString(bytes.ToArray());
        string firstLine = text.Split('\n')[0].Trim();
        string[] parts = firstLine.Split(' ');
        if (parts.Length < 2) return (null, "");

        string rawUrl = parts[1];
        int q = rawUrl.IndexOf('?');
        if (q < 0) return (rawUrl, "");
        return (rawUrl.Substring(0, q), rawUrl.Substring(q + 1));
    }

    bool TokenOk(string query)
    {
        foreach (var kv in query.Split('&'))
        {
            int eq = kv.IndexOf('=');
            if (eq < 0) continue;
            if (kv.Substring(0, eq) == "t"
                && Uri.UnescapeDataString(kv.Substring(eq + 1)) == cfg.Token)
                return true;
        }
        return false;
    }

    static void Send(NetworkStream s, int code, string contentType, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string reason = code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Error";
        string head =
            $"HTTP/1.1 {code} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(head);
        s.Write(hb, 0, hb.Length);
        s.Write(payload, 0, payload.Length);
    }

    static void SendJson(NetworkStream s, int code, string json)
        => Send(s, code, "application/json", json);

    public string Url() => $"http://{LocalIp()}:{cfg.Port}/?t={cfg.Token}";

    // Pick the real router-facing IPv4. Scores NICs so virtual adapters
    // (WSL/Hyper-V vEthernet, Tailscale, VM) lose to the gateway-bearing LAN NIC.
    public static string LocalIp()
    {
        string best = "127.0.0.1";
        int bestScore = int.MinValue;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any));

                string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool virtualish = desc.Contains("virtual") || desc.Contains("hyper-v")
                    || desc.Contains("vethernet") || desc.Contains("wsl")
                    || desc.Contains("tailscale") || desc.Contains("vmware")
                    || desc.Contains("virtualbox") || desc.Contains("loopback");

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254")) continue; // APIPA

                    int score = 0;
                    if (hasGateway) score += 100;                                 // real LAN route
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 10;
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 8;
                    if (virtualish) score -= 50;

                    if (score > bestScore) { bestScore = score; best = ip; }
                }
            }
        }
        catch { /* fall through to best so far */ }
        return best;
    }

    string Html()
    {
        // $$ raw-interpolated: {{token}} interpolates; single { } are literal (CSS/JS safe).
        string token = cfg.Token;
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no">
<title>TheaterDim Remote</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; user-select: none; }
  body { margin:0; font-family: system-ui, -apple-system, sans-serif; background:#0b0b0f;
         color:#eee; min-height:100vh; display:flex; flex-direction:column; }
  header { padding:16px; text-align:center; font-weight:600; letter-spacing:.5px; color:#8ab4ff; }
  #status { font-size:13px; color:#777; text-align:center; padding-bottom:6px; min-height:18px; }
  .grid { flex:1; display:grid; gap:10px; padding:12px;
          grid-template-columns: repeat(3, 1fr); align-content:start; }
  button { border:none; border-radius:18px; background:#1b1d27; color:#eee;
           font-size:17px; padding:24px 6px; cursor:pointer;
           transition:transform .05s ease, background .1s ease; }
  button:active { transform:scale(.94); background:#2a2d3a; }
  .play { background:#1f3a5f; }
  .seek { background:#1d2a33; }
  .full { background:#27314a; }
  .vol  { background:#223240; }
  .stop { background:#3a1f24; }
  small { display:block; font-size:11px; opacity:.6; margin-top:4px; }
</style>
</head>
<body>
  <header>🎬 TheaterDim Remote</header>
  <div id="status">ready</div>
  <div class="grid">
    <button onclick="cmd('prev')">⏮<small>prev</small></button>
    <button class="play" onclick="cmd('playpause')">⏯<small>play/pause</small></button>
    <button onclick="cmd('next')">⏭<small>next</small></button>

    <button class="seek" onclick="cmd('seekback30')">⏪<small>−30s</small></button>
    <button class="seek" onclick="cmd('seekback5')">◀<small>−5s</small></button>
    <button class="seek" onclick="cmd('seekfwd5')">▶<small>+5s</small></button>

    <button class="seek" onclick="cmd('seekfwd30')">⏩<small>+30s</small></button>
    <button class="full" onclick="cmd('fullscreen')">⛶<small>fullscreen</small></button>
    <button class="stop" onclick="cmd('stop')">⏹<small>stop</small></button>

    <button class="vol" onclick="cmd('voldown')">🔉<small>vol −</small></button>
    <button class="vol" onclick="cmd('mute')">🔇<small>mute</small></button>
    <button class="vol" onclick="cmd('volup')">🔊<small>vol +</small></button>

    <button onclick="cmd('subs')">💬<small>subs</small></button>
    <button onclick="cmd('playlist')">📂<small>playlist</small></button>
    <button onclick="cmd('osd')">ℹ️<small>info</small></button>
  </div>
<script>
  const T = "{{token}}";
  const s = document.getElementById('status');
  async function cmd(name){
    try{
      const r = await fetch('/cmd/'+name+'?t='+T, {method:'POST'});
      const j = await r.json();
      s.textContent = j.found ? (name + ' ✓') : 'PotPlayer not running';
      s.style.color = j.found ? '#6c6' : '#c66';
    }catch(e){
      s.textContent = 'connection lost';
      s.style.color = '#c66';
    }
  }
</script>
</body>
</html>
""";
    }
}
