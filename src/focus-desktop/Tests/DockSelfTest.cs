using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using focus_desktop.Services;

namespace focus_desktop.Tests;

/// <summary>Pure Dock/configuration checks. This mode must not create windows or touch desktop state.</summary>
public static class DockSelfTest
{
    public static int Run()
    {
        var passed = 0;
        var failed = 0;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"focus-desk-dock-selftest-{Guid.NewGuid():N}");

        void Check(string name, bool condition)
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"  PASS {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL {name}");
            }
        }

        void CheckExpectedWriteFailure(string name, Action action)
        {
            try
            {
                action();
                Check(name, false);
            }
            catch (IOException)
            {
                Check(name, true);
            }
            catch (UnauthorizedAccessException)
            {
                Check(name, true);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL {name} (unexpected {ex.GetType().Name})");
            }
        }

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            CheckNativeProcessIdentity(Check);
            Check("Dock installed profile is isolated from the legacy profile",
                Paths.InstalledDataDirectoryName == "focus-desktop-dock");
            CheckFullScreenLayout(Check);
            CheckIdentityMatching(Check);
            Task.Run(() =>
            {
                CheckIntentAndLaunchBoundaries(Check);
                CheckAbandonedLaunchFaultObservation(Check);
            }).GetAwaiter().GetResult();
            CheckRecoveryGate(Check);
            CheckWebViewProfileMatching(Check, temporaryDirectory);
            CheckSettingsAndAtomicSave(Check, CheckExpectedWriteFailure, temporaryDirectory);
            ExternalAppIdentitySelfTest.Run(Check);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  FAIL self-test aborted ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL temporary test data cleanup ({ex.GetType().Name})");
            }
        }

        Console.WriteLine($"== Dock self-test: {passed} pass / {failed} fail ==");
        return failed == 0 ? 0 : 1;
    }

    private static void CheckFullScreenLayout(Action<string, bool> check)
    {
        // Reproduce the screenshot: hidden taskbar still reserves the bottom 96 px
        // on a 2560x1600, 200% desktop. Also check other physical screen/DPI sizes.
        foreach (var (width, height, dockWidth, taskbarHeight) in new[]
        {
            (2560, 1600, 192, 96), (1920, 1080, 96, 48), (1920, 1200, 144, 72),
        })
        {
            var monitor = new DesktopRect(0, 0, width, height);
            var approved = new DesktopRect(0, 0, dockWidth, height - taskbarHeight);
            var work = new DesktopRect(dockWidth, 0, width, height - taskbarHeight);
            var full = AppBarWindowService.CalculateLayout(approved, monitor, work, true);
            check($"full-screen Dock reaches bottom at {width}x{height}/{dockWidth}",
                full.ApprovedBounds == new DesktopRect(0, 0, dockWidth, height));
            check($"full-screen content and Dock cover screen without gap or overlap at {width}x{height}/{dockWidth}",
                full.WorkArea == new DesktopRect(dockWidth, 0, width, height)
                && full.ApprovedBounds.Right == full.WorkArea.Left);
            var development = AppBarWindowService.CalculateLayout(approved, monitor, work, false);
            check($"development mode preserves Shell work area at {width}x{height}/{dockWidth}",
                development == new AppBarLayout(approved, work));
            var topHeight = dockWidth * 44 / 96;
            var top = AppBarWindowService.CalculateLayout(new DesktopRect(0, 0, width, topHeight),
                monitor, new DesktopRect(dockWidth, topHeight, width, height - taskbarHeight), true, true);
            check($"top bar excludes left Dock and main excludes both bars at {width}x{height}",
                top.ApprovedBounds == new DesktopRect(dockWidth, 0, width, topHeight)
                && top.WorkArea == new DesktopRect(dockWidth, topHeight, width, height));
        }
    }

    private static void CheckNativeProcessIdentity(Action<string, bool> check)
    {
        var nativeMethod = typeof(ExternalAppWindowService).GetMethod(
            "OpenProcess", BindingFlags.NonPublic | BindingFlags.Static);
        var import = nativeMethod?.GetCustomAttribute<DllImportAttribute>();
        var importedMethods = new[] { typeof(ExternalAppWindowService), typeof(AppBarWindowService), typeof(ExternalWindowLayout), typeof(WindowTransitions), typeof(TaskbarService), typeof(DockWindow) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .ToList();
        var unresolvedImports = importedMethods
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not { } nativeImport
                || !TryResolveNativeEntryPoint(method, nativeImport, out _))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToList();
        check("Dock native imports resolve with their declared entry-point rules", unresolvedImports.Count == 0);

        check("OpenProcess DllImport uses kernel32.dll",
            import != null
            && string.Equals(Path.GetFileName(import.Value), "kernel32.dll", StringComparison.OrdinalIgnoreCase));

        try
        {
            var identityReader = typeof(ExternalAppWindowService).GetMethod(
                "GetProcessIdentity", BindingFlags.NonPublic | BindingFlags.Static);
            var identity = identityReader?.Invoke(null, new object[] { checked((uint)Environment.ProcessId) });
            var actualPath = identity is ExternalWindowIdentity processIdentity
                ? processIdentity.ExecutablePath
                : null;
            var expectedPath = Environment.ProcessPath;
            check("native process identity query resolves this executable",
                !string.IsNullOrWhiteSpace(actualPath)
                && !string.IsNullOrWhiteSpace(expectedPath)
                && string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            var cause = ex is TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException
                : ex;
            check($"native process identity query completes ({cause!.GetType().Name})", false);
        }
    }

    private static bool TryResolveNativeEntryPoint(
        MethodInfo method,
        DllImportAttribute import,
        out string resolvedEntryPoint)
    {
        resolvedEntryPoint = "";
        if (!NativeLibrary.TryLoad(import.Value, out var library)) return false;
        try
        {
            var requestedName = string.IsNullOrWhiteSpace(import.EntryPoint) ? method.Name : import.EntryPoint;
            if (import.ExactSpelling)
            {
                if (!NativeLibrary.TryGetExport(library, requestedName, out _)) return false;
                resolvedEntryPoint = requestedName;
                return true;
            }

            var suffix = import.CharSet switch
            {
                CharSet.Ansi or CharSet.None => 'A',
                CharSet.Unicode => 'W',
                CharSet.Auto => OperatingSystem.IsWindows() ? 'W' : 'A',
                _ => '\0',
            };
            var suffixedName = suffix == '\0' ? null : requestedName + suffix;
            var suffixedFirst = import.CharSet is CharSet.Unicode or CharSet.Auto;
            if (suffixedFirst && suffixedName != null
                && NativeLibrary.TryGetExport(library, suffixedName, out _))
            {
                resolvedEntryPoint = suffixedName;
                return true;
            }

            if (NativeLibrary.TryGetExport(library, requestedName, out _))
            {
                resolvedEntryPoint = requestedName;
                return true;
            }

            if (!suffixedFirst && suffixedName != null
                && NativeLibrary.TryGetExport(library, suffixedName, out _))
            {
                resolvedEntryPoint = suffixedName;
                return true;
            }

            return false;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static void CheckIdentityMatching(Action<string, bool> check)
    {
        var packagedEntry = new ExternalAppEntry
        {
            Id = "packaged:test",
            DisplayName = "Packaged test app",
            Kind = ExternalAppEntry.PackagedAppKind,
            AppUserModelId = "Vendor.TestApp_123!Main",
            Order = 0
        };
        check("packaged identity matches AUMID case-insensitively",
            ExternalAppWindowService.IdentityMatches(packagedEntry,
                new ExternalWindowIdentity("vendor.testapp_123!main", @"C:\ignored\same.exe")));
        check("packaged identity rejects a different AUMID",
            !ExternalAppWindowService.IdentityMatches(packagedEntry,
                new ExternalWindowIdentity("Vendor.Other_123!Main", null)));
        check("packaged identity does not fall back to executable path",
            !ExternalAppWindowService.IdentityMatches(packagedEntry,
                new ExternalWindowIdentity(null, @"C:\ignored\same.exe")));

        var executablePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "中文 路径", "编辑器.exe"));
        var executableEntry = new ExternalAppEntry
        {
            Id = "exe:test",
            DisplayName = "Executable test app",
            Kind = ExternalAppEntry.ExecutableKind,
            ExecutablePath = executablePath,
            Order = 0
        };
        var equivalentPath = Path.Combine(Path.GetDirectoryName(executablePath)!, ".", Path.GetFileName(executablePath));
        check("Win32 identity uses normalized case-insensitive executable path",
            ExternalAppWindowService.IdentityMatches(executableEntry,
                new ExternalWindowIdentity(null, equivalentPath.ToUpperInvariant())));
        check("window title/process name is not an identity substitute",
            !ExternalAppWindowService.IdentityMatches(executableEntry,
                new ExternalWindowIdentity(null, @"C:\other\different.exe")));

        check("owned modal identity matches through owner chain",
            ExternalAppWindowService.MatchesOwnerChain(executableEntry, new[]
            {
                new ExternalWindowIdentity(null, @"C:\other\launcher.exe"),
                new ExternalWindowIdentity(null, equivalentPath)
            }));
        check("unrelated owner chain does not match",
            !ExternalAppWindowService.MatchesOwnerChain(executableEntry, new[]
            {
                new ExternalWindowIdentity(null, @"C:\other\launcher.exe")
            }));

        var workingDirectory = Path.GetDirectoryName(executablePath)!;
        var frozen = new ExternalAppEntry
        {
            Id = ExternalAppEntry.CreateWin32StableId(executablePath, "--safe-mode", workingDirectory),
            DisplayName = "Frozen shortcut entry",
            Kind = ExternalAppEntry.ShellLinkKind,
            ShortcutPath = Path.Combine(workingDirectory, "选定的快捷方式.lnk"),
            ExecutablePath = executablePath,
            Arguments = "--safe-mode",
            WorkingDirectory = workingDirectory,
            Order = 0
        };
        var sameDefinition = new ExternalAppEntry
        {
            Id = frozen.Id,
            DisplayName = frozen.DisplayName,
            Kind = frozen.Kind,
            ShortcutPath = frozen.ShortcutPath,
            ExecutablePath = executablePath,
            Arguments = "--safe-mode",
            WorkingDirectory = workingDirectory,
            Order = 0
        };
        check("unchanged shortcut definition remains valid", ExternalAppCatalog.IsSameFrozenLaunchDefinition(frozen, sameDefinition));
        var changedArguments = new ExternalAppEntry
        {
            Id = ExternalAppEntry.CreateWin32StableId(executablePath, "--unsafe", workingDirectory),
            DisplayName = frozen.DisplayName,
            Kind = frozen.Kind,
            ShortcutPath = frozen.ShortcutPath,
            ExecutablePath = executablePath,
            Arguments = "--unsafe",
            WorkingDirectory = workingDirectory,
            Order = 0
        };
        check("changed shortcut arguments are rejected", !ExternalAppCatalog.IsSameFrozenLaunchDefinition(frozen, changedArguments));
        var changedArgumentsWithOldId = new ExternalAppEntry
        {
            Id = frozen.Id,
            DisplayName = frozen.DisplayName,
            Kind = frozen.Kind,
            ShortcutPath = frozen.ShortcutPath,
            ExecutablePath = executablePath,
            Arguments = "--changed",
            WorkingDirectory = workingDirectory,
            Order = 0
        };
        check("arguments changed without updating the frozen ID are invalid",
            !changedArgumentsWithOldId.TryValidate(out _));
        var changedWorkingDirectory = new ExternalAppEntry
        {
            Id = ExternalAppEntry.CreateWin32StableId(executablePath, "--safe-mode", @"C:\other\working directory"),
            DisplayName = frozen.DisplayName,
            Kind = frozen.Kind,
            ShortcutPath = frozen.ShortcutPath,
            ExecutablePath = executablePath,
            Arguments = "--safe-mode",
            WorkingDirectory = @"C:\other\working directory",
            Order = 0
        };
        check("changed shortcut working directory is rejected",
            !ExternalAppCatalog.IsSameFrozenLaunchDefinition(frozen, changedWorkingDirectory));
        var changedTargetWithOldId = new ExternalAppEntry
        {
            Id = frozen.Id,
            DisplayName = frozen.DisplayName,
            Kind = frozen.Kind,
            ShortcutPath = frozen.ShortcutPath,
            ExecutablePath = Path.Combine(workingDirectory, "another.exe"),
            Arguments = frozen.Arguments,
            WorkingDirectory = workingDirectory,
            Order = 0
        };
        check("changed shortcut target is rejected even if its frozen ID is retained",
            !ExternalAppCatalog.IsSameFrozenLaunchDefinition(frozen, changedTargetWithOldId));
    }

    private static void CheckIntentAndLaunchBoundaries(Action<string, bool> check)
    {
        var gate = new DockSwitchIntentGate();
        var firstIntent = gate.Begin("entry-a");
        check("latest Dock intent is current", gate.IsCurrent(firstIntent));
        var secondIntent = gate.Begin("entry-b");
        check("new Dock intent supersedes an earlier request", !gate.IsCurrent(firstIntent) && gate.IsCurrent(secondIntent));
        gate.Cancel();
        check("cancel invalidates the outstanding Dock intent", !gate.IsCurrent(secondIntent));

        var launches = new DockLaunchRequests();
        var completion = new TaskCompletionSource<IntPtr?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        Task<IntPtr?> Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            return completion.Task;
        }

        var firstWaiter = launches.GetOrStart("entry-a", Factory);
        var abandonedWaiter = launches.GetOrStart("entry-a", Factory);
        check("concurrent launch requests share one pending task",
            ReferenceEquals(firstWaiter, abandonedWaiter));

        // One caller may abandon the task after switching to another app. Complete the
        // shared operation with a fault, then check the request can be retried cleanly.
        completion.SetException(new InvalidOperationException("expected test fault"));
        try { firstWaiter.GetAwaiter().GetResult(); } catch (InvalidOperationException) { }
        check("abandoned caller does not cancel the shared launch request", factoryCalls == 1);

        var retryCompletion = Task.FromResult<IntPtr?>(new IntPtr(42));
        var retry = launches.GetOrStart("entry-a", () => retryCompletion);
        check("failed shared launch request can be retried", retry.GetAwaiter().GetResult() == new IntPtr(42));
    }

    private static void CheckAbandonedLaunchFaultObservation(Action<string, bool> check)
    {
        const string faultMessage = "abandoned launch fault observation test";
        var unobservedFaults = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception.Flatten().InnerExceptions.Any(exception => exception.Message == faultMessage))
            {
                Interlocked.Increment(ref unobservedFaults);
                args.SetObserved();
            }
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var abandoned = StartAbandonedRequest(faultMessage);
            for (var attempt = 0; attempt < 4 && abandoned.IsAlive; attempt++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Yield();
            }

            check("abandoned launch fault is observed and its task can be collected",
                unobservedFaults == 0 && !abandoned.IsAlive);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        static WeakReference StartAbandonedRequest(string expectedFaultMessage)
        {
            var requests = new DockLaunchRequests();
            var task = requests.GetOrStart("abandoned-entry", () =>
                Task.FromException<IntPtr?>(new InvalidOperationException(expectedFaultMessage)));
            var weakTask = new WeakReference(task);
            if (!SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Launch request did not finish in the self-test.");
            return weakTask;
        }
    }

    private static void CheckRecoveryGate(Action<string, bool> check)
    {
        check("recovery remains dirty until the taskbar is restored", !FocusModeService.CanMarkRecoveryClean(false, true));
        check("watchdog cannot clear recovery while Dock/AppBar release is unknown",
            !FocusModeService.CanMarkRecoveryClean(true, false));
        check("recovery clears only after taskbar and Dock recovery", FocusModeService.CanMarkRecoveryClean(true, true));

        check("AppBar recovery requires the parent process to exit",
            !RecoveryService.CanConfirmAppBarReleased(false, true, true));
        check("AppBar recovery requires the original HWND to be destroyed",
            !RecoveryService.CanConfirmAppBarReleased(true, false, true));
        check("AppBar recovery requires the original work area to return",
            !RecoveryService.CanConfirmAppBarReleased(true, true, false));
        check("AppBar recovery is confirmed only when every probe agrees",
            RecoveryService.CanConfirmAppBarReleased(true, true, true));
        check("owned AppBar recovery requires the session owner to match",
            !RecoveryService.CanConfirmOwnedAppBarReleased(false, true, true));
        check("owned AppBar recovery requires the original HWND to be destroyed",
            !RecoveryService.CanConfirmOwnedAppBarReleased(true, false, true));
        check("owned AppBar recovery requires the original work area to return",
            !RecoveryService.CanConfirmOwnedAppBarReleased(true, true, false));
        check("owned AppBar recovery passes only when every probe agrees",
            RecoveryService.CanConfirmOwnedAppBarReleased(true, true, true));
        check("only a metadata-free legacy marker uses taskbar-only recovery",
            RecoveryService.CanUseLegacyTaskbarRecovery(null, false));
        check("new recovery metadata cannot fall back to legacy taskbar-only recovery",
            !RecoveryService.CanUseLegacyTaskbarRecovery(null, true));
        check("a no-AppBar marker rejects a conflicting HWND",
            !RecoveryService.CanConfirmNoAppBarRecovery(true, true));
        check("a no-AppBar marker requires complete parent identity",
            !RecoveryService.CanConfirmNoAppBarRecovery(false, false));
        check("a no-AppBar marker passes with no HWND and complete parent identity",
            RecoveryService.CanConfirmNoAppBarRecovery(false, true));
    }

    private static void CheckWebViewProfileMatching(Action<string, bool> check, string temporaryDirectory)
    {
        var profilePath = Path.GetFullPath(Path.Combine(temporaryDirectory, "WebView Profile"));
        var otherProfilePath = Path.Combine(temporaryDirectory, "Other Profile");
        const string executable = "\"C:\\Program Files\\Microsoft\\EdgeWebView\\Application\\msedgewebview2.exe\"";
        var commandLine = $"{executable} --type=renderer --user-data-dir=\"{profilePath}\" --lang=en-US";
        check("WebView command line matches the exact quoted profile path",
            App.IsWebViewProfileCommandLineMatch(commandLine, profilePath));
        var ebWebViewPath = Path.Combine(profilePath, "EBWebView");
        check("WebView2 EBWebView child profile matches its root profile",
            App.IsWebViewProfileCommandLineMatch(
                $"{executable} --type=renderer --user-data-dir=\"{ebWebViewPath}\"", profilePath));
        check("WebView command line matches the separate profile argument",
            App.IsWebViewProfileCommandLineMatch(
                $"{executable} --type=renderer --user-data-dir \"{profilePath}\"", profilePath));
        check("WebView command line rejects a prefix sibling profile",
            !App.IsWebViewProfileCommandLineMatch(
                $"{executable} --type=renderer --user-data-dir=\"{profilePath}-other\"", profilePath));
        var escapedProfilePath = Path.Combine(profilePath, "..", "Sibling");
        check("WebView command line rejects a parent-traversal sibling profile",
            !App.IsWebViewProfileCommandLineMatch(
                $"{executable} --type=renderer --user-data-dir=\"{escapedProfilePath}\"", profilePath));
        check("WebView command line rejects a different profile path",
            !App.IsWebViewProfileCommandLineMatch(
                $"{executable} --type=renderer --user-data-dir=\"{otherProfilePath}\"", profilePath));
        check("WebView command line ignores the profile path in unrelated arguments",
            !App.IsWebViewProfileCommandLineMatch(
                $"{executable} --other-switch=\"{profilePath}\"", profilePath));
        check("missing WebView command line fails closed",
            !App.IsWebViewProfileCommandLineMatch(null, profilePath));
    }

    private static void CheckSettingsAndAtomicSave(
        Action<string, bool> check,
        Action<string, Action> checkThrows,
        string temporaryDirectory)
    {
        var oldSettings = new AppSettings();
        check("unset external app selection remains a valid old configuration",
            oldSettings.TryValidateExternalApps(out _));

        var confirmedEmpty = new AppSettings
        {
            ExternalAppsConfirmed = true,
            ExternalApps = new List<ExternalAppEntry>()
        };
        check("explicitly confirmed empty selection differs from unset selection",
            confirmedEmpty.TryValidateExternalApps(out _));

        var inconsistent = new AppSettings
        {
            ExternalAppsConfirmed = false,
            ExternalApps = new List<ExternalAppEntry>()
        };
        check("inconsistent confirmation and list state is rejected",
            !inconsistent.TryValidateExternalApps(out _));

        var duplicateEntries = new AppSettings
        {
            ExternalAppsConfirmed = true,
            ExternalApps = new List<ExternalAppEntry>
            {
                NewExecutableEntry("same-id", 0, temporaryDirectory),
                NewExecutableEntry("same-id", 1, temporaryDirectory)
            }
        };
        check("duplicate frozen entry IDs are rejected", !duplicateEntries.TryValidateExternalApps(out _));

        var duplicateOrders = new AppSettings
        {
            ExternalAppsConfirmed = true,
            ExternalApps = new List<ExternalAppEntry>
            {
                NewExecutableEntry("entry-a", 0, temporaryDirectory),
                NewExecutableEntry("entry-b", 0, temporaryDirectory)
            }
        };
        check("duplicate frozen order values are rejected", !duplicateOrders.TryValidateExternalApps(out _));

        var mismatchedIdentity = NewExecutableEntry("identity-check", 0, temporaryDirectory);
        mismatchedIdentity.ExecutablePath = Path.Combine(temporaryDirectory, "changed.exe");
        check("frozen entry rejects a launch field changed without a matching stable ID",
            !mismatchedIdentity.TryValidate(out _));

        var absentPath = Path.Combine(temporaryDirectory, "missing.json");
        _ = AppSettings.LoadFromPath(absentPath, out var missingStatus);
        check("missing isolated config is classified as Missing without creating a file",
            missingStatus == AppSettings.ConfigLoadStatus.Missing && !File.Exists(absentPath));

        var explicitEmptyPath = Path.Combine(temporaryDirectory, "explicit-empty.json");
        confirmedEmpty.SaveToPath(explicitEmptyPath);
        var loadedEmpty = AppSettings.LoadFromPath(explicitEmptyPath, out var emptyStatus);
        check("explicit empty list survives save/load as Valid and confirmed",
            emptyStatus == AppSettings.ConfigLoadStatus.Valid
            && loadedEmpty.ExternalAppsConfirmed == true
            && loadedEmpty.ExternalApps is { Count: 0 });

        var corruptJsonPath = Path.Combine(temporaryDirectory, "corrupt-json.json");
        const string corruptJson = "{\"externalAppsConfirmed\": true,\"externalApps\":";
        File.WriteAllText(corruptJsonPath, corruptJson, Encoding.UTF8);
        var corruptJsonBytes = File.ReadAllBytes(corruptJsonPath);
        _ = AppSettings.LoadFromPath(corruptJsonPath, out var corruptJsonStatus);
        check("malformed JSON is classified as Corrupt without rewriting source",
            corruptJsonStatus == AppSettings.ConfigLoadStatus.Corrupt
            && File.ReadAllBytes(corruptJsonPath).SequenceEqual(corruptJsonBytes));

        var corruptListPath = Path.Combine(temporaryDirectory, "corrupt-list.json");
        const string corruptList = "{\"externalAppsConfirmed\":true,\"externalApps\":[{\"id\":\"x\",\"displayName\":\"X\",\"kind\":\"Executable\",\"executablePath\":\"relative.exe\"}]}";
        File.WriteAllText(corruptListPath, corruptList, Encoding.UTF8);
        var corruptListBytes = File.ReadAllBytes(corruptListPath);
        _ = AppSettings.LoadFromPath(corruptListPath, out var corruptListStatus);
        check("invalid frozen app list is classified as Corrupt without rewriting source",
            corruptListStatus == AppSettings.ConfigLoadStatus.Corrupt
            && File.ReadAllBytes(corruptListPath).SequenceEqual(corruptListBytes));

        var saveFailurePath = Path.Combine(temporaryDirectory, "existing-config.json");
        const string originalContent = "{\"preserve\":\"original config body\"}";
        File.WriteAllText(saveFailurePath, originalContent, Encoding.UTF8);
        var originalBytes = File.ReadAllBytes(saveFailurePath);
        Directory.CreateDirectory(saveFailurePath + ".tmp");
        checkThrows("failed atomic save reports the blocked temp path",
            () => confirmedEmpty.SaveToPath(saveFailurePath));
        check("failed save leaves the existing config body unchanged",
            File.ReadAllBytes(saveFailurePath).SequenceEqual(originalBytes));
    }

    private static ExternalAppEntry NewExecutableEntry(string id, int order, string workingDirectory)
    {
        var executablePath = Path.Combine(workingDirectory, $"{id}.exe");
        return new ExternalAppEntry
        {
            Id = ExternalAppEntry.CreateWin32StableId(executablePath, "", workingDirectory),
            DisplayName = "Disposable test executable",
            Kind = ExternalAppEntry.ExecutableKind,
            ExecutablePath = executablePath,
            WorkingDirectory = workingDirectory,
            Order = order
        };
    }
}
