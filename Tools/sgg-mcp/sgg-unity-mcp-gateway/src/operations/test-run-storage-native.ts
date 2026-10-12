import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { TestRunStoreAdminError } from "./test-run-store-admin.js";

export type NativeFileEvidence = { path: string; dev: string; ino: string; size: string; mtime_ns: string; ctime_ns: string; content_hash: string };
type NativeRootEvidence = { root: string; root_dev: string; root_ino: string };
export type NativeConfigReplacement = NativeRootEvidence & { operation: "configure"; config: NativeFileEvidence; original_base64: string; updated_base64: string };
export type NativeMetadataCleanup = NativeRootEvidence & { operation: "cleanup"; lock: NativeFileEvidence; metadata: NativeFileEvidence[]; artifacts: NativeFileEvidence[]; config?: NativeFileEvidence };

/** Fixed trusted programs; no caller-supplied script, executable, shell expression or output path. */
export function runNativeStorageCommit(request: NativeConfigReplacement | NativeMetadataCleanup, deadlineUnixMs: number): void {
  const timeout = deadlineUnixMs - Date.now();
  if (timeout <= 0) throw new TestRunStoreAdminError("deadline_exceeded");
  let output: string;
  try {
    if (process.platform === "win32") {
      const systemRoot = process.env.SystemRoot;
      if (!systemRoot || !/^[A-Za-z]:\\/.test(systemRoot)) throw new Error("native helper unavailable");
      const executable = path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
      output = execFileSync(executable, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Buffer.from(WINDOWS_STORAGE_COMMIT_SCRIPT, "utf16le").toString("base64")], {
        input: JSON.stringify(request), encoding: "utf8", timeout, maxBuffer: 8192, stdio: ["pipe", "pipe", "pipe"],
        env: { SystemRoot: systemRoot, windir: systemRoot, ...(process.env.TEMP ? { TEMP: process.env.TEMP, TMP: process.env.TEMP } : {}) },
        windowsHide: true,
      });
    } else if (process.platform === "linux" && request.operation === "configure") {
      // flock serializes all cooperating Unix settings writers using the same persistent sidecar.
      const directory = fs.openSync(request.root, fs.constants.O_RDONLY | fs.constants.O_DIRECTORY | fs.constants.O_NOFOLLOW);
      let lock: number | undefined;
      try {
        const opened = fs.fstatSync(directory, { bigint: true });
        if (opened.dev.toString() !== request.root_dev || opened.ino.toString() !== request.root_ino) throw new TestRunStoreAdminError("config_changed");
        lock = fs.openSync(`/proc/self/fd/${directory}/${path.basename(request.config.path)}.settings-write.lock`, fs.constants.O_RDWR | fs.constants.O_CREAT | fs.constants.O_NOFOLLOW, 0o600);
        const stat = fs.fstatSync(lock, { bigint: true });
        if (!stat.isFile() || stat.nlink !== 1n) throw new TestRunStoreAdminError("config_unavailable");
        output = execFileSync("/usr/bin/flock", ["--nonblock", "--exclusive", "/proc/self/fd/4", process.execPath, "--input-type=module", "--eval", LINUX_CONFIG_COMMIT_SCRIPT], {
          input: JSON.stringify(request), encoding: "utf8", timeout, maxBuffer: 8192,
          stdio: ["pipe", "pipe", "pipe", directory, lock], env: {},
        });
      } finally { if (lock !== undefined) fs.closeSync(lock); fs.closeSync(directory); }
    } else throw new TestRunStoreAdminError("storage_unavailable");
  } catch (error) {
    if (error instanceof TestRunStoreAdminError) throw error;
    if (Date.now() >= deadlineUnixMs) throw new TestRunStoreAdminError("deadline_exceeded");
    throw new TestRunStoreAdminError("storage_unavailable");
  }
  let result: unknown;
  try { result = JSON.parse(output.replace(/^\uFEFF/, "").trim()); } catch { throw new TestRunStoreAdminError("storage_unavailable"); }
  if (typeof result !== "object" || result === null || !("status" in result)) throw new TestRunStoreAdminError("storage_unavailable");
  if (result.status !== "ok") {
    const code = "code" in result ? result.code : undefined;
    throw new TestRunStoreAdminError(code === "config_changed" ? "config_changed" : code === "preview_changed" ? "preview_changed" : "storage_unavailable");
  }
}

const LINUX_CONFIG_COMMIT_SCRIPT = String.raw`
import fs from 'node:fs'; import path from 'node:path'; import crypto from 'node:crypto';
let temp, held;
const hash=b=>'sha256:'+crypto.createHash('sha256').update(b).digest('hex');
const same=(a,b)=>a.dev===b.dev&&a.ino===b.ino&&a.size===b.size&&a.mtimeNs===b.mtimeNs&&a.ctimeNs===b.ctimeNs&&b.nlink===1n;
try {
 const q=JSON.parse(fs.readFileSync(0,'utf8'));
 const root=fs.fstatSync(3,{bigint:true});
 const checkRoot=()=>{const s=fs.lstatSync(q.root,{bigint:true});if(!s.isDirectory()||s.isSymbolicLink()||s.dev!==root.dev||s.ino!==root.ino||root.dev.toString()!==q.root_dev||root.ino.toString()!==q.root_ino)throw Error('config_changed');};
 checkRoot(); const name=path.basename(q.config.path); if(path.join(q.root,name)!==q.config.path)throw Error('config_changed');
 const file='/proc/self/fd/3/'+name;
 const lock=fs.fstatSync(4,{bigint:true}), namedLock=fs.lstatSync(file+'.settings-write.lock',{bigint:true});
 if(!lock.isFile()||lock.nlink!==1n||!same(lock,namedLock))throw Error('config_changed');
 const before=fs.lstatSync(file,{bigint:true}); held=fs.openSync(file,fs.constants.O_RDONLY|fs.constants.O_NOFOLLOW);
 const opened=fs.fstatSync(held,{bigint:true});
 if(!before.isFile()||before.isSymbolicLink()||!same(before,opened)||opened.size>1048576n||opened.dev.toString()!==q.config.dev||opened.ino.toString()!==q.config.ino||opened.mtimeNs.toString()!==q.config.mtime_ns||opened.ctimeNs.toString()!==q.config.ctime_ns)throw Error('config_changed');
 const bytes=Buffer.alloc(Number(opened.size)+1); let n=0; while(n<bytes.length){const r=fs.readSync(held,bytes,n,bytes.length-n,null);if(!r)break;n+=r;}
 if(n!==Number(opened.size)||hash(bytes.subarray(0,n))!==q.config.content_hash||bytes.subarray(0,n).toString('base64')!==q.original_base64)throw Error('config_changed');
 const updated=Buffer.from(q.updated_base64,'base64');if(!updated.length||updated.length>1048576)throw Error('config_changed');
 if(q.original_base64!==q.updated_base64){
  temp='/proc/self/fd/3/.test-run-storage-'+crypto.randomUUID()+'.tmp'; const fd=fs.openSync(temp,fs.constants.O_WRONLY|fs.constants.O_CREAT|fs.constants.O_EXCL|fs.constants.O_NOFOLLOW,Number(opened.mode&511n));
  try{fs.writeFileSync(fd,updated);fs.fsyncSync(fd);}finally{fs.closeSync(fd);}
  checkRoot();if(!same(opened,fs.fstatSync(held,{bigint:true}))||!same(before,fs.lstatSync(file,{bigint:true})))throw Error('config_changed');
  // Compared file and parent descriptor stay held through the atomic rename. No copy/overwrite fallback.
  fs.renameSync(temp,file);temp=undefined;fs.fsyncSync(3);checkRoot();
 }
 console.log(JSON.stringify({status:'ok'}));
}catch(e){console.log(JSON.stringify({status:'error',code:e.message==='config_changed'?'config_changed':'storage_unavailable'}));}
finally{if(held!==undefined)fs.closeSync(held);if(temp){try{fs.unlinkSync(temp);}catch{}}}
`;

const WINDOWS_STORAGE_COMMIT_SCRIPT = String.raw`
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
try {
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Text; using System.Collections.Generic; using System.Security.Cryptography; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class SggStorageCommit {
 [StructLayout(LayoutKind.Sequential)] struct Time { public uint Low,High; }
 [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attributes; public Time Creation,Access,Write; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
 [StructLayout(LayoutKind.Sequential)] struct Basic { public long Creation,Access,Write,Change; public uint Attributes; }
 [StructLayout(LayoutKind.Sequential)] struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle,out Info info);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out Basic info,uint size);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref Disposition info,uint size);
 public sealed class Evidence { public string Path,Dev,Ino,Size,Mtime,Ctime,Hash; }
 static readonly List<SafeFileHandle> pins=new List<SafeFileHandle>();
 static readonly Dictionary<string,SafeFileHandle> directories=new Dictionary<string,SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
 static Info Inspect(SafeFileHandle h){Info i;if(h.IsInvalid||!GetFileInformationByHandle(h,out i))throw new IOException("storage_unavailable");return i;}
 static string Inode(Info i){return (((ulong)i.IndexHigh<<32)|i.IndexLow).ToString();}
 static string Hash(byte[] b){using(var h=SHA256.Create())return "sha256:"+BitConverter.ToString(h.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
 static SafeFileHandle PinDirectory(string name){
  name=System.IO.Path.GetFullPath(name);if(name.Length>4096||name.Length<3||name[1]!=':'||name[2]!='\\')throw new IOException("storage_unavailable");
  SafeFileHandle existing;if(directories.TryGetValue(name,out existing))return existing;
  var parent=System.IO.Path.GetDirectoryName(name.TrimEnd('\\'));if(!String.IsNullOrEmpty(parent)&&!String.Equals(parent,name,StringComparison.OrdinalIgnoreCase))PinDirectory(parent);
  var h=CreateFileW(name,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);var i=Inspect(h);
  if((i.Attributes&0x10)==0||(i.Attributes&0x400)!=0){h.Dispose();throw new IOException("storage_unavailable");}
  pins.Add(h);directories[name]=h;return h;
 }
 static FileStream Open(Evidence e,uint share,string failure,bool deletion=false){
  if(e.Path.Length>4096||!System.IO.Path.IsPathRooted(e.Path))throw new IOException(failure);PinDirectory(System.IO.Path.GetDirectoryName(e.Path));
  var h=CreateFileW(e.Path,deletion?0x80010000u:0x80000000u,share,IntPtr.Zero,3,0x00200000,IntPtr.Zero);var i=Inspect(h);Basic b;
  if((i.Attributes&0x410)!=0||i.Links!=1||Inode(i)!=e.Ino||(e.Dev!="0"&&i.Volume.ToString()!=e.Dev)||!GetFileInformationByHandleEx(h,0,out b,(uint)Marshal.SizeOf(typeof(Basic)))
   ||((b.Write-116444736000000000L)*100L).ToString()!=e.Mtime||((b.Change-116444736000000000L)*100L).ToString()!=e.Ctime){h.Dispose();throw new IOException(failure);}
  var s=new FileStream(h,FileAccess.Read);if(s.Length.ToString()!=e.Size||s.Length<1||s.Length>1048576){s.Dispose();throw new IOException(failure);}
  var bytes=new byte[(int)s.Length];int n=0,r;while(n<bytes.Length&&(r=s.Read(bytes,n,bytes.Length-n))>0)n+=r;
  if(n!=bytes.Length||Hash(bytes)!=e.Hash){s.Dispose();throw new IOException(failure);}s.Position=0;return s;
 }
 static FileStream WriterLock(string config){
  var name=config+".settings-write.lock";PinDirectory(System.IO.Path.GetDirectoryName(name));
  // The exact persistent C# sidecar and FileShare.None/native share=0, not a second lock domain.
  var h=CreateFileW(name,0xC0000000,0,IntPtr.Zero,4,0x00200000,IntPtr.Zero);var i=Inspect(h);
  if((i.Attributes&0x410)!=0||i.Links!=1){h.Dispose();throw new IOException("config_changed");}return new FileStream(h,FileAccess.ReadWrite);
 }
 public static void Configure(string root,string dev,string ino,Evidence config,string original,string updated){
  string temp=null;try{
   var directory=Inspect(PinDirectory(root));if(Inode(directory)!=ino||(dev!="0"&&directory.Volume.ToString()!=dev))throw new IOException("config_changed");
   using(var writer=WriterLock(config.Path))using(var held=Open(config,5,"config_changed")){
    var bytes=Convert.FromBase64String(updated);if(bytes.Length<1||bytes.Length>1048576)throw new IOException("config_changed");
    if(Hash(Convert.FromBase64String(original))!=config.Hash)throw new IOException("config_changed");
    if(original==updated)return;
    temp=System.IO.Path.Combine(root,".test-run-storage-"+Guid.NewGuid().ToString("N")+".tmp");
    using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(bytes,0,bytes.Length);f.Flush(true);}
    // Shared writer lock excludes cooperating settings writers; the held destination denies
    // in-place writes through replacement. Delete sharing is required by File.Replace and
    // is not universal CAS against an uncoordinated external pathname replacement.
    using(var verify=Open(config,5,"config_changed")){File.Replace(temp,config.Path,null);}temp=null;
   }
  }finally{if(temp!=null&&File.Exists(temp))File.Delete(temp);Close();}
 }
 public static void Cleanup(string root,string dev,string ino,Evidence owner,Evidence[] metadata,Evidence[] artifacts,Evidence config){
  var held=new List<FileStream>();try{
   var directory=Inspect(PinDirectory(root));if(Inode(directory)!=ino||(dev!="0"&&directory.Volume.ToString()!=dev))throw new IOException("preview_changed");
   held.Add(Open(owner,1,"preview_changed"));if(config!=null)held.Add(Open(config,1,"config_changed"));
   if(metadata.Length>768||artifacts.Length>256)throw new IOException("preview_changed");
   foreach(var e in artifacts)held.Add(Open(e,1,"preview_changed"));
   foreach(var e in metadata){if(System.IO.Path.GetDirectoryName(e.Path)!=root)throw new IOException("preview_changed");held.Add(Open(e,5,"preview_changed",true));}
   // Every exact object is validated and held before any metadata deletion. Artifacts/config stay untouched.
   int start=held.Count-metadata.Length;
   for(int i=0;i<metadata.Length;i++){
    // Delete the exact opened metadata object, never a replacement at its pathname.
    using(var verify=Open(metadata[i],5,"preview_changed")){
     var disposition=new Disposition{Delete=true};if(!SetFileInformationByHandle(held[start+i].SafeFileHandle,4,ref disposition,(uint)Marshal.SizeOf(typeof(Disposition))))throw new IOException("storage_unavailable");
    }
   }
  }finally{foreach(var h in held)h.Dispose();Close();}
 }
 static void Close(){for(int i=pins.Count-1;i>=0;i--)pins[i].Dispose();pins.Clear();directories.Clear();}
}
'@
function Evidence($e){if($null -eq $e){return $null};$v=New-Object SggStorageCommit+Evidence;$v.Path=$e.path;$v.Dev=$e.dev;$v.Ino=$e.ino;$v.Size=$e.size;$v.Mtime=$e.mtime_ns;$v.Ctime=$e.ctime_ns;$v.Hash=$e.content_hash;return $v}
$q=[Console]::In.ReadToEnd()|ConvertFrom-Json
if($q.operation -eq 'configure'){[SggStorageCommit]::Configure($q.root,$q.root_dev,$q.root_ino,(Evidence $q.config),$q.original_base64,$q.updated_base64)}
elseif($q.operation -eq 'cleanup'){$m=@($q.metadata|ForEach-Object{Evidence $_});$a=@($q.artifacts|ForEach-Object{Evidence $_});[SggStorageCommit]::Cleanup($q.root,$q.root_dev,$q.root_ino,(Evidence $q.lock),$m,$a,(Evidence $q.config))}
else{throw 'storage_unavailable'}
[Console]::WriteLine('{"status":"ok"}')
}catch{$c='storage_unavailable';$message=$_.Exception.ToString();if($message.Contains('config_changed')){$c='config_changed'}elseif($message.Contains('preview_changed')){$c='preview_changed'};[Console]::WriteLine('{"status":"error","code":"'+$c+'"}')}
`;
