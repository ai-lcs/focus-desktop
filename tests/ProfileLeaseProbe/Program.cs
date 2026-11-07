using System.Diagnostics;
using focus_desktop.Services;

if (args is ["--attempt", var target])
{
    if (!ProfileUsageLease.TryAcquire(target, out var lease, out _)) return 3;
    lease!.Dispose();
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "focus-profile-lease-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var profile = Path.Combine(root, "profile");
var failures = 0;
void Check(string label, bool value)
{
    Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}");
    if (!value) failures++;
}
int ChildAttempt(string path)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--attempt");
    start.ArgumentList.Add(path);
    using var process = Process.Start(start)!;
    if (!process.WaitForExit(10000)) throw new TimeoutException("Profile lease probe child did not exit.");
    return process.ExitCode;
}

try
{
    Check("first owner obtains profile guard", ProfileUsageLease.TryAcquire(profile, out var owner, out _));
    using (owner)
    {
        Check("another process cannot start with the same profile", ChildAttempt(profile) == 3);
        Check("case spelling does not bypass the profile guard", ChildAttempt(profile.ToUpperInvariant()) == 3);
        Check("independent profile remains available", ChildAttempt(Path.Combine(root, "other-profile")) == 0);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "synthetic-data.txt"), "synthetic only");
        Directory.Delete(profile, recursive: true);
        Check("guard survives actual profile deletion", ChildAttempt(profile) == 3);
        Check("startup cannot recreate data while deletion owner holds guard", !Directory.Exists(profile));
    }
    Check("profile becomes available only after cleanup owner releases", ChildAttempt(profile) == 0);
}
finally
{
    // This unique directory contains synthetic probe files only.
    Directory.Delete(root, recursive: true);
}
return failures == 0 ? 0 : 1;
