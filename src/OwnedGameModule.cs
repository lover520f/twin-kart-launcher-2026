using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TwinKartLauncher
{
    internal static class OwnedGameModule
    {
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr at, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualFreeEx(IntPtr process, IntPtr at, UIntPtr size, uint operation);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr at, byte[] bytes, UIntPtr count, out UIntPtr written);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr at, byte[] bytes, UIntPtr count, out UIntPtr read);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        internal const string SupportedWireClient="5041d9f5d45adb4b1c932d8056a89f70db7fa98f93614ac30685cd1adeb9d9c2";
        internal const int ChannelConstructorRva=0x1bd2870;
        internal const int ChannelConstructorLength=129;
        internal const int BodyConstructorRva=0x1bd2760;
        internal const int BodyConstructorLength=268;

        // Exact-build FKtKartChannelParam constructor initializes 18 floats
        // through immediate stores. Read its defaults without calling into the
        // child or persisting its code and data.
        internal static bool TryReadChannelDefaults(byte[] code,out byte[] fields)
        {
            fields=null;
            if(code==null || code.Length!=ChannelConstructorLength ||
               code[0]!=0xc7 || code[1]!=0x01 || code[6]!=0x48 || code[7]!=0x8b || code[8]!=0xc1 || code[128]!=0xc3)return false;
            for(int index=1;index<18;++index) {
                int at=9+(index-1)*7;
                if(code[at]!=0xc7 || code[at+1]!=0x41 || code[at+2]!=index*4)return false;
            }
            var result=new byte[72];
            Buffer.BlockCopy(code,2,result,0,4);
            for(int index=1;index<18;++index)Buffer.BlockCopy(code,12+(index-1)*7,result,index*4,4);
            fields=result;
            return true;
        }

        internal static bool TryReadBodyDefaults(byte[] code,out byte[] fields)
        {
            fields=null;
            if(code==null || code.Length!=BodyConstructorLength || code[0]!=0x33 || code[1]!=0xc0 || !BitConverter.IsLittleEndian)return false;
            var result=new byte[0xd8];var written=new bool[result.Length];bool returnsThis=false;
            int at=2;
            while(at<code.Length-1) {
                if(at+3<=code.Length && code[at]==0x48 && code[at+1]==0x8b && code[at+2]==0xc1) {
                    if(returnsThis)return false;
                    returnsThis=true;at+=3;continue;
                }
                int width=4;
                if(code[at]==0x48){width=8;++at;}
                else if(code[at]==0x66){width=2;++at;}
                if(at+2>code.Length-1)return false;
                byte op=code[at++],operand=code[at++];
                if(op==0x88 || op==0xc6) {
                    if(width!=4)return false;
                    width=1;
                }
                bool immediate=op==0xc7 || op==0xc6;
                if((!immediate && (returnsThis || (op!=0x89 && op!=0x88))) || (operand&0x3f)!=0x01)return false;
                int offset;
                switch(operand>>6) {
                    case 0:offset=0;break;
                    case 1:if(at>=code.Length-1)return false;offset=(sbyte)code[at++];break;
                    case 2:if(at+4>code.Length-1)return false;offset=BitConverter.ToInt32(code,at);at+=4;break;
                    default:return false;
                }
                if(offset<0 || offset>result.Length-width)return false;
                int size=immediate?Math.Min(width,4):0;
                if(at+size>code.Length-1)return false;
                byte extension=size>0 && (code[at+size-1]&0x80)!=0?(byte)255:(byte)0;
                for(int index=0;index<width;++index) {
                    if(written[offset+index])return false;
                    result[offset+index]=index<size?code[at+index]:extension;
                    written[offset+index]=true;
                }
                at+=size;
            }
            if(at!=code.Length-1 || code[at]!=0xc3 || !returnsThis)return false;
            for(int index=0;index<result.Length;++index)
                if(!written[index] && !(index>=0x19 && index<=0x1b || index>=0x9d && index<=0x9f ||
                    index>=0xaa && index<=0xab || index>=0xbd && index<=0xbf))return false;
            fields=result;
            return true;
        }
        internal static bool TryWireConstant(byte[] data,out string value)
        {
            value=null;
            if(data==null || data.Length!=90 || data[88]!=0 || data[89]!=0)return false;
            for(int i=0;i<88;i+=2) {
                byte c=data[i];
                if(data[i+1]!=0 || !(c>='A' && c<='Z' || c>='a' && c<='z' || c>='0' && c<='9' || c=='+' || c=='/' || c=='='))
                    return false;
            }
            var candidate=Encoding.Unicode.GetString(data,0,88);
            try {
                var bytes=Convert.FromBase64String(candidate);
                try {if(bytes.Length!=32 || !String.Equals(Convert.ToBase64String(bytes),candidate,StringComparison.Ordinal))return false;}
                finally {Array.Clear(bytes,0,bytes.Length);}
            } catch(FormatException) {return false;}
            value=candidate;
            return true;
        }
        static bool ReadWireConstant(Process process,IntPtr image,int offset,out string value)
        {
            var bytes=new byte[90];
            value=null;
            try {
                UIntPtr count;
                return ReadProcessMemory(process.Handle,new IntPtr(checked(image.ToInt64()+offset)),bytes,(UIntPtr)bytes.Length,out count) &&
                    count.ToUInt64()==(ulong)bytes.Length && TryWireConstant(bytes,out value);
            } finally {Array.Clear(bytes,0,bytes.Length);}
        }
        internal static void SendClientWire(Process process,string expectedGame,TextWriter input,CancellationToken stop,bool readChannelDefaults,bool readBodyDefaults)
        {
            // The supported Steam executable is packed. Wait for its own
            // loader to populate the exact read-only constants in memory.
            // Only the just-created, fingerprinted child is inspected.
            var timer=Stopwatch.StartNew();
            try {
                while(timer.ElapsedMilliseconds<20000 && !process.HasExited) {
                    stop.ThrowIfCancellationRequested();
                    process.Refresh();
                    ProcessModule module=null;
                    try {module=process.MainModule;}
                    catch(System.ComponentModel.Win32Exception error) {if(error.NativeErrorCode!=299 && error.NativeErrorCode!=126)throw;}
                    if(module!=null) {
                        if(!String.Equals(module.FileName,expectedGame,StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Owned client image changed.");
                        string transport,integrity;
                        if(ReadWireConstant(process,module.BaseAddress,0x52757f0,out transport) &&
                           ReadWireConstant(process,module.BaseAddress,0x5275790,out integrity)) {
                            byte[] channel=null,body=null;
                            if(readChannelDefaults) {
                                var code=new byte[ChannelConstructorLength];UIntPtr count;
                                if(!ReadProcessMemory(process.Handle,new IntPtr(checked(module.BaseAddress.ToInt64()+ChannelConstructorRva)),code,(UIntPtr)code.Length,out count) ||
                                   count.ToUInt64()!=(ulong)code.Length || !TryReadChannelDefaults(code,out channel)) {
                                    stop.WaitHandle.WaitOne(40);
                                    continue;
                                }
                            }
                            if(readBodyDefaults) {
                                var code=new byte[BodyConstructorLength];UIntPtr count;
                                if(!ReadProcessMemory(process.Handle,new IntPtr(checked(module.BaseAddress.ToInt64()+BodyConstructorRva)),code,(UIntPtr)code.Length,out count) ||
                                   count.ToUInt64()!=(ulong)code.Length || !TryReadBodyDefaults(code,out body)) {
                                    stop.WaitHandle.WaitOne(40);
                                    continue;
                                }
                            }
                            input.WriteLine(transport);
                            input.WriteLine(integrity);
                            if(channel!=null)input.WriteLine(Convert.ToBase64String(channel));
                            if(body!=null)input.WriteLine(Convert.ToBase64String(body));
                            input.Flush();
                            return;
                        }
                    }
                    stop.WaitHandle.WaitOne(40);
                }
                throw new InvalidOperationException("The owned client did not expose its supported launch parameters.");
            } finally {input.Dispose();}
        }
        internal static void Load(Process ownedProcess, string expectedGame, string modulePath, string moduleHash)
        {
            if (ownedProcess.HasExited || !String.Equals(ownedProcess.MainModule.FileName, expectedGame, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned game process changed.");
            modulePath=Path.GetFullPath(modulePath);
            if (ProfileStore.Fingerprint(modulePath)!=moduleHash) throw new InvalidDataException("Native module fingerprint mismatch.");
            IntPtr local=GetModuleHandle("kernel32.dll"), entry=GetProcAddress(local,"LoadLibraryW"), remote=IntPtr.Zero;
            foreach(ProcessModule loaded in ownedProcess.Modules)
                if(String.Equals(loaded.ModuleName,"kernel32.dll",StringComparison.OrdinalIgnoreCase)) remote=loaded.BaseAddress;
            if(local==IntPtr.Zero || entry==IntPtr.Zero || remote==IntPtr.Zero) throw new InvalidOperationException("Loader module unavailable.");
            IntPtr start=new IntPtr(checked(remote.ToInt64()+entry.ToInt64()-local.ToInt64()));
            byte[] path=Encoding.Unicode.GetBytes(modulePath+"\0");
            IntPtr allocation=VirtualAllocEx(ownedProcess.Handle,IntPtr.Zero,(UIntPtr)path.Length,0x3000,0x04);
            if(allocation==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            bool completed=false;
            try
            {
                UIntPtr written;
                if(!WriteProcessMemory(ownedProcess.Handle,allocation,path,(UIntPtr)path.Length,out written) || written.ToUInt64()!=(ulong)path.Length) throw new System.ComponentModel.Win32Exception();
                uint id; IntPtr thread=CreateRemoteThread(ownedProcess.Handle,IntPtr.Zero,UIntPtr.Zero,start,allocation,0,out id);
                if(thread==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                try { completed=WaitForSingleObject(thread,15000)==0; } finally { CloseHandle(thread); }
                if(!completed) throw new TimeoutException("Game module load timed out.");
                ownedProcess.Refresh();
                foreach(ProcessModule loaded in ownedProcess.Modules)
                    if(String.Equals(loaded.FileName,modulePath,StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException("Game module was not loaded.");
            }
            finally { if(completed || ownedProcess.HasExited) VirtualFreeEx(ownedProcess.Handle,allocation,UIntPtr.Zero,0x8000); }
        }
    }
}
