using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.VendorInstallers;

[SupportedOSPlatform("windows")]
public sealed class AuthenticodeFileSignatureVerifier : IFileSignatureVerifier
{
    private static readonly Guid GenericVerifyV2Action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private readonly ILogger<AuthenticodeFileSignatureVerifier> _logger;

    public AuthenticodeFileSignatureVerifier(ILogger<AuthenticodeFileSignatureVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public FileSignatureVerification Verify(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return new FileSignatureVerification(false, null, null, "File does not exist.");
        }

        var fileInfo = new WinTrustFileInfo(filePath);
        var data = new WinTrustData(fileInfo);
        var fileInfoPointer = IntPtr.Zero;
        var dataPointer = IntPtr.Zero;

        try
        {
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            data.FileInfoPointer = fileInfoPointer;

            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);

            var status = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2Action, dataPointer);
            if (status != 0)
            {
                var message = DescribeTrustStatus(status);
                _logger.LogWarning(
                    "Authenticode verification rejected {Path}: status 0x{Status:X8} ({Message})",
                    filePath,
                    status,
                    message);
                return new FileSignatureVerification(false, null, null, message);
            }

            try
            {
#pragma warning disable SYSLIB0057 // This API extracts the embedded Authenticode signer after WinVerifyTrust validates the file.
                using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                return new FileSignatureVerification(
                    true,
                    certificate.Subject,
                    certificate.GetCertHashString(),
                    null);
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning(ex, "WinVerifyTrust accepted {Path}, but its signer certificate could not be read", filePath);
                return new FileSignatureVerification(false, null, null, "The signer certificate could not be read.");
            }
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                var closeData = Marshal.PtrToStructure<WinTrustData>(dataPointer);
                closeData.StateAction = WinTrustDataStateAction.Close;
                Marshal.StructureToPtr(closeData, dataPointer, fDeleteOld: true);
                _ = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2Action, dataPointer);
                Marshal.DestroyStructure<WinTrustData>(dataPointer);
                Marshal.FreeHGlobal(dataPointer);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }
    }

    private static string DescribeTrustStatus(int status) => status switch
    {
        unchecked((int)0x800B0100) => "The file has no Authenticode signature.",
        unchecked((int)0x80096010) => "The file content does not match its digital signature.",
        unchecked((int)0x800B0109) => "The signing certificate chain is not trusted.",
        unchecked((int)0x800B0101) => "The signing certificate is expired or not yet valid.",
        unchecked((int)0x80092010) => "The signing certificate is revoked.",
        _ => new Win32Exception(status).Message
    };

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string filePath)
        {
            StructSize = Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public int StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public WinTrustDataUiChoice UiChoice;
        public WinTrustDataRevocationChecks RevocationChecks;
        public WinTrustDataChoice UnionChoice;
        public IntPtr FileInfoPointer;
        public WinTrustDataStateAction StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public WinTrustDataProviderFlags ProviderFlags;
        public int UiContext;

        public WinTrustData(WinTrustFileInfo _)
        {
            StructSize = Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = WinTrustDataUiChoice.None;
            RevocationChecks = WinTrustDataRevocationChecks.WholeChain;
            UnionChoice = WinTrustDataChoice.File;
            FileInfoPointer = IntPtr.Zero;
            StateAction = WinTrustDataStateAction.Verify;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = WinTrustDataProviderFlags.RevocationCheckChainExcludeRoot;
            UiContext = 0;
        }
    }

    private enum WinTrustDataUiChoice : uint
    {
        None = 2
    }

    private enum WinTrustDataRevocationChecks : uint
    {
        WholeChain = 1
    }

    private enum WinTrustDataChoice : uint
    {
        File = 1
    }

    private enum WinTrustDataStateAction : uint
    {
        Verify = 1,
        Close = 2
    }

    [Flags]
    private enum WinTrustDataProviderFlags : uint
    {
        RevocationCheckChainExcludeRoot = 0x00000080
    }
}
