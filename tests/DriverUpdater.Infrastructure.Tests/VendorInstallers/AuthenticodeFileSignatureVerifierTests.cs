using DriverUpdater.Infrastructure.VendorInstallers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.VendorInstallers;

public class AuthenticodeFileSignatureVerifierTests
{
    [Fact]
    public void Verify_rejects_unsigned_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x00]);
        try
        {
            var verifier = new AuthenticodeFileSignatureVerifier(
                NullLogger<AuthenticodeFileSignatureVerifier>.Instance);

            var result = verifier.Verify(path);

            result.IsTrusted.Should().BeFalse();
            result.Publisher.Should().BeNull();
            result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Verify_rejects_missing_file_without_native_call()
    {
        var verifier = new AuthenticodeFileSignatureVerifier(
            NullLogger<AuthenticodeFileSignatureVerifier>.Instance);

        var result = verifier.Verify(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));

        result.IsTrusted.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not exist");
    }
}
