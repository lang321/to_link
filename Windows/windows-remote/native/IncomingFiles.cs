// REQ-REMOTE-20261109-03. One ordered batch per authenticated viewing connection.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ToLink {
    internal sealed class IncomingFiles : IDisposable {
        const long Limit = 100L * 1024 * 1024;
        readonly string root = Path.Combine(Program.BaseDirectory, "IncomingFiles");
        readonly List<string> paths = new List<string>();
        readonly List<long> sizes = new List<long>();
        string directory; FileStream output; int index; long offset;
        StreamingFiles streaming; bool pasted;
        public string Id { get; private set; }
        public long Received { get; private set; }
        public IntPtr Target { get; private set; }
        public void Begin(Dictionary<string,object> m) {
            if (Id != null) throw new IOException("已有文件正在接收");
            string id = Convert.ToString(m["id"]);
            if (!Regex.IsMatch(id, "^[0-9a-f]{32}$")) throw new IOException("批次无效");
            var names = new List<string>(); long total = 0;
            foreach (var raw in (IEnumerable)m["files"]) {
                var meta = (Dictionary<string,object>)raw; var name = (string)meta["name"]; long size = Convert.ToInt64(meta["size"]);
                if (names.Count >= 32 || size < 0 || size > Limit-total || string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
                    name.Trim() != name || name.EndsWith(".") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Regex.IsMatch(name.Split('.')[0].TrimEnd(), @"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase))
                    throw new IOException("文件名或批次大小不符合 Windows 要求");
                total += size; names.Add(name); sizes.Add(size);
            }
            if (names.Count == 0) throw new IOException("请先选择文件");
            Directory.CreateDirectory(root);
            long cached = 0; string latestFile = Path.Combine(root,"latest");
            string latest = File.Exists(latestFile) ? File.ReadAllText(latestFile) : "";
            foreach (string dir in Directory.GetDirectories(root)) {
                if (!Regex.IsMatch(Path.GetFileName(dir),"^[0-9a-f]{32}$") || (File.GetAttributes(dir) & FileAttributes.ReparsePoint)!=0) continue;
                string marker = Path.Combine(dir,".tolink-size"); if (!File.Exists(marker)) continue;
                if (Path.GetFileName(dir) != latest && File.GetLastWriteTimeUtc(marker) < DateTime.UtcNow.AddDays(-1)) {
                    try { Directory.Delete(dir,true); continue; } catch (IOException) {} catch (UnauthorizedAccessException) {}
                }
                long reserved; if (long.TryParse(File.ReadAllText(marker),out reserved)) cached += reserved;
            }
            if (cached + total > 1024L*1024*1024) throw new IOException("文件接收缓存已满");
            Target = InputController.Foreground(); Id=id; directory=Path.Combine(root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,".tolink-size"),total.ToString());
            for(int i=0;i<names.Count;i++) {
                var folder=Directory.CreateDirectory(Path.Combine(directory,i.ToString("D2")));
                string path=Path.Combine(folder.FullName,names[i]); using(File.Open(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {}
                paths.Add(path);
            }
            AdvanceEmpty();
            if(m.ContainsKey("streaming") && Convert.ToBoolean(m["streaming"]))streaming=new StreamingFiles(paths.ToArray(),sizes.ToArray());
        }
        public bool StartStreaming(InputController input) {
            if(streaming==null)return false;
            StreamingClipboard.Publish(streaming);
            pasted=Target!=IntPtr.Zero && Target==InputController.Foreground();
            if(pasted) { input.Release();InputController.PasteShortcut(); }
            return pasted;
        }
        void AdvanceEmpty() { while(index<sizes.Count && sizes[index]==0) index++; }
        public void Append(byte[] frame, int count) {
            if (Id == null || count<=32 || count>65568 || Encoding.ASCII.GetString(frame,0,32)!=Id || index>=paths.Count || count-32>sizes[index]-offset)
                throw new IOException("文件分块不匹配");
            if(streaming!=null)streaming.Check();
            if(output==null) output=File.Open(paths[index],FileMode.Open,FileAccess.Write,streaming==null ? FileShare.None : FileShare.Read|FileShare.Delete);
            output.Write(frame,32,count-32); Received+=count-32; offset+=count-32;
            // Only expose flushed bytes: Explorer is already reading this file through IStream.
            if(streaming!=null) { output.Flush();streaming.Advance(index,offset); }
            if(offset==sizes[index]) { output.Dispose();output=null;index++;offset=0;AdvanceEmpty(); }
        }
        // Called on the WinForms STA after all bytes have arrived.
        public bool Publish(InputController input) {
            if (Id==null || index!=paths.Count) throw new IOException("文件尚未接收完整");
            if(streaming!=null) {
                streaming.Check();streaming.Complete();
                File.WriteAllText(Path.Combine(root,"latest"),Path.GetFileName(directory));
                directory=null;return pasted;
            }
            var list = new StringCollection(); list.AddRange(paths.ToArray());
            var data = new DataObject(); data.SetFileDropList(list);
            using(var effect=new MemoryStream(BitConverter.GetBytes(1))) {
                data.SetData("Preferred DropEffect",effect);
                InputController.WriteClipboard(data);
            }
            string published=Path.GetFileName(directory);
            directory=null; // Clipboard owns references to these paths even if the target refuses Ctrl+V.
            File.WriteAllText(Path.Combine(root,"latest"),published);
            bool sameWindow = Target!=IntPtr.Zero && Target==InputController.Foreground();
            if (sameWindow) { input.Release(); InputController.PasteShortcut(); }
            return sameWindow;
        }
        public void Dispose() {
            if(streaming!=null && directory!=null)streaming.Cancel("文件传输已中断");
            if(output!=null) output.Dispose(); output=null;
            if(directory!=null) { try { Directory.Delete(directory,true); } catch(IOException) {} catch(UnauthorizedAccessException) {} }
            directory=null;streaming=null;pasted=false;Id=null;Received=0;index=0;offset=0;paths.Clear();sizes.Clear();
        }
    }
}
