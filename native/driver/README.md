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

Hardware status (2026-09-13): Quest 2 + Touch / Virtual Desktop users report that
rotation works broadly as intended with render-pose correction enabled. The v2
body-pose change alone did not resolve persistent black borders. Removing flight
from the submitted layer pose improved the headset view; MirrorView was already
reported normal. Head motion with a held flight offset still causes jitter or
blackouts in the initial version. The held-transform change below improved this
according to hardware feedback; simultaneous flight rotation and head motion still
causes jitter with the pose-fitting path.
Other headsets, games and FBT combinations have not been systematically validated.

No Chaperone setters are used. Original device poses are published before the
flight transform, avoiding transformed-pose feedback into the C# motion engine.
Device pose snapshots use world-from-driver * body * driver-from-head. This
calibration composition needs validation beyond the tested headset. Pose hooks and
the DirectMode_009 SubmitLayer hook have been observed on the installed VD driver;
unit tests alone do not establish SteamVR compatibility.

IPC: session-local named mapping `FlugelKranz.Driver.State.v1` (4704 bytes), guarded
by `FlugelKranz.Driver.Mutex.v1`. See `Transform.h` and `DriverConnection.cs` for the
matching layout. One app owns the transform. A missing client heartbeat for 500ms
revokes ownership and restores identity; recovery requires reconnecting. Original
poses older than 100ms are not considered tracked. Shared-memory access is bounded.

Optional passive frame diagnostics: set `driver_flugelkranz.observeFrames` to true
in the packaged `resources/settings/default.vrsettings` before restarting SteamVR.
The packaged load priority is 100 so the observer can see HMD registration before
the HMD is activated. An existing SteamVR user setting can override these defaults.
With correctFramePose=false, diagnostics observe HMD GetComponent and DirectMode_009
SubmitLayer, forwarding original arguments unchanged. No correction is applied.
`FrameAudit` records at most one layer per second to vrserver.txt, including its
render pose, prediction interval, current flight command and latest physical HMD
sample age. These are not synchronized frame-history samples; moving-head results
cannot alone establish a rendering mismatch. Layer selection is unspecified.
Compare stationary reset/offset states first. Unsupported Direct Mode versions or
HMDs registered before this driver will not produce layer records. Set both
observeFrames and correctFramePose false and restart to remove these hooks.

Experimental render-pose correction: `driver_flugelkranz.correctFramePose=true`
also installs the DirectMode_009 hook. It copies each submitted layer and removes
the flight transform from both `mHmdPose` matrices; textures, depth, projection,
bounds and prediction intervals remain unchanged. The option defaults to false.
This option was enabled for the successful rotation report above. Dynamic-motion
stability remains under development; it is not a general compatibility guarantee.

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

Held-transform correction: after 250ms of continuous valid output samples with an
exactly unchanged flight transform, the same inverse applies throughout the
supported history window. This path does not depend on predicting physical head
motion, so a prediction mismatch no longer disables correction while holding a
rotation. It requires an HMD sample within 50ms. A transform change, invalid pose,
or sample gap over 100ms restarts the hold window. Frames delayed beyond the
supported history window remain outside this model. This change still needs an
on-headset verification beyond the reported improvement. Active flight changes
continue to use the best-fit matcher unless the time-based option below is enabled.

`timeBasedFramePose=true` (default false, requires correctFramePose) is a further
candidate for simultaneous head/flight motion. It stores the original DriverPose
beside each transformed output, and computes the physical head pose for
`SubmitLayer receipt time + flHmdPosePredictionTimeInSecondsFromNow`. Sample time is
`pose-update receipt time + poseTimeOffset`. Bracketing physical samples use linear
position interpolation and quaternion SLERP; otherwise linear/angular velocity
predicts at most 100ms forward or backward. Head/IMU calibration is retained. Fresh
tracking within 50ms is required. No virtual-pose similarity or flight-command
selection is involved. It assigns this physical pose to each eye's metadata while
preserving textures, projection, bounds and prediction times. Logs show `timed=1`,
`physicalDt` and `matchError=-1` (no pose-fit error exists in this path).

This association relies on the documented prediction-time meaning and local
receipt timestamps. It does not reproduce VD/SteamVR prediction exactly, and must
be tested on hardware for residual timing error. Source and distributed defaults
remain opt-in; setting timeBasedFramePose=false restores the earlier fitting path.

Audit records also include `layers`, `missed`, and `held`: counts across submitted
layers since the preceding record, rather than one sampled success/failure per
second. Multiple layers can belong to one rendered frame, so these are layer
counts, not headset frame counts. `held` counts the constant-transform path and
`missed` counts uncorrected layers while correction is enabled.

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
