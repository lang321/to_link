// REQ-REMOTE-20261110-01: bounded, local-only IPC; the relay never receives a privileged command API.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ToLink {
    internal static class DesktopBridge {
        [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint process);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public static string PipeName { get { return "ToLink.Desktop." + Process.GetCurrentProcess().SessionId; } }
        public static string DesktopName(IntPtr desktop) {
            var name = new StringBuilder(256); int needed;
            if (!GetUserObjectInformation(desktop, 2, name, 512, out needed)) throw new IOException("无法读取输入桌面");
            return name.ToString();
        }
        public static bool Secure {
            get {
                var desktop = OpenInputDesktop(0, false, 1);
                if (desktop == IntPtr.Zero) return true;
                try { return !string.Equals(DesktopName(desktop), "Default", StringComparison.OrdinalIgnoreCase); }
                finally { CloseDesktop(desktop); }
            }
        }
        public static void WakeDisplay() {
            // One-shot activity: preserve the user's display sleep policy after the remote action.
            SetThreadExecutionState(2);
            IntPtr result;
            SendMessageTimeout(new IntPtr(0xffff), 0x112, new IntPtr(0xf170), new IntPtr(-1), 2, 500, out result);
        }
        public static byte[] Request(object request) {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification)) {
                try { pipe.Connect(500); }
                catch (TimeoutException) { throw new IOException("请先安装 Windows 锁屏支持服务（安装锁屏支持.cmd）"); }
                // Refuse a same-name pipe created by another local process before sending keystrokes.
                uint server; if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out server)) throw new IOException("无法验证锁屏服务");
                var process = OpenProcess(0x1000, false, server);
                if (process == IntPtr.Zero) throw new IOException("无法验证锁屏服务进程");
                try {
                    var path = new StringBuilder(1024); int length = path.Capacity;
                    var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ToLinkDesktop", "ToLink.DesktopHost.exe");
                    if (!QueryFullProcessImageName(process, 0, path, ref length) || !string.Equals(path.ToString(), expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("锁屏服务来源无效，请重新安装");
                } finally { CloseHandle(process); }
                // A wedged helper must not hang the agent's network/stop loops.
                using (var timeout = new Timer(_ => { try { pipe.Dispose(); } catch { } }, null, 4000, Timeout.Infinite)) {
                    var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(request));
                    using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true)) { writer.Write(bytes.Length); writer.Write(bytes); writer.Flush(); }
                    using (var reader = new BinaryReader(pipe, Encoding.UTF8, true)) {
                        bool ok = reader.ReadBoolean(); int length = reader.ReadInt32();
                        if (length < 0 || length > 8 * 1024 * 1024) throw new IOException("锁屏服务响应无效");
                        var reply = reader.ReadBytes(length);
                        if (reply.Length != length) throw new IOException("锁屏服务连接中断");
                        if (!ok) throw new IOException(Encoding.UTF8.GetString(reply));
                        return reply;
                    }
                }
            }
        }
        public static void Apply(Dictionary<string, object> message) { Request(new { command = "input", input = message }); }
        public static void Release() { try { Request(new { command = "release" }); } catch { } }
        public static void Wake() {
            if (Secure) Request(new { command = "wake" }); else WakeDisplay();
        }
    }
}
