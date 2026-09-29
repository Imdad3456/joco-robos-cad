using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace JocoRobos.Cad
{
    internal static class CredentialStore
    {
        // One login covers every season and the library. The name predates multiple seasons;
        // it is kept so existing sign-ins keep working.
        private const string Target = "JOCO ROBOS CAD:https://cad.imdad.stream/svn/2027-Robot";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credential);

        internal static NetworkCredential Read()
        {
            IntPtr pointer;
            if (!CredRead(Target, 1, 0, out pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1168) return null;
                throw new Win32Exception(error, "Could not read Windows Credential Manager.");
            }
            try
            {
                var value = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
                string password = Marshal.PtrToStringUni(value.CredentialBlob, checked((int)value.CredentialBlobSize / 2));
                return new NetworkCredential(value.UserName, password);
            }
            finally { CredFree(pointer); }
        }

        internal static void Write(NetworkCredential login)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(login.Password);
            if (bytes.Length > 2560) throw new InvalidOperationException("Password is too long for Credential Manager.");
            IntPtr pointer = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var value = new Credential { Type = 1, TargetName = Target,
                    UserName = login.UserName, CredentialBlob = pointer,
                    CredentialBlobSize = (uint)bytes.Length, Persist = 2 };
                if (!CredWrite(ref value, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save to Windows Credential Manager.");
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }
}
