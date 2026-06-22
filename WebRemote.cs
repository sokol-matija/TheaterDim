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
    readonly RemoteShared shared;
    TcpListener? server;
    Thread? thread;
    volatile bool running;

    public WebRemote(Settings cfg, RemoteShared shared)
    {
        this.cfg = cfg;
        this.shared = shared;
    }

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

                // Live status: is PotPlayer running, its title, dim state
                if (path == "/status")
                {
                    if (!TokenOk(query)) { SendJson(stream, 403, "{\"ok\":false}"); return; }
                    bool running2 = PotPlayer.IsRunning;
                    string title = JsonEscape(running2 ? PotPlayer.Title() : "");
                    SendJson(stream, 200,
                        $"{{\"running\":{(running2 ? "true" : "false")},\"title\":\"{title}\"," +
                        $"\"dim\":{(shared.DimActive ? "true" : "false")},\"theater\":{(shared.ForceTheater ? "true" : "false")}}}");
                    return;
                }

                // Manual theater dim toggle: /theater?t=..&m=on|off|toggle
                if (path == "/theater")
                {
                    if (!TokenOk(query)) { SendJson(stream, 403, "{\"ok\":false}"); return; }
                    string mode = QueryValue(query, "m");
                    shared.ForceTheater = mode switch
                    {
                        "on" => true,
                        "off" => false,
                        _ => !shared.ForceTheater,
                    };
                    SendJson(stream, 200, $"{{\"ok\":true,\"theater\":{(shared.ForceTheater ? "true" : "false")}}}");
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

    bool TokenOk(string query) => QueryValue(query, "t") == cfg.Token;

    static string QueryValue(string query, string key)
    {
        foreach (var kv in query.Split('&'))
        {
            int eq = kv.IndexOf('=');
            if (eq < 0) continue;
            if (kv.Substring(0, eq) == key)
                return Uri.UnescapeDataString(kv.Substring(eq + 1));
        }
        return "";
    }

    static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        return sb.ToString();
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
        // Non-interpolated raw string: { } $ ` are all literal. Token injected via Replace.
        const string page =
"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no">
<title>TheaterDim Remote</title>
<style>
  :root { color-scheme: dark; --bg:#0b0b0f; --card:#16181f; --card2:#1d2030; --ink:#e8eaf0; --mut:#7c8198; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; user-select:none; }
  body { margin:0; font-family:system-ui,-apple-system,"Segoe UI",sans-serif; background:var(--bg);
         color:var(--ink); min-height:100dvh; display:flex; flex-direction:column; }
  header { display:flex; align-items:center; justify-content:center; gap:8px;
           padding:16px 12px 4px; font-weight:650; letter-spacing:.3px; }
  header svg { width:22px; height:22px; color:#8ab4ff; }
  .pill { margin:10px 12px 4px; padding:10px 14px; border-radius:14px; background:var(--card);
          display:flex; align-items:center; gap:10px; font-size:13px; min-height:42px; }
  .dot { width:9px; height:9px; border-radius:50%; background:#555; flex:0 0 auto; }
  .dot.on { background:#3ad07a; box-shadow:0 0 8px #3ad07a88; }
  .dot.off { background:#c75c5c; }
  #title { color:var(--mut); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .theater { margin:8px 12px; padding:16px; border:none; border-radius:16px; cursor:pointer;
             background:var(--card2); color:var(--ink); font-size:16px; font-weight:600;
             display:flex; align-items:center; justify-content:center; gap:10px;
             transition:background .15s, box-shadow .15s; }
  .theater svg { width:22px; height:22px; }
  .theater.on { background:linear-gradient(180deg,#caa24a,#b8862f); color:#1a1206;
                box-shadow:0 0 18px #e0a33e55; }
  .grid { flex:1; display:grid; gap:10px; padding:10px 12px 16px;
          grid-template-columns:repeat(3,1fr); align-content:start; }
  .btn { border:none; border-radius:16px; background:var(--card); color:var(--ink);
         padding:18px 6px 12px; cursor:pointer; display:flex; flex-direction:column;
         align-items:center; gap:7px; transition:transform .05s, background .1s; }
  .btn:active { transform:scale(.93); background:#262a38; }
  .btn svg { width:26px; height:26px; }
  .btn small { font-size:11px; color:var(--mut); }
  .btn.play { background:#1f3a5f; } .btn.play svg { color:#9fc4ff; }
  .btn.seek svg { color:#86b8c9; }
  .btn.full svg { color:#a6b0e0; }
  .btn.vol svg  { color:#9fc7b0; }
  .btn.stop { background:#3a1f24; } .btn.stop svg { color:#e08a8a; }
</style>
</head>
<body>
  <header><span id="brand"></span> TheaterDim</header>
  <div class="pill"><span class="dot" id="dot"></span><span id="title">connecting…</span></div>
  <button class="theater" id="theater" onclick="theater()"><span id="ticon"></span><span>Theater — dim displays</span></button>
  <div class="grid" id="grid"></div>
<script>
  const T = "__TOKEN__";
  const ICON = {
    skipback:`<path d="M17.971 4.285A2 2 0 0 1 21 6v12a2 2 0 0 1-3.029 1.715l-9.997-5.998a2 2 0 0 1-.003-3.432z"/><path d="M3 20V4"/>`,
    play:`<path d="M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z"/>`,
    skipfwd:`<path d="M21 4v16"/><path d="M6.029 4.285A2 2 0 0 0 3 6v12a2 2 0 0 0 3.029 1.715l9.997-5.998a2 2 0 0 0 .003-3.432z"/>`,
    rewind:`<path d="M12 6a2 2 0 0 0-3.414-1.414l-6 6a2 2 0 0 0 0 2.828l6 6A2 2 0 0 0 12 18z"/><path d="M22 6a2 2 0 0 0-3.414-1.414l-6 6a2 2 0 0 0 0 2.828l6 6A2 2 0 0 0 22 18z"/>`,
    chevleft:`<path d="m15 18-6-6 6-6"/>`,
    chevright:`<path d="m9 18 6-6-6-6"/>`,
    fastfwd:`<path d="M12 6a2 2 0 0 1 3.414-1.414l6 6a2 2 0 0 1 0 2.828l-6 6A2 2 0 0 1 12 18z"/><path d="M2 6a2 2 0 0 1 3.414-1.414l6 6a2 2 0 0 1 0 2.828l-6 6A2 2 0 0 1 2 18z"/>`,
    maximize:`<path d="M8 3H5a2 2 0 0 0-2 2v3"/><path d="M21 8V5a2 2 0 0 0-2-2h-3"/><path d="M3 16v3a2 2 0 0 0 2 2h3"/><path d="M16 21h3a2 2 0 0 0 2-2v-3"/>`,
    square:`<rect width="18" height="18" x="3" y="3" rx="2"/>`,
    vol1:`<path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/><path d="M16 9a5 5 0 0 1 0 6"/>`,
    vol2:`<path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/><path d="M16 9a5 5 0 0 1 0 6"/><path d="M19.364 18.364a9 9 0 0 0 0-12.728"/>`,
    volx:`<path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/><line x1="22" x2="16" y1="9" y2="15"/><line x1="16" x2="22" y1="9" y2="15"/>`,
    captions:`<rect width="18" height="14" x="3" y="5" rx="2" ry="2"/><path d="M7 15h4M15 15h2M7 11h2M13 11h4"/>`,
    listvideo:`<path d="M21 5H3"/><path d="M10 12H3"/><path d="M10 19H3"/><path d="M15 12.003a1 1 0 0 1 1.517-.859l4.997 2.997a1 1 0 0 1 0 1.718l-4.997 2.997a1 1 0 0 1-1.517-.86z"/>`,
    info:`<circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/>`,
    drama:`<path d="M10 11h.01"/><path d="M14 6h.01"/><path d="M18 6h.01"/><path d="M6.5 13.1h.01"/><path d="M22 5c0 9-4 12-6 12s-6-3-6-12c0-2 2-3 6-3s6 1 6 3"/><path d="M17.4 9.9c-.8.8-2 .8-2.8 0"/><path d="M10.1 7.1C9 7.2 7.7 7.7 6 8.6c-3.5 2-4.7 3.9-3.7 5.6 4.5 7.8 9.5 8.4 11.2 7.4.9-.5 1.9-2.1 1.9-4.7"/><path d="M9.1 16.5c.3-1.1 1.4-1.7 2.4-1.4"/>`
  };
  function svg(k){ return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'+ICON[k]+'</svg>'; }

  const BTNS = [
    ['prev','skipback','prev',''],
    ['playpause','play','play / pause','play'],
    ['next','skipfwd','next',''],
    ['seekback30','rewind','-30s','seek'],
    ['seekback5','chevleft','-5s','seek'],
    ['seekfwd5','chevright','+5s','seek'],
    ['seekfwd30','fastfwd','+30s','seek'],
    ['fullscreen','maximize','fullscreen','full'],
    ['stop','square','stop','stop'],
    ['voldown','vol1','vol -','vol'],
    ['mute','volx','mute','vol'],
    ['volup','vol2','vol +','vol'],
    ['subs','captions','subtitles',''],
    ['playlist','listvideo','playlist',''],
    ['osd','info','info','']
  ];

  document.getElementById('brand').innerHTML = svg('drama');
  document.getElementById('ticon').innerHTML = svg('drama');
  document.getElementById('grid').innerHTML = BTNS.map(function(b){
    return '<button class="btn '+b[3]+'" onclick="cmd(\''+b[0]+'\')">'+svg(b[1])+'<small>'+b[2]+'</small></button>';
  }).join('');

  const dot = document.getElementById('dot');
  const titleEl = document.getElementById('title');
  const theaterBtn = document.getElementById('theater');

  async function cmd(n){
    try{ await fetch('/cmd/'+n+'?t='+T, {method:'POST'}); poll(); }
    catch(e){ offline(); }
  }
  async function theater(){
    try{
      const r = await fetch('/theater?m=toggle&t='+T, {method:'POST'});
      const j = await r.json();
      theaterBtn.classList.toggle('on', j.theater);
    }catch(e){ offline(); }
  }
  function offline(){
    dot.className = 'dot off'; titleEl.textContent = 'connection lost'; titleEl.style.color = '#c75c5c';
  }
  async function poll(){
    try{
      const r = await fetch('/status?t='+T);
      const j = await r.json();
      titleEl.style.color = '';
      if(j.running){
        dot.className = 'dot on';
        titleEl.textContent = j.title && j.title.length ? j.title : 'PotPlayer running';
        titleEl.style.color = 'var(--ink)';
      } else {
        dot.className = 'dot off';
        titleEl.textContent = 'PotPlayer not running';
      }
      theaterBtn.classList.toggle('on', j.theater);
    }catch(e){ offline(); }
  }
  poll();
  setInterval(poll, 2000);
</script>
</body>
</html>
""";
        return page.Replace("__TOKEN__", cfg.Token);
    }
}
