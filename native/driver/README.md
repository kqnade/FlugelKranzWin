# FlugelKranz SteamVR driver

Windows x64 server driver. Based on the driver-pose interception approach explored
by [Kawaii Move Assist](https://github.com/ReinaS-64892/reina_s_kawaii_move_assist).
This implementation installs MinHook detours on the pose-update entry points of
IVRServerDriverHost_005 / _006, then bakes the original world-from-driver transform
and commanded flight transform into `qRotation` and `vecPosition`, following
Kawaii Move Assist's world-space body-pose representation. World-from-driver is
then identity. Linear and angular derivatives are rotated into the same space;
body-local head calibration and pose timing are preserved. Identity commands pass
the original driver pose through unchanged, including its original calibration.

The previous implementation changed world-from-driver instead of the body pose.
Quest 2 / Virtual Desktop testing reported black borders persisting after flight
rotation and disappearing on reset. The v2 body-pose change corrects the difference
from the reference implementation; whether it resolves that rendering symptom
still requires hardware testing. Neither VD nor SteamVR is established as the
cause. Tests verify the pose representation, not rendered headset images.

No Chaperone setters are used. Original device poses are published before the
flight transform, avoiding transformed-pose feedback into the C# motion engine.
Device pose snapshots use world-from-driver * body * driver-from-head. This
calibration composition and the installed-runtime hook behavior need hardware
validation; unit tests do not establish SteamVR compatibility.

IPC: session-local named mapping `FlugelKranz.Driver.State.v1` (4704 bytes), guarded
by `FlugelKranz.Driver.Mutex.v1`. See `Transform.h` and `DriverConnection.cs` for the
matching layout. One app owns the transform. A missing client heartbeat for 500ms
revokes ownership and restores identity; recovery requires reconnecting. Original
poses older than 100ms are not considered tracked. Shared-memory access is bounded.

Optional passive frame diagnostics: set `driver_flugelkranz.observeFrames` to true
in the packaged `resources/settings/default.vrsettings` before restarting SteamVR.
The packaged load priority is 100 so the observer can see HMD registration before
the HMD is activated. An existing SteamVR user setting can override these defaults.
Diagnostics observe HMD GetComponent and DirectMode_009 SubmitLayer, forwarding
the original arguments unchanged. No correction is applied to render layers.
`FrameAudit` records at most one layer per second to vrserver.txt, including its
render pose, prediction interval, current flight command and latest physical HMD
sample age. These are not synchronized frame-history samples; moving-head results
cannot alone establish a rendering mismatch. Layer selection is unspecified.
Compare stationary reset/offset states first. Unsupported Direct Mode versions or
HMDs registered before this driver will not produce layer records. Forwarding unit
tests do not establish that the installed VD driver uses this observed interface.
Set observeFrames back to false and restart to remove these diagnostic hooks.

Experimental render-pose correction: `driver_flugelkranz.correctFramePose=true`
also installs the DirectMode_009 hook. It copies each submitted layer and removes
the flight transform from both `mHmdPose` matrices; textures, depth, projection,
bounds and prediction intervals remain unchanged. The option defaults to false.
It is a hardware-validation candidate, not a verified streaming compatibility fix.

The transform is selected from the last 128 valid HMD output samples (at most
250ms old), using a high-resolution receipt timestamp, driver poseTimeOffset,
linear/angular velocity and SubmitLayer's prediction interval. Predictions beyond
100ms are rejected. This is a best-fit association, NOT an exact frame ID: the API
does not supply our command ID and SteamVR's prediction implementation is not
duplicated exactly. Both eyes must match. Missing/ambiguous matches forward the
original layer unchanged and appear as `corrected=0` in the rate-limited audit log;
this may leave intermittent borders during motion. `corrected=1` means a matched
inverse was applied, not that the HMD displayed it correctly. A full dynamic-motion
test remains necessary. The Core and the application-visible flight pose are not
changed by this option. Set both options false and restart to remove the hooks.

The server driver modifies every device whose pose update passes through the
hooked host functions, including HMD, controllers and FBT trackers. Other drivers
that hook the same functions need separate compatibility testing. No installer
restarts SteamVR or writes room setup. Driver registration is per-user through
SteamVR's `vrpathreg.exe` and can be undone using `install-driver.ps1 -Uninstall`.

Vendored files:

* OpenVR header and license: ValveSoftware/openvr `0924064316de3effbcd1acf1e309182a2deb1c05`.
* MinHook source and build files: TsudaKageyu/minhook `8af6b4acae5a9388fd742b56fa79ece89d96f823`.

Build with MSVC x64, Windows SDK, CMake, and .NET 10: `./build-windows.ps1`.
Run native tests: `ctest --test-dir artifacts/driver-build -C Release --output-on-failure`.
