namespace DriverUpdater.Core.Models;

public enum VendorPageResolutionKind
{
    Installer,
    NoPackageFound,
    PageUnreachable
}

// A vendor page that cannot be reached at all (a fetch that fails outright, including a real
// browser render) says something different than a page that renders fine but lists no
// installable file: the former usually means the lead itself is bad (an invented URL, a moved
// support article), while the latter means the page is real but needs a dedicated scraper to
// go further. Callers use this to decide whether to keep offering the vendor site or drop the
// lead entirely.
public sealed record VendorPageResolution(VendorPageResolutionKind Kind, UpdateCandidate? Candidate = null)
{
    public static VendorPageResolution Installer(UpdateCandidate candidate) => new(VendorPageResolutionKind.Installer, candidate);
    public static readonly VendorPageResolution NoPackageFound = new(VendorPageResolutionKind.NoPackageFound);
    public static readonly VendorPageResolution PageUnreachable = new(VendorPageResolutionKind.PageUnreachable);
}
