// REQ-REMOTE-20261111-01: Explorer reads a virtual-file stream while network chunks arrive.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComStream = System.Runtime.InteropServices.ComTypes.IStream;
using ComStat = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace ToLink {
    internal sealed class StreamingFiles {
        internal readonly string[] Paths; internal readonly long[] Sizes;
        readonly long[] available; readonly object gate = new object();
        string error; DateTime lastData = DateTime.UtcNow; bool complete;
        long delivered;
        internal long Delivered { get { return Interlocked.Read(ref delivered); } }
        internal void RecordDelivery(int count) { Interlocked.Add(ref delivered,count); }
        public StreamingFiles(string[] paths, long[] sizes) { Paths=paths; Sizes=sizes; available=new long[sizes.Length]; }
        public void Check() { lock(gate) { if(error!=null)throw new IOException(error); } }
        public void Advance(int index,long count) {
            lock(gate) { if(error!=null)throw new IOException(error); available[index]=count; lastData=DateTime.UtcNow; Monitor.PulseAll(gate); }
        }
        public void Complete() { lock(gate) { complete=true; Monitor.PulseAll(gate); } }
        public void Cancel(string message) { lock(gate) { if(error==null)error=message; Monitor.PulseAll(gate); } }
        public int Available(int index,long position,int wanted) {
            lock(gate) {
                while(true) {
                    if(error!=null)throw new COMException(error,unchecked((int)0x800704c7));
                    if(position>=Sizes[index])return 0;
                    long count=available[index]-position;
                    if(count>0)return (int)Math.Min(wanted,count);
                    if(complete)throw new COMException("文件数据不完整",unchecked((int)0x8003001e));
                    if((DateTime.UtcNow-lastData).TotalSeconds>30) {
                        error="文件传输超时"; Monitor.PulseAll(gate);
                        throw new COMException(error,unchecked((int)0x800705b4));
                    }
                    Monitor.Wait(gate,500);
                }
            }
        }
    }
    // Preserve Read's S_FALSE at EOF. Returning a short successful read before EOF would
    // let a Shell consumer mistake a temporarily unavailable network chunk for end-of-file.
    [ComVisible(true),Guid("0000000c-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IVirtualFileStream {
        [PreserveSig] int Read([Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)] byte[] data,int count,IntPtr read);
        void Write([MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)] byte[] data,int count,IntPtr written);
        void Seek(long offset,int origin,IntPtr position);
        void SetSize(long size);
        void CopyTo(ComStream target,long count,IntPtr read,IntPtr written);
        void Commit(int flags); void Revert(); void LockRegion(long offset,long count,int type); void UnlockRegion(long offset,long count,int type);
        void Stat(out ComStat stat,int flags); void Clone(out IVirtualFileStream stream);
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class VirtualFileStream : IVirtualFileStream {
        readonly StreamingFiles files; readonly int index; readonly object gate=new object(); long position;
        internal VirtualFileStream(StreamingFiles batch,int file,long start=0) { files=batch;index=file;position=start; }
        public int Read(byte[] data,int count,IntPtr read) {
            int done=0;
            try {
                lock(gate) {
                    if(count<0 || data==null || count>data.Length)return unchecked((int)0x80070057);
                    using(var source=new FileStream(files.Paths[index],FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                        source.Position=position;
                        while(done<count) {
                            int ready=files.Available(index,position,count-done); if(ready==0)break;
                            int n=source.Read(data,done,ready); if(n==0)throw new IOException("文件数据读取失败");
                            position+=n;done+=n;
                        }
                    }
                }
                files.RecordDelivery(done);
                return done==count ? 0 : 1;
            } catch(COMException ex) { return ex.ErrorCode; }
              catch(Exception) { return unchecked((int)0x8003001e); }
              finally { if(read!=IntPtr.Zero)Marshal.WriteInt32(read,done); }
        }
        public void Seek(long offset,int origin,IntPtr result) {
            lock(gate) {
                if(origin<0 || origin>2)throw new COMException("Invalid seek",unchecked((int)0x80030001));
                long next=checked((origin==0 ? 0 : origin==1 ? position : files.Sizes[index])+offset);
                if(next<0)throw new COMException("Invalid seek",unchecked((int)0x80030001));
                position=next;if(result!=IntPtr.Zero)Marshal.WriteInt64(result,position);
            }
        }
        public void Stat(out ComStat stat,int flags) { stat=new ComStat {type=2,cbSize=files.Sizes[index],grfMode=0,pwcsName=(flags&1)==0 ? Path.GetFileName(files.Paths[index]) : null}; }
        public void Clone(out IVirtualFileStream stream) { lock(gate)stream=new VirtualFileStream(files,index,position); }
        public void CopyTo(ComStream target,long count,IntPtr read,IntPtr written) {
            long total=0;var buffer=new byte[65536];var result=Marshal.AllocHGlobal(4);
            try {
                while(total<count) {
                    int hr=Read(buffer,(int)Math.Min(buffer.Length,count-total),result),n=Marshal.ReadInt32(result);
                    if(hr<0)Marshal.ThrowExceptionForHR(hr);
                    if(n==0)break;
                    target.Write(buffer,n,result); if(Marshal.ReadInt32(result)!=n)throw new IOException("Short write");
                    total+=n;if(hr==1)break;
                }
            } finally { Marshal.FreeHGlobal(result);if(read!=IntPtr.Zero)Marshal.WriteInt64(read,total);if(written!=IntPtr.Zero)Marshal.WriteInt64(written,total); }
        }
        public void Write(byte[] data,int count,IntPtr written) { throw new COMException("Read only",unchecked((int)0x80030005)); }
        public void SetSize(long size) { throw new COMException("Read only",unchecked((int)0x80030005)); }
        public void Commit(int flags) {} public void Revert() { throw new COMException("Not supported",unchecked((int)0x80030001)); }
        public void LockRegion(long offset,long count,int type) { throw new COMException("Not supported",unchecked((int)0x80030001)); }
        public void UnlockRegion(long offset,long count,int type) { throw new COMException("Not supported",unchecked((int)0x80030001)); }
    }
    [ComVisible(true),Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAsyncFileCopy {
        void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool value);
        void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool value);
        void StartOperation(IntPtr reserved);
        void InOperation([MarshalAs(UnmanagedType.Bool)] out bool value);
        void EndOperation(int result,IntPtr reserved,uint effects);
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class VirtualFileDataObject : ComDataObject,IAsyncFileCopy {
        [System.Diagnostics.Conditional("STREAM_PROOF")]
        static void Trace(string message) { Console.WriteLine("[ToLink-stream-test] "+message); }
        readonly StreamingFiles files;
        readonly short descriptor=Format("FileGroupDescriptorW"),contents=Format("FileContents"),preferred=Format("Preferred DropEffect");
        volatile bool asyncMode=true,operating;
        internal VirtualFileDataObject(StreamingFiles batch) { files=batch; }
        internal void Cancel() { files.Cancel("Windows 文件复制已取消"); }
        internal static short Format(string name) { return unchecked((short)RegisterClipboardFormat(name)); }
        static FORMATETC Entry(short format,TYMED medium,int index=-1) { return new FORMATETC{cfFormat=format,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=index,tymed=medium,ptd=IntPtr.Zero}; }
        public int QueryGetData(ref FORMATETC format) {
            if(format.dwAspect!=DVASPECT.DVASPECT_CONTENT)return unchecked((int)0x8004006b);
            if((format.cfFormat==descriptor || format.cfFormat==preferred) && (format.tymed&TYMED.TYMED_HGLOBAL)!=0)return 0;
            if(format.cfFormat==contents && (format.tymed&TYMED.TYMED_ISTREAM)!=0 && format.lindex>=-1 && format.lindex<files.Paths.Length)return 0;
            return unchecked((int)0x80040064);
        }
        public void GetData(ref FORMATETC format,out STGMEDIUM medium) {
            medium=new STGMEDIUM();int hr=QueryGetData(ref format);if(hr!=0)Marshal.ThrowExceptionForHR(hr);
            files.Check();
            if(format.cfFormat==contents) {
                Trace("GetData index="+format.lindex);
                if(format.lindex<0)Marshal.ThrowExceptionForHR(unchecked((int)0x80040068));
                medium.tymed=TYMED.TYMED_ISTREAM;
                medium.unionmember=Marshal.GetComInterfaceForObject(new VirtualFileStream(files,format.lindex),typeof(IVirtualFileStream));
                return;
            }
            byte[] bytes;
            if(format.cfFormat==preferred)bytes=BitConverter.GetBytes(1); // copy, never move/delete source
            else {
                bytes=new byte[4+592*files.Paths.Length];
                using(var stream=new MemoryStream(bytes))using(var writer=new BinaryWriter(stream,Encoding.Unicode)) {
                    writer.Write(files.Paths.Length);
                    for(int i=0;i<files.Paths.Length;i++) {
                        int start=4+592*i;stream.Position=start;writer.Write(0x80004064u);
                        stream.Position=start+36;writer.Write(0x80u);
                        stream.Position=start+56;writer.Write(DateTime.UtcNow.ToFileTimeUtc());
                        writer.Write((uint)((ulong)files.Sizes[i]>>32));writer.Write((uint)files.Sizes[i]);
                        writer.Write(Encoding.Unicode.GetBytes(Path.GetFileName(files.Paths[i])+"\0"));
                    }
                }
            }
            IntPtr handle=GlobalAlloc(0x42,(UIntPtr)bytes.Length);if(handle==IntPtr.Zero)throw new OutOfMemoryException();
            IntPtr pointer=GlobalLock(handle);
            if(pointer==IntPtr.Zero){GlobalFree(handle);throw new OutOfMemoryException();}
            try { Marshal.Copy(bytes,0,pointer,bytes.Length); } finally { GlobalUnlock(handle); }
            medium.tymed=TYMED.TYMED_HGLOBAL;medium.unionmember=handle;
        }
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) {
            if(direction!=DATADIR.DATADIR_GET)throw new COMException("Not supported",unchecked((int)0x80004001));
            return new FileFormats(new[]{Entry(descriptor,TYMED.TYMED_HGLOBAL),Entry(contents,TYMED.TYMED_ISTREAM),Entry(preferred,TYMED.TYMED_HGLOBAL)});
        }
        public void GetDataHere(ref FORMATETC format,ref STGMEDIUM medium) { throw new COMException("Not supported",unchecked((int)0x80040069)); }
        public int GetCanonicalFormatEtc(ref FORMATETC input,out FORMATETC output) { output=input;output.ptd=IntPtr.Zero;return 0x40130; }
        public void SetData(ref FORMATETC format,ref STGMEDIUM medium,bool release) { if(release)ReleaseStgMedium(ref medium); }
        public int DAdvise(ref FORMATETC format,ADVF flags,IAdviseSink sink,out int connection) { connection=0;return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { throw new COMException("Not supported",unchecked((int)0x80040003)); }
        public int EnumDAdvise(out IEnumSTATDATA data) { data=null;return unchecked((int)0x80040003); }
        public void SetAsyncMode(bool value) { asyncMode=value; } public void GetAsyncMode(out bool value) { value=asyncMode; }
        public void StartOperation(IntPtr reserved) { operating=true; }
        public void InOperation(out bool value) { value=operating; }
        public void EndOperation(int result,IntPtr reserved,uint effects) { operating=false;if(result<0 || effects==0)Cancel(); }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags,UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class FileFormats : IEnumFORMATETC {
        readonly FORMATETC[] formats;int position;
        internal FileFormats(FORMATETC[] values,int index=0) { formats=values;position=index; }
        public int Next(int count,FORMATETC[] values,int[] fetched) { int n=0;while(n<count && position<formats.Length)values[n++]=formats[position++];if(fetched!=null&&fetched.Length>0)fetched[0]=n;return n==count?0:1; }
        public int Skip(int count) { int old=position;position=Math.Min(formats.Length,position+count);return position-old==count?0:1; }
        public int Reset() { position=0;return 0; }
        public void Clone(out IEnumFORMATETC copy) { copy=new FileFormats(formats,position); }
    }
    internal static class StreamingClipboard {
        static readonly object gate=new object();static Control owner;static volatile VirtualFileDataObject current;
        static Control Owner() {
            lock(gate) {
                if(owner!=null)return owner;
                var ready=new ManualResetEvent(false);Exception failure=null;
                var thread=new Thread(()=>{
                    try {
                        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
                        var control=new Control();var handle=control.Handle;owner=control;ready.Set();
                        Application.Run();
                    } catch(Exception ex) { failure=ex;ready.Set(); }
                    finally { OleUninitialize(); }
                });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();ready.WaitOne();ready.Dispose();
                if(failure!=null)throw failure;return owner;
            }
        }
        internal static VirtualFileDataObject Publish(StreamingFiles batch) {
            var previous=current;
            if(previous!=null) {
                bool busy;previous.InOperation(out busy);
                if(busy)throw new IOException("请等待 Windows 当前文件复制结束，或在系统复制窗口取消");
            }
            // Dedicated STA keeps COM reads waiting on network data off the agent's UI and receive loop.
            return (VirtualFileDataObject)Owner().Invoke((Func<VirtualFileDataObject>)(()=>{
                var value=new VirtualFileDataObject(batch);
                Marshal.ThrowExceptionForHR(OleSetClipboard(value));current=value;return value;
            }));
        }
        [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] static extern void OleUninitialize();
        [DllImport("ole32.dll")] static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] ComDataObject data);
    }
}
