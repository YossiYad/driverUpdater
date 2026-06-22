# Release checklist

DriverUpdater performs privileged driver installation. Public releases should meet
every item below before uploading artifacts.

## Supported systems

- Windows 10 and Windows 11.
- Native packages are produced for x64, ARM64, and x86.
- Windows Update and Microsoft Update Catalog are the primary cross-architecture
  sources.
- NVIDIA and Gigabyte direct vendor packages are enabled only on x64 because those
  vendors do not provide equivalent Windows ARM64 or current x86 packages through
  the endpoints used by the app.

## Build and verification

1. Pick a semantic version and update `Version`, `AssemblyVersion`, and
   `FileVersion` in `Directory.Build.props`.
2. Configure a trusted code-signing certificate through `VELOPACK_SIGN_PARAMS`.
3. Run `build\release-all.cmd <version>`.
4. Confirm all tests, the NuGet vulnerability audit, and all three Velopack builds
   succeed.
5. Verify each `SHA256SUMS.txt` file and retain it with the uploaded artifacts.
6. Install and smoke-test the x64 package on Windows 10 and Windows 11.
7. Install and smoke-test the ARM64 package on physical Windows on ARM hardware.
8. Test the x86 package on 32-bit Windows when that package is offered publicly.
9. Confirm the binaries and setup executables have a valid Authenticode signature.
10. Upload each runtime to a separate release directory or clearly label it.

## Publication gate

Do not describe an unsigned setup executable as a public production release.
Unsigned builds trigger SmartScreen warnings and do not establish publisher
identity. They are suitable for internal testing only.
