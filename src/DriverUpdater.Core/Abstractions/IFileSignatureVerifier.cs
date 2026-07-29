using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IFileSignatureVerifier
{
    FileSignatureVerification Verify(string filePath);
}
