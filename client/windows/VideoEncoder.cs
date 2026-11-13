// REQ-REMOTE-20261105-01: hardware video encoding, with change detection before the encoder.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ToLink {
    public sealed class VideoEncoder {
        readonly object gate = new object(); Process process;
        readonly AutoResetEvent wake = new AutoResetEvent(false); long activeUntil;
        public void Wake() { Interlocked.Exchange(ref activeUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2); wake.Set(); }
        [DllImport("msvcrt.dll", EntryPoint = "memcmp", CallingConvention = CallingConvention.Cdecl)] static extern int CompareMemory(IntPtr a, IntPtr b, UIntPtr count);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] static extern int memcmp(byte[] a, byte[] b, UIntPtr count);
        public static bool EqualPixels(byte[] a, byte[] b) { return a != null && b != null && a.Length == b.Length && memcmp(a, b, (UIntPtr)a.Length) == 0; }
        // A top-down DIB exposes stable native pixels, avoiding GDI+ LockBits staging copies.
        sealed class DesktopPixels : IDisposable {
            [StructLayout(LayoutKind.Sequential)] struct Info { public uint size; public int width, height; public ushort planes, bits; public uint compression, imageSize; public int xppm, yppm; public uint used, important; }
            [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
            [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref Info info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
            readonly Rectangle bounds; IntPtr screen, memory, bitmap, previous; public IntPtr Pixels;
            public DesktopPixels(Rectangle area) {
                bounds = area;
                try {
                    screen = GetDC(IntPtr.Zero); memory = CreateCompatibleDC(screen);
                    var info = new Info { size = 40, width = area.Width, height = -area.Height, planes = 1, bits = 32 };
                    bitmap = CreateDIBSection(screen, ref info, 0, out Pixels, IntPtr.Zero, 0);
                    if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero || Pixels == IntPtr.Zero) throw new IOException("Unable to allocate desktop capture buffer");
                    previous = SelectObject(memory, bitmap);
                    if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new IOException("Unable to select capture buffer");
                } catch { Dispose(); throw; }
            }
            public void Capture() { if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x00CC0020)) throw new IOException("Desktop capture failed: " + Marshal.GetLastWin32Error()); }
            public void Dispose() {
                if (memory != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (memory != IntPtr.Zero) DeleteDC(memory); if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                bitmap = memory = screen = previous = Pixels = IntPtr.Zero;
            }
        }
        public void Stop() { lock (gate) { if (process != null) try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } } }
        public async Task Run(AgentConfig config, string codec, Func<bool> active, Func<object, Task> sendJson, Func<byte[], Task> sendBinary, CancellationToken cancellation) {
            var bounds = SystemInformation.VirtualScreen;
            int width = Math.Min(1920, bounds.Width) & ~1, height = Math.Max(2, (int)((long)bounds.Height * width / bounds.Width)) & ~1;
            int fps = 20;
            string encoder = codec == "h265" ? "hevc_qsv" : "h264_qsv";
            string mime = codec == "h265" ? "video/mp4; codecs=\"hvc1.1.6.L120.B0\"" : "video/mp4; codecs=\"avc1.42E028\"";
            string args = "-hide_banner -loglevel warning -nostdin -filter_threads 1 -probesize 32 -analyzeduration 0 -f rawvideo -pix_fmt bgr0 -video_size " + width + "x" + height + " -framerate " + fps + " -i pipe:0 -an -vf format=nv12 -c:v " + encoder + " -preset veryfast -global_quality 20 -g 60 -bf 0 -async_depth 1 -low_power 0 -profile:v " + (codec == "h265" ? "main" : "baseline") + " -level:v 40 -tag:v " + (codec == "h265" ? "hvc1" : "avc1") + " -movflags empty_moov+default_base_moof+frag_every_frame -flush_packets 1 -f mp4 pipe:1";
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var child = new Process()) {
                child.StartInfo = new ProcessStartInfo(Path.Combine(Program.BaseDirectory, "ffmpeg.exe"), args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                var error = new StringBuilder(); child.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (error) { if (error.Length < 4096) error.AppendLine(e.Data); } };
                child.Start(); child.BeginErrorReadLine(); lock (gate) process = child;
                using (stop.Token.Register(Stop)) {
                    var producer = Task.Run(() => Produce(child.StandardInput.BaseStream, bounds, width, height, fps, active, sendJson, stop.Token));
                    var cursor = CursorLoop(bounds, active, sendJson, stop.Token);
                    var output = ReadVideo(child.StandardOutput.BaseStream, codec, mime, width, height, active, sendJson, sendBinary, stop.Token);
                    Exception failure = null;
                    await Task.WhenAny(producer, output, cursor);
                    stop.Cancel(); Stop();
                    try { await Task.WhenAll(producer, output, cursor); } catch (Exception ex) { failure = ex; }
                    finally { lock (gate) if (process == child) process = null; }
                    if (active() && !cancellation.IsCancellationRequested) {
                        string detail; lock (error) detail = error.ToString();
                        throw new IOException("Video encoder stopped: " + detail + (failure == null ? "" : failure.Message));
                    }
                }
            }
        }
        static async Task CursorLoop(Rectangle bounds, Func<bool> active, Func<object, Task> sendJson, CancellationToken cancellation) {
            NativeCursor.Snapshot previous = null;
            while (active() && !cancellation.IsCancellationRequested) {
                var current = NativeCursor.Read(bounds);
                if (previous == null || !current.Same(previous)) { await sendJson(current); previous = current; }
                await Task.Delay(50, cancellation);
            }
        }
        async Task Produce(Stream input, Rectangle bounds, int width, int height, int fps, Func<bool> active, Func<object, Task> sendJson, CancellationToken cancellation) {
            bool nativeSize = width == bounds.Width && height == bounds.Height;
            using (var direct = nativeSize ? new DesktopPixels(bounds) : null)
            using (var original = nativeSize ? null : new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb))
            using (var capture = nativeSize ? null : Graphics.FromImage(original))
            using (var scaled = nativeSize ? null : new Bitmap(width, height, PixelFormat.Format32bppRgb))
            using (var resize = nativeSize ? null : Graphics.FromImage(scaled)) {
                // Pin one reusable frame. Unchanged pixels are compared in place, with no managed copy.
                byte[] pixels = new byte[width * height * 4]; var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                bool haveFrame = false, idle = false; int settle = 0; var changedAt = Stopwatch.StartNew();
                var waits = new WaitHandle[] { wake, cancellation.WaitHandle };
                try {
                    while (active() && !cancellation.IsCancellationRequested) {
                        var time = Stopwatch.StartNew();
                        bool changed;
                        if (nativeSize) {
                            direct.Capture(); changed = CopyChanged(direct.Pixels, pinned, pixels, haveFrame);
                        } else {
                            capture.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                            resize.DrawImage(original, new Rectangle(0, 0, width, height));
                            var data = scaled.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                            try { if (data.Stride != width * 4) throw new IOException("Unexpected capture stride"); changed = CopyChanged(data.Scan0, pinned, pixels, haveFrame); }
                            finally { scaled.UnlockBits(data); }
                        }
                        if (changed) {
                            haveFrame = true; changedAt.Restart(); settle = 2;
                            if (idle) { idle = false; await sendJson(new { type = "idle", idle = false }); }
                        } else if (!idle && changedAt.ElapsedMilliseconds > 500) { idle = true; await sendJson(new { type = "idle", idle = true }); }
                        // Drain the last changed image; never discard reference frames after encoding.
                        if (changed || settle > 0) { await input.WriteAsync(pixels, 0, pixels.Length, cancellation); if (!changed) settle--; }
                        bool quiet = settle == 0 && changedAt.ElapsedMilliseconds > 1000 && Stopwatch.GetTimestamp() > Interlocked.Read(ref activeUntil);
                        int interval = quiet ? 200 : 1000 / fps;
                        // Remote input wakes capture immediately; external changes are checked at 5 Hz while idle.
                        int delay = Math.Max(1, interval - (int)time.ElapsedMilliseconds);
                        if (quiet) { if (WaitHandle.WaitAny(waits, delay) == 1) break; }
                        else { wake.Reset(); await Task.Delay(delay, cancellation); }
                    }
                } finally { pinned.Free(); }
            }
        }
        static bool CopyChanged(IntPtr source, GCHandle pinned, byte[] pixels, bool haveFrame) {
            bool changed = !haveFrame || CompareMemory(source, pinned.AddrOfPinnedObject(), (UIntPtr)pixels.Length) != 0;
            if (changed) Marshal.Copy(source, pixels, 0, pixels.Length); return changed;
        }
        static async Task<byte[]> ReadBox(Stream source, CancellationToken cancellation) {
            var header = new byte[8]; int offset = 0;
            while (offset < 8) { int n = await source.ReadAsync(header, offset, 8 - offset, cancellation); if (n == 0) return null; offset += n; }
            long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
            if (size < 8 || size > 4 * 1024 * 1024) throw new IOException("Invalid MP4 box size");
            var box = new byte[(int)size]; Buffer.BlockCopy(header, 0, box, 0, 8); offset = 8;
            while (offset < box.Length) { int n = await source.ReadAsync(box, offset, box.Length - offset, cancellation); if (n == 0) throw new EndOfStreamException(); offset += n; }
            return box;
        }
        static async Task ReadVideo(Stream source, string codec, string mime, int width, int height, Func<bool> active, Func<object, Task> sendJson, Func<byte[], Task> sendBinary, CancellationToken cancellation) {
            using (var fragment = new MemoryStream()) {
                bool announced = false; int frames = 0; long bytes = 0; var period = Stopwatch.StartNew();
                while (active() && !cancellation.IsCancellationRequested) {
                    var box = await ReadBox(source, cancellation); if (box == null) return;
                    string type = Encoding.ASCII.GetString(box, 4, 4); fragment.Write(box, 0, box.Length);
                    if (fragment.Length > 4 * 1024 * 1024) throw new IOException("Video fragment too large");
                    if (type != "moov" && type != "mdat") continue;
                    if (!announced) { await sendJson(new { type = "stream", format = "mp4", codec = codec, mime = mime, width = width, height = height }); announced = true; }
                    var payload = fragment.ToArray(); fragment.SetLength(0);
                    await sendBinary(payload); if (type == "mdat") frames++; bytes += payload.Length;
                    if (period.ElapsedMilliseconds >= 10000) { Program.Log("video codec=" + codec + " frames=" + frames + " bytes=" + bytes + " elapsedMs=" + period.ElapsedMilliseconds); period.Restart(); frames = 0; bytes = 0; }
                }
            }
        }
    }
}
