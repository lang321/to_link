// REQ-REMOTE-20261111-01: isolated native Explorer proof; preserve supported clipboard formats.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Automation;
using ComDataObject=System.Runtime.InteropServices.ComTypes.IDataObject;
namespace ToLink {
    internal static class StreamingPasteProof {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out RECT bounds);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left,top,right,bottom; }
        [DllImport("ole32.dll")] static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] ComDataObject data);
        delegate bool EnumWindow(IntPtr window,IntPtr context);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback,IntPtr context);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        static bool CancelNativeCopy(string file) {
            var handles=new List<IntPtr>();EnumWindows((handle,context)=>{if(IsWindowVisible(handle))handles.Add(handle);return true;},IntPtr.Zero);
            foreach(var handle in handles)try {
                var window=AutomationElement.FromHandle(handle);
                string kind=window.Current.ClassName;
                if(kind!="#32770" && kind!="OperationStatusWindow")continue;
                bool ours=false;
                foreach(AutomationElement text in window.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)))
                    if(text.Current.Name.Contains(Path.GetFileNameWithoutExtension(file))){ours=true;break;}
                if(!ours)continue;
                foreach(AutomationElement button in window.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button))) {
                    if(button.Current.Name!="取消" && button.Current.Name!="Cancel")continue;
                    object pattern;if(button.TryGetCurrentPattern(InvokePattern.Pattern,out pattern)){((InvokePattern)pattern).Invoke();return true;}
                }
            } catch(ElementNotAvailableException) {}
            return false;
        }
        [STAThread] static int Main(string[] args) {
            bool cancelTest=Array.IndexOf(args,"--cancel")>=0,multi=Array.IndexOf(args,"--multi")>=0;
            var backup=new DataObject();var previous=Clipboard.GetDataObject();
            if(previous!=null)foreach(var format in previous.GetFormats(false)) {
                if(format=="FileDrop")backup.SetFileDropList(Clipboard.GetFileDropList());
                else if(format=="Preferred DropEffect") {
                    var stream=previous.GetData(format) as MemoryStream;
                    if(stream==null){Console.WriteLine("SKIP: unsupported clipboard effect");return 2;}
                    backup.SetData(format,new MemoryStream(stream.ToArray()));
                } else if(format=="UnicodeText" || format=="Text" || format=="OEMText" || format=="Locale" || format=="System.String") {
                    if(Clipboard.ContainsText())backup.SetText(Clipboard.GetText(),TextDataFormat.UnicodeText);
                } else if(format!="FileNameW" && format!="FileName") {Console.WriteLine("SKIP: clipboard format "+format);return 2;}
            }
            string root=Path.Combine(@"D:\ToLink\stream-proof",DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            string target=Path.Combine(root,"target"),source=Path.Combine(root,"cloud-stream-proof-"+Path.GetFileName(root)+".bin");
            string allowedTarget=null;foreach(string arg in args)if(arg.StartsWith("--target=",StringComparison.Ordinal))allowedTarget=Path.GetFullPath(arg.Substring(9));
            Directory.CreateDirectory(target);File.WriteAllBytes(source,new byte[0]);
            const int firstSize=4*1024*1024;int total=firstSize+(multi ? 70000 : 0);
            StreamingFiles batch=null;var receiver=new IncomingFiles();VirtualFileDataObject data=null;Task producer=null;
            try {
                dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                IntPtr window=IntPtr.Zero;var point=new System.Drawing.Point();
                for(int tries=0;tries<40 && window==IntPtr.Zero;tries++) {
                    foreach(dynamic item in shell.Windows())try {
                        string path=(string)item.Document.Folder.Self.Path;
                        if(allowedTarget!=null ? !string.Equals(path,allowedTarget,StringComparison.OrdinalIgnoreCase) : (!path.StartsWith(@"D:\ToLink\stream-proof\",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path)!="target"))continue;
                        IntPtr candidate=new IntPtr((int)item.HWND);RECT bounds;GetWindowRect(candidate,out bounds);
                        var hit=new System.Drawing.Point((bounds.left+bounds.right)/2,bounds.top+14);
                        if(GetForegroundWindow()==candidate || GetAncestor(WindowFromPoint(hit),2)==candidate){window=candidate;point=hit;target=path;break;}
                    } catch {}
                    if(window==IntPtr.Zero){if(tries==0)Process.Start("explorer.exe","/n,\""+target+"\"");Thread.Sleep(250);}
                }
                if(window==IntPtr.Zero)throw new Exception("No visible test folder; please activate a stream-proof target folder");
                var screen=SystemInformation.VirtualScreen;
                var click=new Dictionary<string,object>{{"type","button"},{"button",0},{"down",true},{"x",(point.X-screen.X)/(double)(screen.Width-1)},{"y",(point.Y-screen.Y)/(double)(screen.Height-1)}};
                if(GetForegroundWindow()!=window){var controls=new InputController();controls.Apply(click);click["down"]=false;controls.Apply(click);Thread.Sleep(200);}
                if(GetForegroundWindow()!=window)throw new Exception("Test folder is not foreground");
                string id=Guid.NewGuid().ToString("N");
                var manifest=new List<object>{new Dictionary<string,object>{{"name",Path.GetFileName(source)},{"size",(long)firstSize}}};
                if(multi){manifest.Add(new Dictionary<string,object>{{"name","多文件-"+Path.GetFileName(root)+"-"+id+".bin"},{"size",70000L}});manifest.Add(new Dictionary<string,object>{{"name","empty-"+Path.GetFileName(root)+"-"+id+".txt"},{"size",0L}});}
                receiver.Begin(new Dictionary<string,object>{{"id",id},{"streaming",true},{"files",manifest.ToArray()}});
                var input=new InputController();
                if(!receiver.StartStreaming(input))throw new Exception("Streaming paste was not started");
                batch=(StreamingFiles)typeof(IncomingFiles).GetField("streaming",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(receiver);
                data=(VirtualFileDataObject)typeof(StreamingClipboard).GetField("current",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
                source=batch.Paths[0];
                long sent=0;bool early=false;
                producer=Task.Run(async()=>{
                    var frame=new byte[32+65536];System.Text.Encoding.ASCII.GetBytes(id).CopyTo(frame,0);
                    for(int i=32;i<frame.Length;i++)frame[i]=(byte)((i-32)%251);
                    for(int file=0;file<batch.Sizes.Length;file++)for(long offset=0;offset<batch.Sizes[file];offset+=65536) {
                        receiver.Append(frame,32+(int)Math.Min(65536,batch.Sizes[file]-offset));Interlocked.Exchange(ref sent,receiver.Received);
                        await Task.Delay(cancelTest ? 350 : 150);
                    }
                    receiver.Publish(input);
                });
                string destination=Path.Combine(target,Path.GetFileName(source));
                for(int i=0;i<240;i++) {
                    Thread.Sleep(100);Application.DoEvents();
                    if(File.Exists(destination)) {
                        long size=new FileInfo(destination).Length,current=Interlocked.Read(ref sent);
                        if(size>0 && current<total && batch.Delivered>0 && !early){early=true;File.WriteAllBytes(Path.Combine(root,"native-copy.jpg"),RemoteAgent.CaptureDesktop(SystemInformation.VirtualScreen,new AgentConfig{MaxWidth=1600,Quality=70}));Console.WriteLine("EARLY_STREAM_DELIVERY delivered="+batch.Delivered+" target="+size+" received="+current+" total="+total);}
                        if(cancelTest && current>=262144 && current<total && CancelNativeCopy(Path.GetFileName(source))) {
                            Console.WriteLine("NATIVE_CANCEL_INVOKED received="+current);
                            try { if(!producer.Wait(5000))throw new Exception("Producer did not stop after native cancel"); }
                            catch(AggregateException ex) { if(ex.GetBaseException().Message.Contains("取消")){Console.WriteLine("PASS: native cancel stopped receiver; root="+root);return 0;} throw; }
                            throw new Exception("Native cancellation was ignored");
                        }
                        bool operating;data.InOperation(out operating);
                        if(current==total && size==firstSize && producer.IsCompleted && !operating)break;
                    }
                }
                producer.GetAwaiter().GetResult();
                if(cancelTest)throw new Exception("Native cancel button was not located");
                if(!early)throw new Exception("No target write before transfer completion");
                using(var sha=SHA256.Create()) {
                    foreach(string path in batch.Paths) {
                        string a=Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
                        string b=Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(Path.Combine(target,Path.GetFileName(path)))));
                        if(a!=b)throw new Exception("Target hash mismatch");
                    }
                }
                Console.WriteLine("PASS: native streaming copy and target SHA256; files="+batch.Paths.Length+"; root="+root);return 0;
            } catch(Exception ex) {Console.WriteLine("FAIL: "+ex);return 1;}
            finally {
                if(batch!=null)batch.Cancel("Test complete");if(producer!=null)try{producer.Wait(3000);}catch{}
                if(data!=null && OleIsCurrentClipboard(data)==0)Clipboard.SetDataObject(backup,true);
                receiver.Dispose();
                if(allowedTarget!=null && batch!=null)foreach(string path in batch.Paths) {
                    string copied=Path.Combine(target,Path.GetFileName(path));
                    if(File.Exists(copied) && Path.GetFileName(copied).Contains(Path.GetFileName(root)))try{File.Delete(copied);}catch(IOException){}
                }
            }
        }
    }
}
