// REQ-REMOTE-20261110-01: run as the ordinary installing user against the real helper.
using System;
using System.IO;
namespace ToLink {
    internal static class DesktopBridgeTests {
        static int Main() {
            try {
                var frame=DesktopBridge.Request(new { command="capture" });
                if(frame.Length<4 || frame[0]!=255 || frame[1]!=216) throw new Exception("Invalid JPEG");
                using(var stream=new MemoryStream(frame)) using(var image=System.Drawing.Image.FromStream(stream)) {
                    if(image.Width<1 || image.Width>1920 || image.Height<1) throw new Exception("Invalid frame size");
                    Console.WriteLine("capture={0}x{1}, bytes={2}",image.Width,image.Height,frame.Length);
                }
                DesktopBridge.Request(new { command="wake" });
                DesktopBridge.Request(new { command="release" });
                bool rejected=false;
                try { DesktopBridge.Request(new { command="execute", path="ignored" }); } catch(IOException) { rejected=true; }
                if(!rejected) throw new Exception("Unknown command accepted");
                rejected=false;
                try { DesktopBridge.Request(new { command="input", input=new { type="paste",text="must-not-reach-clipboard" } }); } catch(IOException) { rejected=true; }
                if(!rejected) throw new Exception("Clipboard command accepted");
                Console.WriteLine("PASS: capture, wake, release, unknown command and clipboard rejection"); return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
