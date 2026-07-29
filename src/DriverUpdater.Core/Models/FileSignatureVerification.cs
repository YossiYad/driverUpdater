namespace DriverUpdater.Core.Models;

public sealed record FileSignatureVerification(
    bool IsTrusted,
    string? Publisher,
    string? CertificateThumbprint,
    string? ErrorMessage);
