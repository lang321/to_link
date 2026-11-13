// REQ-REMOTE-20261111-01: verify partial arrivals cannot be mistaken for EOF.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace ToLink {
    internal static class StreamingReadTests {
        static int Main() {
            string root=Path.Combine(Path.GetTempPath(),"tolink-stream-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string file=Path.Combine(root,"data.bin");File.WriteAllBytes(file,new byte[0]);
            IntPtr read=Marshal.AllocHGlobal(4),position=Marshal.AllocHGlobal(8);
            try {
                var batch=new StreamingFiles(new[]{file},new[]{100000L});var stream=new VirtualFileStream(batch,0);var bytes=new byte[120000];
                var pending=Task.Run(()=>stream.Read(bytes,bytes.Length,read));
                Thread.Sleep(100);if(pending.IsCompleted)throw new Exception("Premature EOF before first chunk");
                using(var writer=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) {
                    var chunk=new byte[50000];for(int i=0;i<chunk.Length;i++)chunk[i]=(byte)(i%251);
                    writer.Write(chunk,0,chunk.Length);writer.Flush();batch.Advance(0,50000);
                    Thread.Sleep(100);if(pending.IsCompleted)throw new Exception("Premature EOF between chunks");
                    writer.Write(chunk,0,chunk.Length);writer.Flush();batch.Advance(0,100000);batch.Complete();
                    if(pending.Result!=1 || Marshal.ReadInt32(read)!=100000)throw new Exception("Incorrect EOF length/status");
                    for(int i=0;i<100000;i++)if(bytes[i]!=chunk[i%50000])throw new Exception("Data mismatch");
                }
                stream.Seek(50000,0,position);IVirtualFileStream clone;stream.Clone(out clone);
                if(clone.Read(bytes,10,read)!=0 || Marshal.ReadInt32(read)!=10)throw new Exception("Seek/clone failed");
                var cancelled=new StreamingFiles(new[]{file},new[]{100001L});var waiting=new VirtualFileStream(cancelled,0);
                var blocked=Task.Run(()=>waiting.Read(bytes,1,read));Thread.Sleep(100);cancelled.Cancel("cancel test");
                if(!blocked.Wait(2000) || blocked.Result>=0)throw new Exception("Cancellation did not abort read");
                Console.WriteLine("PASS: delayed arrival, full-length read, EOF, content, seek/clone, cancellation");return 0;
            } catch(Exception ex) {Console.WriteLine(ex);return 1;}
            finally {Marshal.FreeHGlobal(read);Marshal.FreeHGlobal(position);Directory.Delete(root,true);}
        }
    }
}
