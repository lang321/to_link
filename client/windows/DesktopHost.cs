// REQ-REMOTE-20261110-01: LocalSystem broker restricted to the installed owner and that owner's console session.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ToLink {
    internal sealed class DesktopHost : ServiceBase {
        readonly string owner; Thread worker; volatile bool stopping; Process child;
        DesktopHost(string sid) { owner = new SecurityIdentifier(sid).Value; ServiceName = "ToLinkDesktop"; }
        static void Main(string[] args) {
            if (!WindowsIdentity.GetCurrent().IsSystem || args.Length != 2) return;
            if (args[0] == "--helper") { Serve(new SecurityIdentifier(args[1])); return; }
            if (args[0] == "--service") ServiceBase.Run(new DesktopHost(args[1]));
        }
        protected override void OnStart(string[] args) { worker = new Thread(Supervise) { IsBackground=true }; worker.Start(); }
        protected override void OnStop() { stopping = true; if (worker != null) worker.Join(6000); StopChild(); }
        void StopChild() { if (child != null) { try { if (!child.HasExited) child.Kill(); child.WaitForExit(2000); } catch { } child.Dispose(); child = null; } }
        void Supervise() {
            uint runningSession = uint.MaxValue;
            while (!stopping) {
                try {
                    uint session = WTSGetActiveConsoleSessionId();
                    bool accepted = session != uint.MaxValue && OwnerOfSession(session) == owner;
                    if (!accepted || runningSession != session || (child != null && child.HasExited)) StopChild();
                    if (accepted && child == null) { child = Launch(session, owner); runningSession = session; }
                } catch (Exception ex) { Program.Log("[LockScreen] service: " + ex.Message); StopChild(); }
                for (int i=0; i<10 && !stopping; i++) Thread.Sleep(100);
            }
            StopChild();
        }
        static string OwnerOfSession(uint session) {
            IntPtr token;
            if (!WTSQueryUserToken(session, out token)) return null;
            try { using (var identity = new WindowsIdentity(token)) return identity.User.Value; }
            finally { CloseHandle(token); }
        }
        static Process Launch(uint session, string owner) {
            IntPtr current = IntPtr.Zero, token = IntPtr.Zero;
            try {
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0002 | 0x0008, out current) ||
                    !DuplicateTokenEx(current, 0x02000000, IntPtr.Zero, 2, 1, out token) ||
                    !SetTokenInformation(token, 12, ref session, 4)) throw new IOException("Unable to prepare console token: " + Marshal.GetLastWin32Error());
                var startup = new STARTUPINFO { cb=Marshal.SizeOf(typeof(STARTUPINFO)), desktop="winsta0\\default" };
                PROCESS_INFORMATION info;
                string path = typeof(DesktopHost).Assembly.Location;
                if (!CreateProcessAsUser(token, path, new StringBuilder("\""+path+"\" --helper "+owner), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, Path.GetDirectoryName(path), ref startup, out info))
                    throw new IOException("Unable to start console helper: " + Marshal.GetLastWin32Error());
                try { return Process.GetProcessById((int)info.processId); }
                finally { CloseHandle(info.process); CloseHandle(info.thread); }
            } finally { if(token!=IntPtr.Zero)CloseHandle(token); if(current!=IntPtr.Zero)CloseHandle(current); }
        }
        static void Serve(SecurityIdentifier owner) {
            // Permit the installing user to verify our executable path, without granting
            // terminate, memory-write, token, or process-creation rights.
            IntPtr descriptor; uint descriptorSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x1000;;;"+owner.Value+")",1,out descriptor,out descriptorSize)) return;
            try { if (!SetKernelObjectSecurity(GetCurrentProcess(),4,descriptor)) return; }
            finally { LocalFree(descriptor); }
            var input = new InputController(); DateTime lastInput = DateTime.MinValue;
            var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(owner,PipeAccessRights.ReadWrite,AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
            // Release input even if the ordinary agent dies without sending a final release.
            using (var watchdog = new System.Threading.Timer(_ => {
                if ((DateTime.UtcNow-lastInput).TotalSeconds > 20) { try { OnDesktop(() => { input.Release(); return new byte[0]; }); } catch { } }
            }, null, 1000, 1000))
            while (true) {
                using (var pipe = new NamedPipeServerStream(DesktopBridge.PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.None,4096,4096,security)) {
                    pipe.WaitForConnection();
                    using (var timeout = new System.Threading.Timer(_ => { try { pipe.Dispose(); } catch { } },null,4000,Timeout.Infinite)) {
                        try {
                            uint clientId;
                            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out clientId)) throw new IOException("Invalid caller");
                            using (var client=Process.GetProcessById((int)clientId)) if (client.SessionId != Process.GetCurrentProcess().SessionId) throw new IOException("Wrong session");
                            string sid=null; pipe.RunAsClient(() => { using(var identity=WindowsIdentity.GetCurrent()) sid=identity.User.Value; });
                            if(sid!=owner.Value || OwnerOfSession((uint)Process.GetCurrentProcess().SessionId)!=owner.Value) throw new IOException("Wrong owner");
                            var reader = new BinaryReader(pipe,Encoding.UTF8,true); int size=reader.ReadInt32();
                            if(size<2 || size>16384) throw new IOException("Invalid request size");
                            var bytes=reader.ReadBytes(size); if(bytes.Length!=size) throw new IOException("Incomplete request");
                            var request=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(bytes));
                            byte[] reply;
                            try {
                                reply=OnDesktop(() => {
                                    string command=Convert.ToString(request["command"]);
                                    if(command=="wake") { input.Release(); DesktopBridge.WakeDisplay(); }
                                    else if(command=="release") input.Release();
                                    else if(command=="capture") {
                                        var bounds=SystemInformation.VirtualScreen;
                                        return RemoteAgent.CaptureDesktop(bounds,new AgentConfig { MaxWidth=1920, Quality=70 });
                                    } else if(command=="input") {
                                        var message=request["input"] as Dictionary<string,object>;
                                        string type=message==null ? "" : Convert.ToString(message["type"]);
                                        if(type!="move" && type!="button" && type!="key" && type!="wheel" && type!="text") throw new IOException("Unsupported input");
                                        input.Apply(message); lastInput=DateTime.UtcNow;
                                    } else throw new IOException("Unsupported command");
                                    return new byte[0];
                                });
                                Reply(pipe,true,reply);
                            } catch { Reply(pipe,false,Encoding.UTF8.GetBytes("锁屏桌面暂不可用，请稍后重试")); }
                        } catch { /* Malformed or abandoned local request: close only this connection. */ }
                    }
                }
            }
        }
        static readonly object desktopGate = new object();
        static byte[] OnDesktop(Func<byte[]> action) {
            // SetThreadDesktop cannot switch a thread owning windows/hooks. Use a fresh thread
            // and bind BEFORE any GDI/WinForms call; never move a shared thread-pool thread.
            lock(desktopGate) {
                byte[] result=null; Exception failure=null; IntPtr desktop=IntPtr.Zero;
                var thread=new Thread(() => {
                    desktop=DesktopBridge.OpenInputDesktop(0,false,0x01ff);
                    if(desktop==IntPtr.Zero) { failure=new IOException("Input desktop unavailable"); return; }
                    try {
                        if(!SetThreadDesktop(desktop)) throw new IOException("Unable to bind input desktop");
                        result=action();
                    } catch(Exception ex) { failure=ex; }
                });
                thread.IsBackground=true; thread.Start(); thread.Join();
                if(desktop!=IntPtr.Zero) DesktopBridge.CloseDesktop(desktop);
                if(failure!=null) throw failure;
                return result;
            }
        }
        static void Reply(Stream pipe,bool ok,byte[] data) { if(data.Length>8*1024*1024)throw new IOException("Frame too large"); var writer=new BinaryWriter(pipe,Encoding.UTF8,true); writer.Write(ok); writer.Write(data.Length); writer.Write(data); writer.Flush(); }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct STARTUPINFO { public int cb; public string reserved,desktop,title; public int x,y,width,height,xChars,yChars,fill,flags; public short show,reservedSize; public IntPtr reservedPointer,input,output,error; }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process,thread; public uint processId,threadId; }
        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll",SetLastError=true)] static extern bool WTSQueryUserToken(uint session,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr token,uint access,IntPtr attributes,int level,int type,out IntPtr duplicate);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetTokenInformation(IntPtr token,int type,ref uint value,int length);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessAsUser(IntPtr token,string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref STARTUPINFO startup,out PROCESS_INFORMATION process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out IntPtr descriptor,out uint size);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetKernelObjectSecurity(IntPtr handle,uint information,IntPtr descriptor);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint process);
        [DllImport("user32.dll",SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktop);
    }
}
