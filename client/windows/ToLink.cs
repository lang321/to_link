// REQ-REMOTE-20261105-01 · Windows logged-in desktop agent, .NET Framework 4.5+ / C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ToLink {
    public sealed class AgentConfig {
        public string Server { get; set; }
        public string DeviceId { get; set; }
        public string Token { get; set; }
        public string EnrollmentToken { get; set; }
        public string AccessPassword { get; set; }
        public int Fps { get; set; }
        public int MaxWidth { get; set; }
        public int Quality { get; set; }
        public Dictionary<string, ServerProfile> Servers { get; set; }
    }
    internal static class Program {
        public static EventWaitHandle ShowEvent;
#if SINGLE_FILE
        public static readonly string BaseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToLink");
#else
        public static readonly string BaseDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location);
#endif
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] static void Main(string[] args) {
            // .NET Framework defaults to coalescing small frame headers; avoid delayed-ACK stalls.
            System.Net.ServicePointManager.UseNagleAlgorithm = false;
            SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (Array.IndexOf(args, "--test-window") >= 0) { Directory.CreateDirectory(BaseDirectory); Application.Run(new DiagnosticForm()); return; }
            bool created;
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ToLinkRemoteShow"))
            using (var mutex = new Mutex(true, "Local\\ToLinkRemoteAgent", out created)) {
                if (!created) { showEvent.Set(); return; }
                ShowEvent = showEvent;
                try {
#if SINGLE_FILE
                    SingleFileRuntime.Prepare(BaseDirectory);
#endif
                    var json = new JavaScriptSerializer();
                    var config = json.Deserialize<AgentConfig>(File.ReadAllText(Path.Combine(Program.BaseDirectory, "agent.json")));
                    Uri uri;
                    if (!Uri.TryCreate(config.Server, UriKind.Absolute, out uri) || (uri.Scheme != "ws" && uri.Scheme != "wss") || (string.IsNullOrEmpty(config.Token) && string.IsNullOrEmpty(config.EnrollmentToken))) throw new Exception("agent.json 缺少服务器地址或注册凭据");
                    if (string.IsNullOrEmpty(config.AccessPassword)) { var bytes = new byte[12]; using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes); config.AccessPassword = Convert.ToBase64String(bytes); }
                    config.Fps = Math.Max(1, Math.Min(15, config.Fps)); config.MaxWidth = Math.Max(640, Math.Min(2560, config.MaxWidth)); config.Quality = Math.Max(30, Math.Min(90, config.Quality));
                    Application.Run(new MainForm(config, Array.IndexOf(args, "--start") >= 0));
                } catch (Exception ex) { Log("startup: " + ex.Message); MessageBox.Show(ex.Message, "ToLink 启动失败"); }
            }
        }
        public static void SaveConfig(AgentConfig config) {
            ServerSettings.Remember(config);
            var file = Path.Combine(Program.BaseDirectory, "agent.json");
            File.WriteAllText(file + ".tmp", new JavaScriptSerializer().Serialize(config), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(file + ".tmp", file, null); else File.Move(file + ".tmp", file);
        }
        public static void Log(string message) {
            try {
                lock (typeof(Program)) {
                    var file = Path.Combine(Program.BaseDirectory, "ToLink.log");
                    if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.WriteAllText(file, "");
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [ToLink] " + message + Environment.NewLine);
                }
            } catch { }
        }
    }
    internal static class Registration {
        public static string DefaultEnrollmentToken(string address) {
            using (var stream = typeof(Program).Assembly.GetManifestResourceStream("ToLink.agent.json")) {
                string json;
                if (stream != null) { using (var reader = new StreamReader(stream)) json = reader.ReadToEnd(); }
                else { string file = Path.Combine(Program.BaseDirectory, "bootstrap.json"); if (!File.Exists(file)) return null; json = File.ReadAllText(file); }
                var bootstrap = new JavaScriptSerializer().Deserialize<AgentConfig>(json);
                return ServerSettings.Normalize(bootstrap.Server) == ServerSettings.Normalize(address) ? bootstrap.EnrollmentToken : null;
            }
        }
        static Dictionary<string, object> Post(AgentConfig config, string path, string token, object data) {
            var server = new Uri(config.Server);
            var uri = new UriBuilder(server.Scheme == "wss" ? "https" : "http", server.Host, server.Port, path).Uri;
            var req = (HttpWebRequest)WebRequest.Create(uri); req.Method = "POST"; req.Proxy = null; req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            req.ContentType = "application/json"; req.Headers["Authorization"] = "Bearer " + token;
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(data)); req.ContentLength = bytes.Length;
            using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            using (var response = req.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
        }
        public static void EnsureRegistered(AgentConfig config) {
            if (!string.IsNullOrEmpty(config.DeviceId) && !string.IsNullOrEmpty(config.Token)) return;
            var result = Post(config, "/api/register", config.EnrollmentToken, new { name = Environment.MachineName, password = config.AccessPassword });
            config.DeviceId = Convert.ToString(result["deviceId"]); config.Token = Convert.ToString(result["token"]); config.EnrollmentToken = null;
            Program.SaveConfig(config); Program.Log("registered device ID=" + config.DeviceId);
        }
        public static void ChangePassword(AgentConfig config, string password) {
            if (!string.IsNullOrEmpty(config.DeviceId)) Post(config, "/api/device/password", config.Token, new { deviceId = config.DeviceId, password = password });
            config.AccessPassword = password; Program.SaveConfig(config);
        }
    }
    public sealed class MainForm : Form {
        readonly AgentConfig config; readonly Label status; readonly Button toggle; readonly NotifyIcon tray;
        readonly RemoteAgent agent; CancellationTokenSource cancellation; Task worker; bool quitting;
        public MainForm(AgentConfig c, bool autoStart) {
            config = c; Text = "ToLink · Windows 被控端"; ClientSize = new Size(570, 650); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(18, 27, 39); ForeColor = Color.FromArgb(225, 235, 245);
            var title = new Label { Text = "ToLink  远程桌面", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Left = 24, Top = 22, AutoSize = true }; Controls.Add(title);
            Controls.Add(new Label { Text = "本机设备 ID", Left = 26, Top = 78, Width = 200, Height = 25 });
            var deviceId = new TextBox { Text = string.IsNullOrEmpty(c.DeviceId) ? "启动后自动分配" : c.DeviceId, Left = 26, Top = 109, Width = 345, Height = 40, ReadOnly = true, Font = new Font(Font.FontFamily, 22, FontStyle.Bold) }; Controls.Add(deviceId);
            var copyId = new Button { Text = "复制 ID", Left = 385, Top = 110, Width = 112, Height = 42 }; copyId.Click += (s, e) => { if (!string.IsNullOrEmpty(c.DeviceId)) Clipboard.SetText(c.DeviceId); }; Controls.Add(copyId);
            Controls.Add(new Label { Text = "本机访问密码（8～128 个字符）", Left = 26, Top = 173, Width = 360, Height = 26 });
            var password = new TextBox { Text = c.AccessPassword, Left = 26, Top = 204, Width = 345, MaxLength = 128, UseSystemPasswordChar = true }; Controls.Add(password);
            var savePassword = new Button { Text = "保存密码", Left = 385, Top = 200, Width = 112, Height = 36, Enabled = !string.IsNullOrEmpty(c.DeviceId) }; Controls.Add(savePassword);
            var reveal = new CheckBox { Text = "显示密码", Left = 26, Top = 243, Width = 140 }; reveal.CheckedChanged += (s, e) => password.UseSystemPasswordChar = !reveal.Checked; Controls.Add(reveal);
            status = new Label { Text = "已停止", Left = 26, Top = 284, Width = 475, Height = 36, ForeColor = Color.FromArgb(121, 223, 183) }; Controls.Add(status);
            savePassword.Click += async (s, e) => {
                if (password.Text.Length < 8) { status.Text = "密码至少需要 8 个字符"; return; }
                savePassword.Enabled = false; toggle.Enabled = false;
                try { string value = password.Text; await Task.Run(() => Registration.ChangePassword(c, value)); status.Text = "密码已保存，旧浏览器会话已撤销"; }
                catch (Exception ex) { status.Text = "保存失败，请检查服务器连接"; Program.Log("password update: " + ex.GetType().Name); }
                finally { savePassword.Enabled = true; toggle.Enabled = true; }
            };
            toggle = new Button { Text = "启动连接", Left = 26, Top = 330, Width = 130, Height = 38 }; Controls.Add(toggle);
            Controls.Add(new Label { Text = "服务器地址", Left = 26, Top = 385, Width = 250, Height = 24 });
            var serverAddress = new TextBox { Text = c.Server, Left = 26, Top = 415, Width = 510 }; Controls.Add(serverAddress);
            Controls.Add(new Label { Text = "新服务器注册密钥（当前测试服务器可留空）", Left = 26, Top = 454, Width = 510, Height = 24, Font = new Font(Font.FontFamily, 9) });
            var registrationKey = new TextBox { Left = 26, Top = 483, Width = 510, UseSystemPasswordChar = true }; Controls.Add(registrationKey);
            var saveServer = new Button { Text = "保存并连接", Left = 26, Top = 530, Width = 150, Height = 38 }; Controls.Add(saveServer);
            Controls.Add(new Label { Text = "例如：192.168.100.101:19000 或 https://remote.example.com\n每个服务器地址分别保存设备 ID 与密码。\n关闭窗口后保留托盘；退出可停止远程控制。", Left = 26, Top = 579, Width = 520, Height = 65, Font = new Font(Font.FontFamily, 9) });
            saveServer.Click += async (s, e) => {
                if (!toggle.Enabled) return;
                string address, key = registrationKey.Text;
                try {
                    address = ServerSettings.Normalize(serverAddress.Text);
                    if (string.IsNullOrEmpty(key)) key = ServerSettings.Normalize(c.Server) == address && !string.IsNullOrEmpty(c.EnrollmentToken) ? c.EnrollmentToken : Registration.DefaultEnrollmentToken(address);
                    if (ServerSettings.NeedsEnrollment(c, address) && string.IsNullOrEmpty(key)) throw new ArgumentException("首次连接该服务器需要填写注册密钥");
                } catch (Exception ex) { status.Text = ex.Message; return; }
                saveServer.Enabled = false; toggle.Enabled = false; savePassword.Enabled = false;
                try {
                    await Stop(); ServerSettings.Switch(c, address, key); Program.SaveConfig(c);
                    serverAddress.Text = c.Server; deviceId.Text = c.DeviceId ?? "连接后自动分配"; password.Text = c.AccessPassword; registrationKey.Clear();
                    cancellation = new CancellationTokenSource(); worker = Task.Run(() => agent.Run(cancellation.Token)); toggle.Text = "停止连接";
                } catch (Exception ex) { status.Text = "保存服务器失败：" + ex.Message; }
                finally { saveServer.Enabled = true; toggle.Enabled = true; savePassword.Enabled = !string.IsNullOrEmpty(c.DeviceId); }
            };
            toggle.Click += async (s, e) => { if (worker == null && (string.IsNullOrEmpty(c.DeviceId) || serverAddress.Text != c.Server)) saveServer.PerformClick(); else await Toggle(); };
            var menu = new ContextMenuStrip(); menu.Items.Add("打开窗口", null, (s, e) => ShowWindow()); menu.Items.Add("启动 / 停止", null, async (s, e) => await Toggle());
            menu.Items.Add("退出", null, async (s, e) => { quitting = true; await Stop(); tray.Visible = false; Close(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "ToLink · 已停止", Visible = true, ContextMenuStrip = menu }; tray.DoubleClick += (s, e) => ShowWindow();
            agent = new RemoteAgent(c, this, message => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { status.Text = message; deviceId.Text = c.DeviceId ?? "启动后自动分配"; if (!string.IsNullOrEmpty(c.DeviceId) && toggle.Enabled) savePassword.Enabled = true; tray.Text = "ToLink · " + (message.Length > 45 ? message.Substring(0, 45) : message); })); });
            var showWait = ThreadPool.RegisterWaitForSingleObject(Program.ShowEvent, (s, expired) => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)ShowWindow); }, null, -1, false);
            FormClosed += (s, e) => showWait.Unregister(null);
            FormClosing += (s, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } else { agent.Stop(); tray.Dispose(); } };
            Shown += async (s, e) => { if (autoStart) Hide(); if (autoStart || !string.IsNullOrEmpty(c.DeviceId)) await Toggle(); else status.Text = "确认服务器地址后点击“保存并连接”"; };
        }
        void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        async Task Toggle() {
            if (!toggle.Enabled) return;
            toggle.Enabled = false;
            try {
                if (worker != null) await Stop();
                else { cancellation = new CancellationTokenSource(); worker = Task.Run(() => agent.Run(cancellation.Token)); toggle.Text = "停止连接"; }
            } finally { toggle.Enabled = true; }
        }
        async Task Stop() {
            if (worker == null) return;
            cancellation.Cancel(); agent.Stop();
            try { await worker; } catch (OperationCanceledException) { }
            worker = null; cancellation.Dispose(); cancellation = null; toggle.Text = "启动连接"; status.Text = "已停止"; tray.Text = "ToLink · 已停止";
        }
    }
    public sealed class RemoteAgent {
        readonly AgentConfig config; readonly Action<string> state; readonly InputController input = new InputController();
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1); readonly VideoEncoder encoder = new VideoEncoder();
        volatile string requestedCodec = "jpeg", preferredCodec = "jpeg"; int generation; volatile bool secureDesktop;
        volatile ClientWebSocket socket; volatile bool viewing; long lastHeartbeat;
        readonly Control clipboardOwner;
        public RemoteAgent(AgentConfig c, Control owner, Action<string> s) { config = c; clipboardOwner = owner; state = s; }
        public void Stop() { viewing = false; encoder.Stop(); var ws = socket; if (ws != null) ws.Abort(); ReleaseInput(); }
        public async Task Run(CancellationToken cancellation) {
            int delay = 1000;
            while (!cancellation.IsCancellationRequested) {
                using (var ws = new ClientWebSocket()) using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                    socket = ws; viewing = false;
                    try {
                        if (string.IsNullOrEmpty(config.DeviceId)) { state("正在注册设备 ID…"); Registration.EnsureRegistered(config); }
                        state("正在连接中转服务器…");
                        ws.Options.SetRequestHeader("Authorization", "Bearer " + config.Token); ws.Options.Proxy = null;
                        // Avoid the protocol-heartbeat path implicated by reproducible RSV errors on
                        // this test machine. Application heartbeats share the single send loop.
                        ws.Options.KeepAliveInterval = TimeSpan.Zero;
                        var uri = new Uri(config.Server.TrimEnd('/') + "/ws/agent?device=" + Uri.EscapeDataString(config.DeviceId));
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) { timeout.CancelAfter(10000); await ws.ConnectAsync(uri, timeout.Token); }
                        delay = 1000; Program.Log("relay connected"); state("已连接 · 等待浏览器");
                        Interlocked.Exchange(ref lastHeartbeat, DateTime.UtcNow.Ticks);
                        var receive = Receive(ws, connection.Token);
                        var publish = Publish(ws, connection.Token);
                        var watchdog = Watchdog(ws, connection.Token);
                        var finished = await Task.WhenAny(receive, publish, watchdog);
                        Program.Log((finished == receive ? "receive" : "publish") + " ended: " + finished.Status + "; socket=" + ws.State + (finished.Exception == null ? "" : "; " + finished.Exception.GetBaseException().Message));
                        connection.Cancel(); encoder.Stop(); ws.Abort();
                        try { await Task.WhenAll(receive, publish, watchdog); } catch (OperationCanceledException) { }
                    } catch (Exception ex) {
                        if (!cancellation.IsCancellationRequested) { Program.Log("connection: " + ex.ToString()); state("连接中断，自动重连中…"); }
                    } finally { connection.Cancel(); encoder.Stop(); ws.Abort(); viewing = false; ReleaseInput(); socket = null; }
                }
                if (!cancellation.IsCancellationRequested) { await Task.Delay(delay, cancellation); delay = Math.Min(15000, delay * 2); }
            }
        }
        async Task Receive(ClientWebSocket ws, CancellationToken cancellation) {
            var buffer = new byte[8192]; var serializer = new JavaScriptSerializer();
            using (var files = new IncomingFiles())
            while (ws.State == WebSocketState.Open && !cancellation.IsCancellationRequested) {
                using (var stream = new MemoryStream()) {
                    WebSocketReceiveResult result;
                    do {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                        if (result.MessageType == WebSocketMessageType.Close) { Program.Log("relay close: " + result.CloseStatus + " " + result.CloseStatusDescription); return; }
                        if (stream.Length + result.Count > (result.MessageType == WebSocketMessageType.Binary ? 65568 : 32768)) throw new IOException("Invalid relay command");
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if(result.MessageType == WebSocketMessageType.Binary) {
                        string id=files.Id, error=null;
                        try { if(!viewing) throw new IOException(); files.Append(stream.GetBuffer(), (int)stream.Length); }
                        catch(Exception ex) { error=ex is IOException ? ex.Message : "文件分块接收失败"; files.Dispose(); }
                        await SendJson(ws,new { type="filesAck",id=id,ok=error==null,received=files.Received,message=error },cancellation);
                        continue;
                    }
                    var m = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(stream.ToArray()));
                    var type = Convert.ToString(m["type"]);
                    if (type == "viewing") {
                        files.Dispose(); Interlocked.Increment(ref generation); encoder.Stop();
                        string codec = m.ContainsKey("codec") ? Convert.ToString(m["codec"]) : "jpeg"; requestedCodec = codec == "h265" || codec == "h264" ? codec : "jpeg"; preferredCodec = requestedCodec;
                        viewing = Convert.ToBoolean(m["active"]); Interlocked.Exchange(ref lastHeartbeat, DateTime.UtcNow.Ticks);
                        if (!viewing) ReleaseInput(); state(viewing ? "浏览器已连接 · 推流中" : "已连接 · 等待浏览器");
                    } else if (type == "heartbeat") Interlocked.Exchange(ref lastHeartbeat, DateTime.UtcNow.Ticks);
                    else if (type == "probe") await SendJson(ws, new { type = "probeAck", nonce = m["nonce"] }, cancellation);
                    else if (type == "release") ReleaseInput();
                    else if (viewing && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastHeartbeat) < TimeSpan.FromSeconds(20).Ticks) {
                        if (type == "wake") {
                            bool ok = true; string message = "已唤醒屏幕，请启用控制并输入系统密码";
                            try { DesktopBridge.Wake(); Interlocked.Increment(ref generation); encoder.Stop(); }
                            catch (Exception ex) { ok = false; message = ex.Message; }
                            await SendJson(ws, new { type = "wakeResult", ok = ok, message = message }, cancellation);
                        } else if (DesktopBridge.Secure) {
                            string error = null;
                            try {
                                if (type == "paste" || type.StartsWith("files", StringComparison.Ordinal)) {
                                    files.Dispose(); throw new IOException("锁屏界面请直接键入系统密码，解锁后可粘贴");
                                }
                                DesktopBridge.Apply(m);
                            } catch (Exception ex) { error = ex.Message; }
                            if (error != null) await SendJson(ws, new { type = "error", message = error }, cancellation);
                        } else if (type == "filesBegin" || type == "filesFinish" || type == "filesCancel") {
                            string id=Convert.ToString(m["id"]), error=null; bool pasted=false;
                            try {
                                if(type=="filesBegin") { files.Begin(m);pasted=files.StartStreaming(input); }
                                else if(files.Id!=id) throw new IOException("文件批次已结束");
                                else if(type=="filesCancel") files.Dispose();
                                else clipboardOwner.Invoke((Action)(() => { cancellation.ThrowIfCancellationRequested(); if(!viewing) throw new IOException("控制已断开"); pasted=files.Publish(input); }));
                            } catch(Exception ex) { error=ex.GetBaseException().Message; }
                            long received=files.Received;
                            if(type!="filesBegin" || error!=null) files.Dispose();
                            await SendJson(ws,new { type="filesAck",id=id,ok=error==null,received=received,pasted=pasted,message=error },cancellation);
                        } else if (type == "paste") {
                            string error = null;
                            try {
                                // WinForms clipboard operations run on the interactive STA thread.
                                clipboardOwner.Invoke((Action)(() => {
                                    if (viewing && !cancellation.IsCancellationRequested && ws.State == WebSocketState.Open)
                                        input.PasteClipboard(Convert.ToString(m["text"]));
                                }));
                            } catch (Exception ex) { error = ex.GetBaseException().Message; }
                            if (error != null) await SendJson(ws, new { type = "error", message = error }, cancellation);
                        } else input.Apply(m);
                        encoder.Wake();
                    }
                }
            }
        }
        async Task SendJson(ClientWebSocket ws, object value, CancellationToken cancellation) {
            var data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
            await Send(ws, data, WebSocketMessageType.Text, cancellation);
        }
        async Task Send(ClientWebSocket ws, byte[] data, WebSocketMessageType type, CancellationToken cancellation) {
            await sendGate.WaitAsync(cancellation);
            try { using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) { timeout.CancelAfter(5000); await ws.SendAsync(new ArraySegment<byte>(data), type, true, timeout.Token); } }
            finally { sendGate.Release(); }
        }
        void ReleaseInput() { input.Release(); if (secureDesktop || DesktopBridge.Secure) DesktopBridge.Release(); }
        async Task Watchdog(ClientWebSocket ws, CancellationToken cancellation) {
            int tick = 0;
            while (!cancellation.IsCancellationRequested && ws.State == WebSocketState.Open) {
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastHeartbeat) > TimeSpan.FromSeconds(20).Ticks) ReleaseInput();
                bool nextDesktop = DesktopBridge.Secure;
                if (nextDesktop != secureDesktop) {
                    ReleaseInput(); secureDesktop = nextDesktop; if (!nextDesktop) requestedCodec = preferredCodec; Interlocked.Increment(ref generation); encoder.Stop();
                    Program.Log("desktop changed: " + (nextDesktop ? "secure" : "user"));
                }
                if (tick++ % 5 == 0) await SendJson(ws, new { type = "agentHeartbeat" }, cancellation);
                await Task.Delay(1000, cancellation);
            }
        }
        async Task Publish(ClientWebSocket ws, CancellationToken cancellation) {
            Rectangle previous = Rectangle.Empty; long nextError = 0; byte[] lastJpeg = null; bool jpegIdle = false; int jpegGeneration = -1;
            var period = System.Diagnostics.Stopwatch.StartNew(); long captureMs = 0, sendMs = 0; int frameCount = 0;
            while (ws.State == WebSocketState.Open && !cancellation.IsCancellationRequested) {
                var loop = System.Diagnostics.Stopwatch.StartNew();
                var bounds = SystemInformation.VirtualScreen;
                if (bounds != previous) { await SendJson(ws, new { type = "hello", name = Environment.MachineName, nativePaste = true, filePaste = true, streamingFilePaste = true, wake = true, width = bounds.Width, height = bounds.Height }, cancellation); previous = bounds; input.Release(); }
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastHeartbeat) > TimeSpan.FromSeconds(20).Ticks) ReleaseInput();
                if (!viewing) { period.Restart(); captureMs = 0; sendMs = 0; frameCount = 0; await Task.Delay(300, cancellation); continue; }
                if (requestedCodec != "jpeg" && !secureDesktop) {
                    int current = generation; string codec = requestedCodec; bool failed = false;
                    try { await encoder.Run(config, codec, () => viewing && generation == current, value => SendJson(ws, value, cancellation), data => Send(ws, data, WebSocketMessageType.Binary, cancellation), cancellation); }
                    catch (Exception ex) { if (!cancellation.IsCancellationRequested && viewing && generation == current) { failed = true; Program.Log("encoder: " + ex.Message); } }
                    if (failed && DesktopBridge.Secure) {
                        // Locking can invalidate GDI before the watchdog sees the desktop change.
                        // Preserve the requested codec instead of treating this as a hardware failure.
                        secureDesktop = true; Interlocked.Increment(ref generation); continue;
                    }
                    if (failed) {
                        requestedCodec = codec == "h265" ? "h264" : "jpeg";
                        await SendJson(ws, new { type = "error", message = "硬件编码暂不可用，正在尝试 " + requestedCodec + " 模式。" }, cancellation);
                    }
                    continue;
                }
                if (jpegGeneration != generation) { jpegGeneration = generation; lastJpeg = null; await SendJson(ws, new { type = "stream", format = "jpeg", codec = "jpeg" }, cancellation); }
                byte[] frame = null; bool reportError = false; string captureError = null;
                var timing = System.Diagnostics.Stopwatch.StartNew();
                try { frame = secureDesktop ? DesktopBridge.Request(new { command = "capture" }) : CaptureDesktop(bounds, config); captureMs += timing.ElapsedMilliseconds; }
                catch (Exception ex) {
                    if (DateTime.UtcNow.Ticks > nextError) { Program.Log("capture: " + ex.Message); reportError = true; captureError = ex.Message; nextError = DateTime.UtcNow.AddSeconds(10).Ticks; }
                }
                if (reportError) await SendJson(ws, new { type = "error", message = captureError ?? "无法采集桌面，请检查锁屏支持服务" }, cancellation);
                if (frame != null) {
                    bool same = VideoEncoder.EqualPixels(frame, lastJpeg);
                    if (same != jpegIdle) { jpegIdle = same; await SendJson(ws, new { type = "idle", idle = same }, cancellation); }
                    if (!same) { timing.Restart(); await Send(ws, frame, WebSocketMessageType.Binary, cancellation); sendMs += timing.ElapsedMilliseconds; frameCount++; lastJpeg = frame; }
                }
                if (period.ElapsedMilliseconds >= 10000) { Program.Log("stream frames=" + frameCount + " elapsedMs=" + period.ElapsedMilliseconds + " captureMs=" + captureMs + " sendMs=" + sendMs); period.Restart(); captureMs = 0; sendMs = 0; frameCount = 0; }
                await Task.Delay(Math.Max(1, 1000 / config.Fps - (int)loop.ElapsedMilliseconds), cancellation);
            }
        }
        internal static byte[] CaptureDesktop(Rectangle bounds, AgentConfig config) {
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 64000000) throw new IOException("Unsupported desktop size");
            using (var original = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb)) {
                using (var g = Graphics.FromImage(original)) { g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy); NativeCursor.Draw(g, bounds); }
                int width = Math.Min(config.MaxWidth, bounds.Width), height = Math.Max(1, (int)((long)bounds.Height * width / bounds.Width));
                using (var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb)) using (var g = Graphics.FromImage(resized)) using (var stream = new MemoryStream()) using (var options = new EncoderParameters(1)) {
                    g.DrawImage(original, new Rectangle(0, 0, width, height));
                    options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)config.Quality);
                    ImageCodecInfo codec = null; foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") codec = c;
                    resized.Save(stream, codec, options); return stream.ToArray();
                }
            }
        }
    }
    internal static class NativeCursor {
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr cursor; public Point position; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool icon; public int xHotspot, yHotspot; public IntPtr mask, color; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
        public sealed class Snapshot {
            public string type { get { return "cursor"; } }
            public double x { get; set; } public double y { get; set; }
            public bool visible { get; set; } public string shape { get; set; }
            public bool Same(Snapshot other) { return x == other.x && y == other.y && visible == other.visible && shape == other.shape; }
        }
        static readonly Dictionary<IntPtr, string> Shapes = new Dictionary<IntPtr, string> {
            { Cursors.Arrow.Handle, "default" }, { Cursors.IBeam.Handle, "text" }, { Cursors.Hand.Handle, "pointer" },
            { Cursors.WaitCursor.Handle, "wait" }, { Cursors.AppStarting.Handle, "progress" }, { Cursors.Cross.Handle, "crosshair" },
            { Cursors.SizeWE.Handle, "ew-resize" }, { Cursors.SizeNS.Handle, "ns-resize" }, { Cursors.SizeAll.Handle, "move" },
            { Cursors.SizeNESW.Handle, "nesw-resize" }, { Cursors.SizeNWSE.Handle, "nwse-resize" }, { Cursors.No.Handle, "not-allowed" }
        };
        public static Snapshot Read(Rectangle bounds) {
            var ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(ci); string shape;
            bool found = GetCursorInfo(ref ci);
            if (!Shapes.TryGetValue(ci.cursor, out shape)) shape = "default";
            return new Snapshot { x = Math.Max(0, Math.Min(1, (double)(ci.position.X - bounds.X) / Math.Max(1, bounds.Width - 1))),
                y = Math.Max(0, Math.Min(1, (double)(ci.position.Y - bounds.Y) / Math.Max(1, bounds.Height - 1))), visible = found && ci.flags == 1 && bounds.Contains(ci.position), shape = shape };
        }
        public static void Draw(Graphics g, Rectangle bounds) {
            var ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(ci);
            if (!GetCursorInfo(ref ci) || ci.flags != 1) return;
            ICONINFO ii; if (!GetIconInfo(ci.cursor, out ii)) return;
            try { var dc = g.GetHdc(); try { DrawIconEx(dc, ci.position.X - bounds.X - ii.xHotspot, ci.position.Y - bounds.Y - ii.yHotspot, ci.cursor, 0, 0, 0, IntPtr.Zero, 3); } finally { g.ReleaseHdc(dc); } }
            finally { if (ii.mask != IntPtr.Zero) DeleteObject(ii.mask); if (ii.color != IntPtr.Zero) DeleteObject(ii.color); }
        }
    }
    public sealed class InputController {
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION data; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT keyboard; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        readonly object gate = new object(); readonly HashSet<ushort> keys = new HashSet<ushort>(); readonly HashSet<int> buttons = new HashSet<int>();
        static bool Extended(ushort vk) { return (vk >= 33 && vk <= 46) || vk == 91 || vk == 92 || vk == 163 || vk == 165 || vk == 111; }
        static void Emit(INPUT input) { if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) != 1) throw new IOException("SendInput failed (" + Marshal.GetLastWin32Error() + ")"); }
        static INPUT KeyEvent(ushort vk, bool down) {
            return new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { vk = vk, flags = (down ? 0u : 2u) | (Extended(vk) ? 1u : 0u) } } };
        }
        static void Key(ushort vk, bool down) { Emit(KeyEvent(vk, down)); }
        public void PasteClipboard(string text) {
            if (string.IsNullOrEmpty(text) || text.Length > 1000 || text.IndexOf('\0') >= 0)
                throw new ArgumentException("剪贴板文本无效或超过 1000 字符");
            lock (gate) {
                Release();
                var value = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
                var data = new DataObject();
                data.SetText(value, TextDataFormat.UnicodeText);
                IntPtr target = Foreground();
                WriteClipboard(data);
                if(target == IntPtr.Zero || target != Foreground()) throw new IOException("文本已写入 Windows 剪贴板，请在目标窗口粘贴");
                PasteShortcut();
            }
        }
        [DllImport("user32.dll", EntryPoint="GetForegroundWindow")] internal static extern IntPtr Foreground();
        internal static void WriteClipboard(DataObject data) {
            try { Clipboard.SetDataObject(data, true, 5, 40); }
            catch (ExternalException) { throw new IOException("Windows 剪贴板被占用，请重试"); }
        }
        internal static void PasteShortcut() {
                var chord = new[] { KeyEvent(0x11, true), KeyEvent(0x56, true), KeyEvent(0x56, false), KeyEvent(0x11, false) };
                uint inserted = SendInput(4, chord, Marshal.SizeOf(typeof(INPUT)));
                if (inserted != 4) {
                    if (inserted > 0) { try { Key(0x56, false); Key(0x11, false); } catch (IOException) { } }
                    throw new IOException("内容已写入 Windows 剪贴板，请在目标窗口粘贴");
                }
        }
        static void Mouse(uint flags, uint data, int x, int y) { Emit(new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { dx = x, dy = y, flags = flags, mouseData = data } } }); }
        static uint ButtonFlag(int button, bool down) { return button == 0 ? (down ? 2u : 4u) : button == 2 ? (down ? 8u : 16u) : (down ? 32u : 64u); }
        void Move(Dictionary<string, object> m) {
            double x = Convert.ToDouble(m["x"]), y = Convert.ToDouble(m["y"]);
            if (double.IsNaN(x) || double.IsNaN(y) || x < 0 || x > 1 || y < 0 || y > 1) throw new IOException("Invalid coordinates");
            Mouse(0xC001, 0, (int)Math.Round(x * 65535), (int)Math.Round(y * 65535));
        }
        public void Apply(Dictionary<string, object> m) {
            lock (gate) {
                var type = Convert.ToString(m["type"]);
                if (type == "move") Move(m);
                else if (type == "button") {
                    int button = Convert.ToInt32(m["button"]); bool down = Convert.ToBoolean(m["down"]);
                    if (button < 0 || button > 2) return; Move(m); Mouse(ButtonFlag(button, down), 0, 0, 0); if (down) buttons.Add(button); else buttons.Remove(button);
                } else if (type == "wheel") { int delta = Convert.ToInt32(m["delta"]); if (Math.Abs((long)delta) <= 1200) Mouse(0x0800, unchecked((uint)delta), 0, 0); }
                else if (type == "key") {
                    int value = Convert.ToInt32(m["vk"]); if (value < 8 || value > 254) return;
                    ushort vk = (ushort)value; bool down = Convert.ToBoolean(m["down"]); Key(vk, down); if (down) keys.Add(vk); else keys.Remove(vk);
                } else if (type == "text") {
                    string text = Convert.ToString(m["text"]); if (text.Length > 1000) return; Release();
                    // Unicode injection avoids clipboard changes and dependence on the active IME.
                    foreach (char c in text) { Emit(new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { scan = c, flags = 4 } } }); Emit(new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { scan = c, flags = 6 } } }); }
                }
            }
        }
        public void Release() {
            lock (gate) {
                foreach (ushort key in keys) try { Key(key, false); } catch { }
                foreach (int button in buttons) try { Mouse(ButtonFlag(button, false), 0, 0, 0); } catch { }
                keys.Clear(); buttons.Clear();
            }
        }
    }
    // An isolated test window provides observable input evidence without editing user documents.
    public sealed class DiagnosticForm : Form {
        readonly TextBox box; readonly Button button; int clicks, wheels, keyUps;
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 40 };
        public DiagnosticForm() {
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); PersistState(); };
            FormClosed += (s, e) => { saveTimer.Stop(); saveTimer.Dispose(); };
            Text = "ToLink 验收窗口"; ClientSize = new Size(640, 330); StartPosition = FormStartPosition.CenterScreen; TopMost = true; WindowState = FormWindowState.Maximized; BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 13);
            Controls.Add(new Label { Text = "REQ-REMOTE-20261105-01 · 键鼠与中文输入验收", Left = 20, Top = 20, Width = 600, Height = 35 });
            box = new TextBox { Left = 25, Top = 80, Width = 580, Height = 110, Multiline = true }; Controls.Add(box);
            button = new Button { Left = 25, Top = 215, Width = 220, Height = 50, Text = "点击计数：0" }; Controls.Add(button);
            Controls.Add(new Label { Text = "清晰度样本 ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789\n中文文字：远程桌面 · 原生分辨率 · 静止画面不持续推流", Left = 25, Top = 300, Width = 1050, Height = 80, Font = new Font("Microsoft YaHei UI", 11), ForeColor = Color.Black });
            var motion = new CheckBox { Text = "动态画面测试", Left = 25, Top = 395, Width = 240, Height = 35 }; Controls.Add(motion);
            var block = new Panel { Left = 25, Top = 455, Width = 80, Height = 80, BackColor = Color.FromArgb(35, 140, 100) }; Controls.Add(block);
            int step = 0; var timer = new System.Windows.Forms.Timer { Interval = 50 }; timer.Tick += (s, e) => { step = (step + 9) % Math.Max(100, ClientSize.Width - 110); block.Left = 25 + step; };
            motion.CheckedChanged += (s, e) => { timer.Enabled = motion.Checked; }; FormClosed += (s, e) => timer.Dispose();
            button.Click += (s, e) => { clicks++; button.Text = "点击计数：" + clicks; Save(); };
            box.TextChanged += (s, e) => Save(); box.KeyUp += (s, e) => { keyUps++; Save(); }; box.MouseWheel += (s, e) => { wheels += e.Delta; Save(); };
            Shown += (s, e) => { button.Focus(); Save(); }; Move += (s, e) => { if (IsHandleCreated && box != null) Save(); };
        }
        void Save() {
            saveTimer.Stop(); saveTimer.Start();
        }
        void PersistState() {
            var p = box.PointToScreen(new Point(50, 30)); var b = button.PointToScreen(new Point(70, 20));
            File.WriteAllText(Path.Combine(Program.BaseDirectory, "smoke-state.json"), new JavaScriptSerializer().Serialize(new { text = box.Text, clicks = clicks, wheel = wheels, keyUps = keyUps, textX = p.X, textY = p.Y, buttonX = b.X, buttonY = b.Y, pid = System.Diagnostics.Process.GetCurrentProcess().Id }), new UTF8Encoding(false, false));
        }
    }
}
