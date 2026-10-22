using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using WinBox.Core.Import;

namespace WinBox.Platform.Import;

/// <summary>Reads PE version resources and verifies Authenticode signatures with WinVerifyTrust.</summary>
public sealed class WindowsFileInspector : IFileInspector
{
    public ExecutableInfo? ReadExecutableInfo(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            var info = new ExecutableInfo(Clean(v.OriginalFilename), Clean(v.FileDescription), Clean(v.ProductName), Clean(v.CompanyName), Clean(v.ProductVersion));
            return info is { OriginalFilename: null, FileDescription: null, ProductName: null, CompanyName: null, ProductVersion: null } ? null : info;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public SignatureInfo ReadSignature(string path)
    {
        var valid = VerifyTrust(path);
        string? subject = null;
        try
        {
#pragma warning disable SYSLIB0057 // Only reading the embedded signer certificate's subject; trust is checked by WinVerifyTrust above.
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Unsigned file.
        }
        return new SignatureInfo(valid, subject);
    }

    private static bool VerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
            };
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
