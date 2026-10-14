using System.Runtime.InteropServices;
using System.Text;
using QuotaMonitor.Core.Adapters;
namespace QuotaMonitor.Security;
// Generic credential belongs to this application only; no Harness credentials are inspected.
public sealed class CredentialStore(string target="QuotaMonitor/ClaudeCN/SystemAccessToken") : ISecretStore
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Credential
    {
        public uint Flags,Type;public string? TargetName,Comment;public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;public uint CredentialBlobSize;public IntPtr CredentialBlob;public uint Persist,AttributeCount;public IntPtr Attributes;public string? TargetAlias,UserName;
    }
    [DllImport("advapi32",EntryPoint="CredReadW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CredRead(string target,uint type,uint reserved,out IntPtr credential);
    [DllImport("advapi32",EntryPoint="CredWriteW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CredWrite(ref Credential credential,uint flags);
    [DllImport("advapi32",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CredDelete(string target,uint type,uint flags);
    [DllImport("advapi32")] private static extern void CredFree(IntPtr credential);
    public string? Read(){if(!CredRead(target,1,0,out var ptr)){if(Marshal.GetLastWin32Error()==1168)return null;throw new System.ComponentModel.Win32Exception();}try{var c=Marshal.PtrToStructure<Credential>(ptr);return Marshal.PtrToStringUni(c.CredentialBlob,(int)c.CredentialBlobSize/2);}finally{CredFree(ptr);}}
    public void Write(string value){var bytes=Encoding.Unicode.GetBytes(value);if(bytes.Length>2560)throw new ArgumentException("Token 过长。");var ptr=Marshal.AllocHGlobal(bytes.Length);try{Marshal.Copy(bytes,0,ptr,bytes.Length);var c=new Credential{Type=1,TargetName=target,CredentialBlobSize=(uint)bytes.Length,CredentialBlob=ptr,Persist=2,UserName="ClaudeCN"};if(!CredWrite(ref c,0))throw new System.ComponentModel.Win32Exception();}finally{Marshal.Copy(new byte[bytes.Length],0,ptr,bytes.Length);Marshal.FreeHGlobal(ptr);Array.Clear(bytes);}}
    public void Delete(){if(!CredDelete(target,1,0)&&Marshal.GetLastWin32Error()!=1168)throw new System.ComponentModel.Win32Exception();}
}
