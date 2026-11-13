// REQ-REMOTE-20261109-03. Local-only integration fixture for the real WinForms agent.
using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace ToLink {
    public static class NativePasteTest {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        [STAThread] public static int Main(string[] args) {
            if(Array.IndexOf(args,"--restore-plaintext")<0) return 2;
            const string expected="ToLink 端到端\r\n原生粘贴 😀";
            var prior=Clipboard.GetDataObject();
            if(prior!=null) foreach(string f in prior.GetFormats(false))
                if(f!="UnicodeText" && f!="Text" && f!="OEMText" && f!="Locale" && f!="System.String") {Console.WriteLine("Skipped: clipboard contains other formats");return 2;}
            string original=Clipboard.GetText();

            Application.EnableVisualStyles();
            var form=new Form {Text="ToLink 文件验收",TopMost=true,StartPosition=FormStartPosition.CenterScreen};
            var box=new TextBox {Dock=DockStyle.Fill,Multiline=true};form.Controls.Add(box);
            int shortcuts=0,exitCode=1;
            box.KeyDown+=(s,e)=>{if(e.Control && e.KeyCode==Keys.V)shortcuts++;};
            form.Shown+=async(s,e)=>{
                var stop=new CancellationTokenSource();Task run=null;RemoteAgent agent=null;string batch=null;
                try {
                    form.Activate();box.Focus();await Task.Delay(120);
                    if(GetForegroundWindow()!=form.Handle){
                        var point=box.PointToScreen(new Point(15,15));if(WindowFromPoint(point)!=box.Handle)throw new IOException("Test window covered");
                        var b=SystemInformation.VirtualScreen;var cmd=new Dictionary<string,object>{{"type","button"},{"button",0},{"down",true},{"x",(point.X-b.X)/(double)(b.Width-1)},{"y",(point.Y-b.Y)/(double)(b.Height-1)}};
                        var controls=new InputController();controls.Apply(cmd);cmd["down"]=false;controls.Apply(cmd);await Task.Delay(150);
                    }
                    if(GetForegroundWindow()!=form.Handle || !box.Focused || Clipboard.GetText()!=original)throw new IOException("Test focus/clipboard changed");
                    var config=new AgentConfig{Server="ws://127.0.0.1:"+int.Parse(args[0]),DeviceId="fixture",Token=args[1],Fps=2,MaxWidth=640,Quality=55};
                    agent=new RemoteAgent(config,form,ignore=>{});run=Task.Run(()=>agent.Run(stop.Token));bool sawText=false;
                    for(int i=0;i<500;i++){
                        await Task.Delay(30);
                        if(GetForegroundWindow()!=form.Handle)throw new IOException("Focus changed during test");
                        if(!sawText && shortcuts==1 && box.Text==expected && Clipboard.GetText()==expected){sawText=true;Console.WriteLine("TEXT_OK");}
                        if(sawText && Clipboard.ContainsFileDropList()){
                            var paths=Clipboard.GetFileDropList();
                            if(paths.Count!=3 || Path.GetFileName(paths[0])!="样例.bin" || Path.GetFileName(paths[1])!="empty.txt" || Path.GetFileName(paths[2])!="sample.txt")throw new IOException("Manifest mismatch");
                            string folder=Path.GetDirectoryName(Path.GetDirectoryName(paths[0]));if(Path.GetDirectoryName(folder)!=Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'),"IncomingFiles"))throw new IOException("Cache path mismatch");
                            batch=folder;var bytes=File.ReadAllBytes(paths[0]);
                            if(bytes.Length!=70000 || new FileInfo(paths[1]).Length!=0 || File.ReadAllText(paths[2])!="hello")throw new IOException("File length/content mismatch");
                            for(int n=0;n<bytes.Length;n++)if(bytes[n]!=(byte)(n%251))throw new IOException("Binary mismatch");
                            if(shortcuts<2)continue;
                            Console.WriteLine("FILES_OK");exitCode=0;break;
                        }
                    }
                }catch(Exception ex){Console.WriteLine(ex.Message);}
                stop.Cancel();if(agent!=null)agent.Stop();if(run!=null)try{await run;}catch(OperationCanceledException){}
                var current=Clipboard.GetFileDropList();
                if(Clipboard.GetText()==expected || (batch!=null && current.Count==3 && current[0].StartsWith(batch+"\\",StringComparison.OrdinalIgnoreCase))){
                    if(original.Length==0)Clipboard.Clear();else Clipboard.SetText(original);
                    if(batch!=null)Directory.Delete(batch,true);
                }
                stop.Dispose();form.Close();
            };
            Application.Run(form);return exitCode;
        }
    }
}
