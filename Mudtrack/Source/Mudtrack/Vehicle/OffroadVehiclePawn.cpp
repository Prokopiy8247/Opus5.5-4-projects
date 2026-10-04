// OffroadVehiclePawn.cpp
//
// SOLVER OVERVIEW
// ---------------
// One Chaos rigid body carries the mass. Each tick we advance a fixed-step
// accumulator at PhysicsStep (1/120 s), and within each substep:
//
//   1. Probe the ground under every wheel with a multi-sample sweep. The
//      supporting surface comes from the soil field (ruts included) and, where
//      a solid obstacle is found above it, from that obstacle.
//   2. Solve each suspension spring/damper from the measured compression and
//      its rate of change. Clamp to the travel limits. The force is capped so a
//      single substep can never launch the vehicle.
//   3. Build the tyre force from slip ratio and slip angle, limited by the
//      friction ellipse and the available traction from the soil.
//   4. Feed engine torque through the gearbox and differentials into the wheel
//      rotational states, and integrate each wheel's angular velocity.
//   5. Apply the accumulated forces to the body at the wheel contact points, so
//      load transfer and roll are emergent rather than scripted.
//
// Nothing in this path calls SetActorLocation, and the body is never aligned to
// the surface normal. Visual wheel transforms are derived from the solver state.

#include "OffroadVehiclePawn.h"
#include "Drivetrain.h"
#include "../Soil/SoilField.h"
#include "../Mudtrack.h"

#include "Components/BoxComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Components/ArrowComponent.h"
#include "GameFramework/SpringArmComponent.h"
#include "Camera/CameraComponent.h"
#include "Engine/World.h"
#include "Engine/StaticMesh.h"
#include "EngineUtils.h"
#include "GameFramework/PlayerStart.h"
#include "DrawDebugHelpers.h"
#include "Kismet/GameplayStatics.h"
#include "Materials/MaterialInterface.h"
#include "PhysicsEngine/PhysicsSettings.h"
#include "GameFramework/PlayerController.h"
#include "../UI/MudtrackHUD.h"

DEFINE_LOG_CATEGORY_STATIC(LogVehicle, Log, All);

namespace
{
	/** Gravity in cm/s^2. */
	constexpr float Gravity = 980.f;

	/** Convert N to the Unreal force unit (kg*cm/s^2) — numerically identical. */
	FORCEINLINE float NewtonsToUU(float N) { return N; }

	/** Traction ellipse exponent; 2 = ellipse, >2 = squarer. */
	constexpr float FrictionEllipseExp = 2.0f;

	FORCEINLINE float Sign(float V) { return V >= 0.f ? 1.f : -1.f; }
	FORCEINLINE float Saturate(float V) { return FMath::Clamp(V, 0.f, 1.f); }

	/**
	 * Magic-formula-flavoured tyre curve.
	 * Returns a friction coefficient in [0, Peak], peaking at SlipPeak and
	 * decaying toward a sliding plateau.
	 */
	/**
	 * Torques in the drivetrain are specified in N*cm (PeakTorqueNcm = 42000 is
	 * 420 N*m). Everything they act against -- wheel inertia in kg*cm^2, tyre
	 * reaction = force (kg*cm/s^2) x radius (cm) -- is in Unreal's units, where
	 * 1 N*cm = 100 kg*cm^2/s^2. Without this factor the engine and brakes were a
	 * hundredth of their rated strength: full throttle in low range moved the
	 * vehicle less than a centimetre in five seconds.
	 */
	constexpr float NcmToUnreal = 100.f;

	FORCEINLINE float TyreCurve(float Slip, float Peak, float SlipPeak)
	{
		const float S = FMath::Abs(Slip);
		if (S < 1e-5f) return 0.f;
		const float X = S / FMath::Max(SlipPeak, 1e-4f);
		// Rise to the peak at X = 1 (x*e^(1-x) meets it with zero slope), then a
		// gentle, monotonic fall to 72% of peak for a fully sliding tyre.
		//
		// The previous shape, x*e^(1-x)*0.94 + 0.06x, collapsed past the peak:
		// about 31% of peak at 60-130% slip, recovering only at very high slip.
		// A driven wheel that broke loose settled in that trough, spinning at
		// twice road speed, and a locked wheel braked at ~0.3 g.
		const float Y = (X <= 1.f)
			? X * FMath::Exp(1.f - X)
			: FMath::Lerp(1.0f, 0.72f, Saturate((X - 1.f) / 3.f));
		return Peak * Y;
	}
}

AOffroadVehiclePawn::AOffroadVehiclePawn()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.TickGroup = TG_PrePhysics;

	BodyCollision = CreateDefaultSubobject<UBoxComponent>(TEXT("BodyCollision"));
	RootComponent = BodyCollision;
	// Chassis box: 400 long (X, forward) x 184 wide (Y) x 56 tall, centred on the
	// body origin.
	//
	// It used to be 190 x 400 -- i.e. 4 m WIDE and 1.9 m long, turned 90 degrees
	// from the wheel layout. Chaos derives the inertia tensor from this shape, so
	// that made roll inertia about 5x too high and pitch about 4x too low, and the
	// vehicle hit trees side-on. Its underside also sat at z = -40, below the
	// ground at rest (about -35.5), so the underbody contact fired permanently.
	// The underside is now at -28: clear of the ground at rest, with the springs
	// able to compress before the hull grounds.
	BodyCollision->SetBoxExtent(FVector(200.f, 92.f, 28.f));
	BodyCollision->SetCollisionEnabled(ECollisionEnabled::QueryAndPhysics);
	BodyCollision->SetCollisionObjectType(ECC_Pawn);
	BodyCollision->SetCollisionResponseToAllChannels(ECR_Block);
	BodyCollision->SetCollisionResponseToChannel(ECC_Camera, ECR_Ignore);
	BodyCollision->SetSimulatePhysics(false);   // enabled in BeginPlay
	BodyCollision->SetLinearDamping(0.02f);
	BodyCollision->SetAngularDamping(0.35f);
	BodyCollision->bReplicatePhysicsToAutonomousProxy = false;
	BodyCollision->SetUseCCD(true);

	BodyMesh = CreateDefaultSubobject<UStaticMeshComponent>(TEXT("BodyMesh"));
	BodyMesh->SetupAttachment(BodyCollision);
	BodyMesh->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	BodyMesh->SetRelativeLocation(FVector::ZeroVector);

	// ---- cameras
	ChaseArm = CreateDefaultSubobject<USpringArmComponent>(TEXT("ChaseArm"));
	ChaseArm->SetupAttachment(BodyCollision);
	ChaseArm->TargetArmLength = 780.f;
	ChaseArm->SetRelativeRotation(FRotator(-14.f, 0.f, 0.f));
	ChaseArm->bInheritPitch = false;
	ChaseArm->bInheritRoll = false;
	ChaseArm->bEnableCameraLag = true;
	ChaseArm->CameraLagSpeed = 8.f;
	ChaseArm->bDoCollisionTest = true;

	ChaseCamera = CreateDefaultSubobject<UCameraComponent>(TEXT("ChaseCamera"));
	ChaseCamera->SetupAttachment(ChaseArm);
	ChaseCamera->FieldOfView = 82.f;

	SuspensionCamera = CreateDefaultSubobject<UCameraComponent>(TEXT("SuspensionCamera"));
	SuspensionCamera->SetupAttachment(BodyCollision);
	SuspensionCamera->SetRelativeLocation(FVector(150.f, 130.f, -10.f));
	SuspensionCamera->SetRelativeRotation(FRotator(6.f, -110.f, 0.f));
	SuspensionCamera->FieldOfView = 68.f;

	FreeCamera = CreateDefaultSubobject<UCameraComponent>(TEXT("FreeCamera"));
	FreeCamera->SetupAttachment(BodyCollision);
	FreeCamera->SetRelativeLocation(FVector(-900.f, 0.f, 500.f));
	FreeCamera->SetRelativeRotation(FRotator(-18.f, 0.f, 0.f));
	FreeCamera->FieldOfView = 74.f;

	WinchLine = CreateDefaultSubobject<USpringArmComponent>(TEXT("WinchLine"));
	WinchLine->SetupAttachment(BodyCollision);
	WinchLine->TargetArmLength = 0.f;
	WinchLine->bDoCollisionTest = false;
	WinchLine->SetHiddenInGame(true);

	AutoPossessPlayer = EAutoReceiveInput::Player0;

	// Manual exposure on every camera, so the picture does not depend on what
	// the first frames happened to contain.
	for (UCameraComponent* Cam : { ChaseCamera.Get(), SuspensionCamera.Get(), FreeCamera.Get() })
	{
		FPostProcessSettings& PP = Cam->PostProcessSettings;
		PP.bOverride_AutoExposureMethod = true;
		PP.AutoExposureMethod = EAutoExposureMethod::AEM_Manual;
		PP.bOverride_AutoExposureApplyPhysicalCameraExposure = true;
		PP.AutoExposureApplyPhysicalCameraExposure = false;
		// In manual mode AutoExposureBias is exposure COMPENSATION in stops (+ is
		// brighter) relative to EV100 0, so EV100 14 is a bias of -14. Passing
		// +14 overexposed the frame to solid white.
		PP.bOverride_AutoExposureBias = true;
		PP.AutoExposureBias = -ExposureEV100;
	}

	// ---- default 4x4 wheel setup: solid axles, coil springs
	Wheels.SetNum(4);
	const float HalfTrack = 83.f;
	const float AxleF = 142.5f;
	const float AxleR = -142.5f;

	// Front left, front right, rear left, rear right
	const FVector Attach[4] = {
		FVector( HalfTrack, AxleF, 46.f),   // 0 FL
		FVector(-HalfTrack, AxleF, 46.f),   // 1 FR   (X is left/right in UE)
		FVector( HalfTrack, AxleR, 46.f),   // 2 RL
		FVector(-HalfTrack, AxleR, 46.f),   // 3 RR
	};
	// NOTE: Unreal is left-handed with +X forward, +Y right, +Z up. Blender
	// exported +X = left/right, +Y = forward. FBX conversion handles the swap;
	// here we place wheels with Y as the lateral axis and X as longitudinal to
	// match the imported mesh orientation.
	const FVector AttachUE[4] = {
		FVector( AxleF, -HalfTrack, 46.f),
		FVector( AxleF,  HalfTrack, 46.f),
		FVector( AxleR, -HalfTrack, 46.f),
		FVector( AxleR,  HalfTrack, 46.f),
	};
	(void)Attach;

	for (int32 i = 0; i < 4; ++i)
	{
		FWheelSetup& W = Wheels[i];
		W.AttachPoint = AttachUE[i];
		W.Radius = 46.5f;
		W.Width = 33.f;
		W.SuspensionRestLength = 46.f;
		W.MaxCompression = 22.f;
		W.MaxDroop = 18.f;

		// Spring and damper rates are derived from the actual corner load, not
		// guessed. In Unreal's force units (kg*cm/s^2) a 2150 kg vehicle weighs
		//   2150 * 980 = 2,107,000, i.e. ~526,750 per corner.
		// For a comfortable ~11 cm of static sag:
		//   k = 526750 / 11 ≈ 48,000
		// Critical damping for a 537 kg corner is 2*sqrt(k*m) ≈ 10,100, so we sit
		// at a little under half of that: firmer in rebound than compression.
		W.SpringRate = 48000.f;
		W.DampingCompression = 3400.f;
		W.DampingRebound = 5400.f;
		W.BumpStopRate = 260000.f;

		// A 46.5 cm wheel of roughly 35 kg has I ≈ 0.5*m*r² ≈ 37,800 kg*cm².
		W.Inertia = 36000.f;

		W.PeakFriction = 1.05f;
		W.PeakSlipRatio = 0.16f;
		W.PeakSlipAngle = 0.14f;
		W.bSteerable = (i < 2);
		W.bDriven = true;
		W.bHandbrake = (i >= 2);
	}

	// ---- default imported meshes -------------------------------------
	//
	// These are the assets Scripts/import_assets.py produces. Setting them here,
	// in C++, makes the vehicle render even when it is spawned as the bare class
	// rather than through BP_Offroad4x4 (which is what actually happened: the
	// Blueprint was created but its CDO defaults did not survive into a packaged
	// cook, so SetupVisualComponents logged "body=missing ..."). A Blueprint
	// subclass can still override any of these.
	BodyMeshAsset   = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Vehicle/SM_VehicleBody.SM_VehicleBody")));
	WheelMeshAsset  = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Vehicle/SM_Wheel.SM_Wheel")));
	AxleMeshAsset   = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Vehicle/SM_AxleFront.SM_AxleFront")));
	SpringMeshAsset = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Vehicle/SM_Spring.SM_Spring")));
}

void AOffroadVehiclePawn::BeginPlay()
{
	Super::BeginPlay();
	for (UCameraComponent* Cam : { ChaseCamera.Get(), SuspensionCamera.Get(), FreeCamera.Get() })
	{
		if (Cam != nullptr)
		{
			Cam->PostProcessSettings.AutoExposureBias = -ExposureEV100;
		}
	}
	InitializeVehicleRuntime();
}

void AOffroadVehiclePawn::InitializeVehicleRuntime()
{
	// Idempotent: BeginPlay and the headless commandlet both call this, and it
	// must be safe to call twice.
	if (bRuntimeInitialized)
	{
		return;
	}
	bRuntimeInitialized = true;

	// Physics body
	BodyCollision->SetSimulatePhysics(true);
	BodyCollision->SetMassOverrideInKg(NAME_None, MassKg, true);
	BodyCollision->SetCenterOfMass(CentreOfMassOffset);
	BodyCollision->SetEnableGravity(true);
	BodyCollision->SetPhysicsLinearVelocity(FVector::ZeroVector);
	BodyCollision->SetPhysicsAngularVelocityInDegrees(FVector::ZeroVector);
	BodyCollision->WakeAllRigidBodies();

	WheelStates.SetNum(Wheels.Num());
	for (int32 i = 0; i < WheelStates.Num(); ++i)
	{
		WheelStates[i].SuspensionLength = Wheels[i].SuspensionRestLength;
		WheelStates[i].PrevSuspensionLength = Wheels[i].SuspensionRestLength;
	}

	EngineOmega = Engine.IdleRPM * 2.f * PI / 60.f;
	Drive.EngineRPM = Engine.IdleRPM;

	LastSafeLocation = GetActorLocation();
	LastSafeRotation = GetActorRotation();

	// Build the visual components (wheels, springs, axles) from the imported meshes.
	SetupVisualComponents();

	// Locate the soil field in the level.
	SoilField = nullptr;
	for (TActorIterator<ASoilField> It(GetWorld()); It; ++It)
	{
		SoilField = *It;
		break;
	}
	if (SoilField != nullptr)
	{
		// The pawn's first ground probe can happen before the soil field's own
		// BeginPlay; make sure the grid exists before anything samples it.
		SoilField->EnsureInitialised();
	}
	if (SoilField == nullptr)
	{
		UE_LOG(LogVehicle, Warning, TEXT("No ASoilField in level; falling back to line traces only"));
	}
	else
	{
		// Drop the vehicle onto the surface before physics takes over.
		//
		// PlayerStart sits at a fixed height (z=200 in this level) with no
		// knowledge of the procedural terrain under it, and the terrain here is
		// generated at runtime -- so the spawn point can be tens of metres above
		// the ground or below it, in which case the wheels start inside the soil
		// and are pushed out. Placing the body at (surface + suspension + wheel
		// radius) makes the first frame start at the static ride height instead
		// of in free fall.
		//
		// This is the one place SetActorLocation is legitimate: it runs once, at
		// spawn, before the solver has any state worth preserving.
		const FVector Loc = GetActorLocation();
		const FSoilSample S = SoilField->SampleSoil(FVector(Loc.X, Loc.Y, 0.f));
		if (S.bValid)
		{
			// Place the body so the wheel rests on the surface at the STATIC ride
			// height, with the spring already carrying the vehicle's weight.
			//
			// Geometry, all measured from the body origin, with the suspension
			// axis pointing down and the hub hanging SuspensionLength below the
			// attach point:
			//
			//     attach world z = body_z + AttachPoint.Z
			//     hub     world z = attach_z - SuspensionLength
			//     contact world z = hub_z - Radius          <-- must equal surface
			//
			// Solving for body_z with SuspensionLength = RestLength - StaticSag:
			//
			//     body_z = surface + (RestLength - StaticSag) + Radius - AttachPoint.Z
			//
			// Two earlier attempts got this wrong in opposite directions: adding
			// AttachPoint.Z instead of subtracting it (which pushed the body up by
			// 2*46 cm and left the wheels at full droop, so nothing supported the
			// vehicle and it fell), and ignoring the static sag entirely.
			float HighestAttach = Wheels.Num() > 0 ? Wheels[0].AttachPoint.Z : 0.f;
			float MaxRadius = Wheels.Num() > 0 ? Wheels[0].Radius : 0.f;
			float MinRest = Wheels.Num() > 0 ? Wheels[0].SuspensionRestLength : 0.f;
			float MaxRest = MinRest;
			for (const FWheelSetup& W : Wheels)
			{
				HighestAttach = FMath::Max(HighestAttach, W.AttachPoint.Z);
				MaxRadius = FMath::Max(MaxRadius, W.Radius);
				MinRest = FMath::Min(MinRest, W.SuspensionRestLength);
				MaxRest = FMath::Max(MaxRest, W.SuspensionRestLength);
			}

			const float StaticSag = (Wheels.Num() > 0 && Wheels[0].SpringRate > 0.f)
				? ((MassKg * 980.f) / 4.f) / Wheels[0].SpringRate
				: 0.f;

			const float RideLength = FMath::Clamp(MaxRest - StaticSag,
				MaxRest - Wheels[0].MaxCompression,
				MaxRest + Wheels[0].MaxDroop);

			const float TargetZ = S.SurfaceZ + RideLength + MaxRadius - HighestAttach;
			SetActorLocation(FVector(Loc.X, Loc.Y, TargetZ), false, nullptr, ETeleportType::TeleportPhysics);

			// Start the solver at the sagged length so the very first substep
			// already produces ~static load instead of zero.
			for (FWheelState& WS : WheelStates)
			{
				WS.SuspensionLength = RideLength;
				WS.PrevSuspensionLength = WS.SuspensionLength;
			}

			UE_LOG(LogVehicle, Log,
				TEXT("Vehicle placed on ground: surface=%.1f sag=%.2f ride_len=%.2f target_z=%.1f (was %.1f)"),
				S.SurfaceZ, StaticSag, RideLength, TargetZ, Loc.Z);
		}
		else
		{
			UE_LOG(LogVehicle, Warning,
				TEXT("No valid soil sample under the spawn point (z=%.1f); the vehicle starts in free fall"),
				Loc.Z);
		}
	}

	SetCameraMode(0);

	UE_LOG(LogVehicle, Log, TEXT("Vehicle runtime initialized: %d wheels, mass %.0f kg, soil=%s"),
		WheelStates.Num(), MassKg, SoilField ? TEXT("found") : TEXT("MISSING"));
}

// ---------------------------------------------------------------------------
// Input
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::SetupPlayerInputComponent(UInputComponent* PlayerInputComponent)
{
	Super::SetupPlayerInputComponent(PlayerInputComponent);
	if (PlayerInputComponent == nullptr) return;

	// Legacy axis/action bindings, declared in DefaultInput.ini. Using these
	// rather than Enhanced Input keeps the pawn callable from Python and from
	// the automated tests without needing an asset-based mapping context.
	PlayerInputComponent->BindAxis(TEXT("MT_Throttle"), this, &AOffroadVehiclePawn::InputThrottle);
	PlayerInputComponent->BindAxis(TEXT("MT_Brake"), this, &AOffroadVehiclePawn::InputBrake);
	PlayerInputComponent->BindAxis(TEXT("MT_Steer"), this, &AOffroadVehiclePawn::InputSteer);

	PlayerInputComponent->BindAction(TEXT("MT_Handbrake"), IE_Pressed, this, &AOffroadVehiclePawn::InputHandbrakePressed);
	PlayerInputComponent->BindAction(TEXT("MT_ShiftUp"), IE_Pressed, this, &AOffroadVehiclePawn::InputShiftUp);
	PlayerInputComponent->BindAction(TEXT("MT_ShiftDown"), IE_Pressed, this, &AOffroadVehiclePawn::InputShiftDown);
	PlayerInputComponent->BindAction(TEXT("MT_ToggleRange"), IE_Pressed, this, &AOffroadVehiclePawn::InputToggleRange);
	PlayerInputComponent->BindAction(TEXT("MT_CycleDrive"), IE_Pressed, this, &AOffroadVehiclePawn::InputCycleDrive);
	PlayerInputComponent->BindAction(TEXT("MT_CycleCenterDiff"), IE_Pressed, this, &AOffroadVehiclePawn::InputCycleCenterDiff);
	PlayerInputComponent->BindAction(TEXT("MT_CycleFrontDiff"), IE_Pressed, this, &AOffroadVehiclePawn::InputCycleFrontDiff);
	PlayerInputComponent->BindAction(TEXT("MT_CycleRearDiff"), IE_Pressed, this, &AOffroadVehiclePawn::InputCycleRearDiff);
	PlayerInputComponent->BindAction(TEXT("MT_Winch"), IE_Pressed, this, &AOffroadVehiclePawn::InputWinch);
	PlayerInputComponent->BindAction(TEXT("MT_WinchRelease"), IE_Pressed, this, &AOffroadVehiclePawn::InputReleaseWinch);
	PlayerInputComponent->BindAction(TEXT("MT_Recover"), IE_Pressed, this, &AOffroadVehiclePawn::InputRecover);
	PlayerInputComponent->BindAction(TEXT("MT_CycleCamera"), IE_Pressed, this, &AOffroadVehiclePawn::InputCycleCamera);
	PlayerInputComponent->BindAction(TEXT("MT_ToggleDiagnostics"), IE_Pressed, this, &AOffroadVehiclePawn::InputToggleDiagnostics);
	PlayerInputComponent->BindAction(TEXT("MT_ToggleAutoGearbox"), IE_Pressed, this, &AOffroadVehiclePawn::InputToggleAutoGearbox);
	PlayerInputComponent->BindAction(TEXT("MT_LoadUp"), IE_Pressed, this, &AOffroadVehiclePawn::InputLoadUp);
	PlayerInputComponent->BindAction(TEXT("MT_LoadDown"), IE_Pressed, this, &AOffroadVehiclePawn::InputLoadDown);

	UE_LOG(LogVehicle, Log, TEXT("Input bound"));
}

void AOffroadVehiclePawn::InputToggleDiagnostics()
{
	if (const APlayerController* PC = Cast<APlayerController>(GetController()))
	{
		if (AMudtrackHUD* HUD = Cast<AMudtrackHUD>(PC->GetHUD()))
		{
			HUD->ToggleDiagnostics();
		}
	}
}

// Direct calls take the pedals from the keyboard/gamepad axes (see the note on
// bExternalControl in the header): this is the API the tests and scripts use.
void AOffroadVehiclePawn::SetThrottle(float Value) { bExternalControl = true; ThrottleTarget = FMath::Clamp(Value, -1.f, 1.f); }
void AOffroadVehiclePawn::SetBrake(float Value)    { bExternalControl = true; BrakeTarget = FMath::Clamp(Value, 0.f, 1.f); }
void AOffroadVehiclePawn::SetSteering(float Value) { bExternalControl = true; SteeringTarget = FMath::Clamp(Value, -1.f, 1.f); }
void AOffroadVehiclePawn::SetHandbrake(bool bOn)   { bHandbrakeOn = bOn; }

void AOffroadVehiclePawn::ShiftUp()
{
	const int32 MaxGear = Gearbox.Ratios.Num() - 1;
	if (Drive.GearIndex < MaxGear && Drive.ShiftTimer <= 0.f)
	{
		Drive.GearIndex++;
		Drive.ShiftTimer = Gearbox.ShiftTime;
		OnGearChanged.Broadcast(Drive.GearIndex);
	}
}

void AOffroadVehiclePawn::ShiftDown()
{
	if (Drive.GearIndex > 0 && Drive.ShiftTimer <= 0.f)
	{
		Drive.GearIndex--;
		Drive.ShiftTimer = Gearbox.ShiftTime;
		OnGearChanged.Broadcast(Drive.GearIndex);
	}
}

void AOffroadVehiclePawn::ToggleRange()
{
	Drive.bLowRange = !Drive.bLowRange;
}

void AOffroadVehiclePawn::CycleDriveMode()
{
	switch (Drive.DriveMode)
	{
	case EDriveMode::FourWheelDrive:    Drive.DriveMode = EDriveMode::TwoWheelDriveRear; break;
	case EDriveMode::TwoWheelDriveRear: Drive.DriveMode = EDriveMode::TwoWheelDriveFront; break;
	default:                            Drive.DriveMode = EDriveMode::FourWheelDrive; break;
	}
}

void AOffroadVehiclePawn::CycleCenterDiff()
{
	switch (Drive.CenterDiff)
	{
	case EDiffLock::Open:        Drive.CenterDiff = EDiffLock::LimitedSlip; break;
	case EDiffLock::LimitedSlip: Drive.CenterDiff = EDiffLock::Locked; break;
	default:                     Drive.CenterDiff = EDiffLock::Open; break;
	}
}

void AOffroadVehiclePawn::CycleAxleDiff(bool bFront)
{
	EDiffLock& D = bFront ? Drive.FrontDiff : Drive.RearDiff;
	switch (D)
	{
	case EDiffLock::Open:        D = EDiffLock::LimitedSlip; break;
	case EDiffLock::LimitedSlip: D = EDiffLock::Locked; break;
	default:                     D = EDiffLock::Open; break;
	}
}

void AOffroadVehiclePawn::SetCameraMode(int32 Mode)
{
	ChaseCamera->SetActive(Mode == 0);
	SuspensionCamera->SetActive(Mode == 1);
	FreeCamera->SetActive(Mode == 2);
}

void AOffroadVehiclePawn::CycleCamera()
{
	// Determine current mode from which camera is active.
	int32 Mode = 0;
	if (SuspensionCamera->IsActive()) Mode = 1;
	else if (FreeCamera->IsActive()) Mode = 2;
	SetCameraMode((Mode + 1) % 3);
}

FString AOffroadVehiclePawn::GetDrivetrainSummary() const
{
	FString DriveStr;
	switch (Drive.DriveMode)
	{
	case EDriveMode::TwoWheelDriveRear:  DriveStr = TEXT("2WD-R"); break;
	case EDriveMode::TwoWheelDriveFront: DriveStr = TEXT("2WD-F"); break;
	default:                             DriveStr = TEXT("4WD");   break;
	}

	auto DiffStr = [](EDiffLock L) -> const TCHAR*
	{
		switch (L)
		{
		case EDiffLock::LimitedSlip: return TEXT("LSD");
		case EDiffLock::Locked:      return TEXT("LOCK");
		default:                     return TEXT("OPEN");
		}
	};

	const TCHAR* RangeStr = Drive.bLowRange ? TEXT("LOW") : TEXT("HIGH");
	const TCHAR* CenterStr = DiffStr(Drive.CenterDiff);
	const TCHAR* FrontStr = DiffStr(Drive.FrontDiff);
	const TCHAR* RearStr = DiffStr(Drive.RearDiff);

	return FString::Printf(TEXT("%s  %s  C:%s F:%s R:%s"),
		*DriveStr, RangeStr, CenterStr, FrontStr, RearStr);
}

// ---------------------------------------------------------------------------
// Visual component setup
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::SetupVisualComponents()
{
	// Idempotent. BeginPlay, the headless driver and the test runner can all
	// reach here; creating a second component with the same name asserts, so we
	// tear down anything we made previously and start clean.
	auto DestroyPrev = [this](TArray<TObjectPtr<UStaticMeshComponent>>& Arr)
	{
		for (TObjectPtr<UStaticMeshComponent>& C : Arr)
		{
			if (C != nullptr && IsValid(C))
			{
				C->DestroyComponent();
			}
		}
		Arr.Reset();
	};
	DestroyPrev(WheelMeshes);
	DestroyPrev(SpringMeshes);
	DestroyPrev(AxleMeshes);

	// Remove any stragglers from a previous partial run.
	TArray<UActorComponent*> Existing;
	GetComponents(Existing);
	for (UActorComponent* C : Existing)
	{
		if (C == nullptr) continue;
		const FString N = C->GetName();
		if (N.StartsWith(TEXT("Wheel_")) || N.StartsWith(TEXT("Spring_")) ||
		    N.StartsWith(TEXT("Axle_")))
		{
			C->DestroyComponent();
		}
	}

	auto MakeComp = [this](const TCHAR* Name) -> UStaticMeshComponent*
	{
		UStaticMeshComponent* C = NewObject<UStaticMeshComponent>(this, Name);
		C->SetupAttachment(BodyCollision);
		C->RegisterComponent();
		C->SetCollisionEnabled(ECollisionEnabled::NoCollision);
		C->SetGenerateOverlapEvents(false);
		C->SetCastShadow(true);
		C->SetMobility(EComponentMobility::Movable);
		return C;
	};

	// The meshes are modelled standing on a ground plane at mesh Z = 0. Put that
	// plane where the physics puts the ground at static ride height, so the body
	// sits on its wheels instead of floating above them (it was ~35 cm high,
	// because the mesh origin was placed at the actor origin, which is the axle
	// line plus the suspension travel, not the ground).
	if (Wheels.Num() > 0)
	{
		const FWheelSetup& W0 = Wheels[0];
		const float StaticSag = ((MassKg * 980.f) / FMath::Max(Wheels.Num(), 1)) / FMath::Max(W0.SpringRate, 1.f);
		const float RideLength = FMath::Clamp(W0.SuspensionRestLength - StaticSag,
			W0.SuspensionRestLength - W0.MaxCompression, W0.SuspensionRestLength + W0.MaxDroop);
		MeshGroundZ = W0.AttachPoint.Z - RideLength - W0.Radius;
	}

	UStaticMesh* BM = BodyMeshAsset.LoadSynchronous();
	if (BM != nullptr)
	{
		BodyMesh->SetStaticMesh(BM);
		BodyMesh->SetRelativeRotation(BodyMeshRotationOffset);
		BodyMesh->SetRelativeLocation(FVector(0.f, 0.f, MeshGroundZ));
	}

	UStaticMesh* WM = WheelMeshAsset.LoadSynchronous();
	UStaticMesh* AM = AxleMeshAsset.LoadSynchronous();
	UStaticMesh* SM = SpringMeshAsset.LoadSynchronous();

	WheelMeshes.SetNum(4);
	SpringMeshes.SetNum(4);

	const TCHAR* WheelNames[4] = { TEXT("Wheel_FL"), TEXT("Wheel_FR"), TEXT("Wheel_RL"), TEXT("Wheel_RR") };
	const TCHAR* SpringNames[4] = { TEXT("Spring_FL"), TEXT("Spring_FR"), TEXT("Spring_RL"), TEXT("Spring_RR") };

	for (int32 i = 0; i < 4; ++i)
	{
		UStaticMeshComponent* WC = MakeComp(WheelNames[i]);
		if (WM) WC->SetStaticMesh(WM);
		WheelMeshes[i] = WC;

		UStaticMeshComponent* SC = MakeComp(SpringNames[i]);
		if (SM) SC->SetStaticMesh(SM);
		SpringMeshes[i] = SC;
	}

	AxleMeshes.SetNum(2);
	const TCHAR* AxleNames[2] = { TEXT("Axle_Front"), TEXT("Axle_Rear") };
	for (int32 i = 0; i < 2; ++i)
	{
		UStaticMeshComponent* AC = MakeComp(AxleNames[i]);
		if (AM) AC->SetStaticMesh(AM);
		AxleMeshes[i] = AC;
	}

	// The wheel mesh has its axle along mesh X (measured: 33 x 98.6 x 98.6 cm) and
	// a 49.3 cm radius against the solver's 46.5 cm. Scale it to the solver's
	// radius so the drawn tyre touches the ground exactly where the physics does,
	// rather than sinking 2.8 cm into it.
	if (WM != nullptr)
	{
		const FBox WB = WM->GetBoundingBox();
		const float MeshRadius = FMath::Max(WB.GetExtent().Z, 1.f);
		const float Scale = (Wheels.Num() > 0) ? Wheels[0].Radius / MeshRadius : 1.f;
		for (int32 i = 0; i < WheelMeshes.Num(); ++i)
		{
			if (WheelMeshes[i] != nullptr)
			{
				WheelMeshes[i]->SetRelativeScale3D(FVector(Scale));
			}
		}
	}

	// The spring was exported in place on the vehicle: its geometry is centred at
	// (-62, -142.5, 72.5), not at its pivot. Measure where it sits so it can be
	// drawn between its real seats -- under the body at the inboard spring
	// position and on top of the axle -- rather than at the pivot, which put the
	// coils a metre and a half off to the side and lying flat.
	if (SM != nullptr)
	{
		const FBox SB = SM->GetBoundingBox();
		SpringMeshCentre = SB.GetCenter();
		SpringMeshHeight = FMath::Max(SB.GetSize().Z, 1.f);
		SpringLateral = FMath::Abs(SpringMeshCentre.X);   // mesh X is lateral
		SpringTopZ = MeshGroundZ + SB.Max.Z;
		const float WheelR = (Wheels.Num() > 0) ? Wheels[0].Radius : 46.5f;
		SpringBottomAboveHub = SB.Min.Z - WheelR;          // mesh hub height = wheel radius
	}

	// Same for the axle housing: exported at the front-axle position.
	if (AM != nullptr)
	{
		const FBox AB = AM->GetBoundingBox();
		AxleMeshCentre = AB.GetCenter();
		const float WheelR = (Wheels.Num() > 0) ? Wheels[0].Radius : 46.5f;
		AxleBelowHub = WheelR - AxleMeshCentre.Z;
	}

	UE_LOG(LogVehicle, Log, TEXT("Visual components created (body=%s wheel=%s axle=%s spring=%s)"),
		(BM != nullptr) ? TEXT("ok") : TEXT("missing"),
		(WM != nullptr) ? TEXT("ok") : TEXT("missing"),
		(AM != nullptr) ? TEXT("ok") : TEXT("missing"),
		(SM != nullptr) ? TEXT("ok") : TEXT("missing"));
}

// ---------------------------------------------------------------------------
// Tick and fixed-step loop
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	if (!BodyCollision->IsSimulatingPhysics()) return;

	const double StartTime = FPlatformTime::Seconds();

	// Accumulate and run a bounded number of fixed steps.
	//
	// NOTE: the substepping was removed deliberately. Running the suspension
	// several times per frame while Chaos integrates the rigid body only ONCE per
	// frame is not a smaller version of the same simulation -- it is a different,
	// unstable one. Within a frame the body's velocity does not change, so a
	// damper evaluated N times produces N times the force in the same direction
	// while the spring's compression barely moves, and the pair stops being a
	// spring-damper at all: the damper wins, the wheel force pins to its cap, and
	// the vehicle is thrown. Every attempt to fix this by adjusting coefficients
	// made it worse, because the coefficients were not the problem.
	//
	// One evaluation per frame against the real frame delta keeps the damper a
	// function of the velocity it is actually resisting.
	//
	// The step is clamped exactly as Chaos clamps its own (MaxPhysicsDeltaTime):
	// on a long frame Chaos only integrates that much, and a wheel solver stepping
	// further than the body it drives would spin the wheels up faster than the
	// body moves -- slip that is pure artefact.
	AccumulatedTime = 0.f;
	const float MaxStep = FMath::Max(UPhysicsSettings::Get()->MaxPhysicsDeltaTime, 1.f / 240.f);
	StepPhysics(FMath::Min(DeltaSeconds, MaxStep));
	const int32 Steps = 1;

	// Apply everything the substeps accumulated, exactly once per frame. See the
	// note in StepPhysics: applying inside the substep loop is what multiplied
	// the wheel loads and threw the vehicle.
	ApplyAccumulatedForces(DeltaSeconds);

	CurrentSubsteps = Steps;
	LastPhysicsMs = (float)((FPlatformTime::Seconds() - StartTime) * 1000.0);

	// Visuals update once per frame, not once per substep.
	UpdateWheelVisuals(DeltaSeconds);

	// Bookkeeping for the HUD and for the roll-over recovery point.
	const FVector Vel = BodyCollision->GetPhysicsLinearVelocity();
	SpeedKph = Vel.Size() * 0.036f;

	const FRotator Rot = GetActorRotation();
	BodyPitchDeg = Rot.Pitch;
	BodyRollDeg = Rot.Roll;

	// Track a "safe" pose: upright, near-stationary, four wheels roughly level.
	if (FMath::Abs(BodyRollDeg) < 25.f && FMath::Abs(BodyPitchDeg) < 30.f && SpeedKph < 5.f)
	{
		LastSafeLocation = GetActorLocation();
		LastSafeRotation = FRotator(0.f, Rot.Yaw, 0.f);
	}

	// One line a second of what the vehicle is doing. Cheap, and it is what makes
	// a report like "it drives off on its own" checkable from a log.
	TelemetryTimer += DeltaSeconds;
	if (TelemetryTimer >= 1.f)
	{
		TelemetryTimer = 0.f;
		int32 Grounded = 0;
		for (const FWheelState& WS : WheelStates)
		{
			Grounded += WS.bGrounded ? 1 : 0;
		}
		const FVector Loc = GetActorLocation();
		const float FwdKph = FVector::DotProduct(Vel, GetActorForwardVector()) * 0.036f;
		float RutMax = 0.f, SinkMax = 0.f, SlipMax = 0.f, PressMax = 0.f;
		for (const FWheelState& WS : WheelStates)
		{
			RutMax = FMath::Max(RutMax, WS.RutDepth);
			SinkMax = FMath::Max(SinkMax, WS.Sinkage);
			SlipMax = FMath::Max(SlipMax, FMath::Abs(WS.SlipRatio));
			PressMax = FMath::Max(PressMax, WS.NormalLoad / 626.f * 100.f);
		}
		UE_LOG(LogVehicle, Log,
			TEXT("TELEM pos=(%.0f,%.0f,%.0f) yaw=%.0f pitch=%.1f roll=%.1f kph=%.1f fwd_kph=%.1f ")
			TEXT("gear=%d%s rpm=%.0f wheel_w=%.1f thr=%.2f brk=%.2f steer=%.2f grounded=%d ")
			TEXT("rut=%.1f sink=%.1f slip=%.2f press=%.0f cls=%d fps=%.0f"),
			Loc.X, Loc.Y, Loc.Z, Rot.Yaw, Rot.Pitch, Rot.Roll, SpeedKph, FwdKph,
			Drive.GearIndex, bAutomaticGearbox ? TEXT("A") : TEXT("M"), Drive.EngineRPM, Drive.WheelAvgOmega,
			ThrottleInput, BrakeInput, SteeringInput,
			Grounded,
			RutMax, SinkMax, SlipMax, PressMax,
			WheelStates.Num() > 0 ? (int32)WheelStates[0].SoilClass : -1,
			DeltaSeconds > 0.f ? 1.f / DeltaSeconds : 0.f);
	}
}

void AOffroadVehiclePawn::StepPhysics(float DT)
{
	// 0. Pedals. The throttle axis is signed (W = +1, S = -1). As in most driving
	//    games, the "back" pedal brakes while the vehicle rolls forward and, once
	//    it has stopped, selects reverse and drives backwards; the forward pedal
	//    does the mirror image in reverse. The brake axis (tests, gamepad trigger)
	//    always just brakes.
	//
	//    The engine only ever sees |throttle|, so a negative throttle used to mean
	//    FULL forward drive: holding S braked and drove forward at the same time.
	{
		const float FwdKph = FVector::DotProduct(BodyCollision->GetPhysicsLinearVelocity(),
		                                         GetActorForwardVector()) * 0.036f;
		const bool bReverseGear = (Drive.GearIndex == 0);
		const float Ahead = FMath::Max(ThrottleTarget, 0.f);
		const float Back = FMath::Max(-ThrottleTarget, 0.f);

		const float WantThrottle = bReverseGear ? Back : Ahead;
		const float WantBrake = FMath::Max(BrakeTarget, bReverseGear ? Ahead : Back);

		// Nearly stopped and asking for the other direction: change direction.
		const bool bAskOpposite = bReverseGear ? (Ahead > 0.5f) : (Back > 0.5f);
		if (bAskOpposite && FMath::Abs(FwdKph) < 2.f)
		{
			DirectionRequestTime += DT;
			if (DirectionRequestTime > 0.3f && Drive.ShiftTimer <= 0.f)
			{
				Drive.GearIndex = bReverseGear ? 1 : 0;
				Drive.ShiftTimer = Gearbox.ShiftTime;
				OnGearChanged.Broadcast(Drive.GearIndex);
				DirectionRequestTime = 0.f;
			}
		}
		else
		{
			DirectionRequestTime = 0.f;
		}

		// Smooth so the physics is not fed a step function.
		const float Rate = 6.0f;
		ThrottleInput = FMath::FInterpTo(ThrottleInput, WantThrottle, DT, Rate);
		BrakeInput = FMath::FInterpTo(BrakeInput, WantBrake, DT, Rate);
	}
	SteeringInput = FMath::FInterpTo(SteeringInput, SteeringTarget, DT, 9.0f);

	// 1. Drivetrain (engine, gears, differential torque split)
	UpdateAutoShift(DT);
	UpdateDrivetrain(DT);

	// 2. Ground probing and suspension
	for (int32 i = 0; i < WheelStates.Num(); ++i)
	{
		UpdateSuspension(i, DT);
	}

	// 3. Tyre forces, using the suspension load we just computed
	for (int32 i = 0; i < WheelStates.Num(); ++i)
	{
		UpdateTyreForces(i, DT);
	}

	// 3b. Locked differentials are a rigid coupling, so they hold after the
	//     wheels have been integrated too -- not only at the start of the step,
	//     which let an unloaded wheel run away from its partner within the step.
	//     The average conserves the pair's angular momentum.
	if (WheelStates.Num() >= 4)
	{
		auto Lock = [this](int32 A, int32 B)
		{
			const float Avg = 0.5f * (WheelStates[A].AngularVelocity + WheelStates[B].AngularVelocity);
			WheelStates[A].AngularVelocity = Avg;
			WheelStates[B].AngularVelocity = Avg;
		};
		if (Drive.FrontDiff == EDiffLock::Locked) { Lock(0, 1); }
		if (Drive.RearDiff == EDiffLock::Locked)  { Lock(2, 3); }
		if (Drive.DriveMode == EDriveMode::FourWheelDrive && Drive.CenterDiff == EDiffLock::Locked)
		{
			const float Front = 0.5f * (WheelStates[0].AngularVelocity + WheelStates[1].AngularVelocity);
			const float Rear = 0.5f * (WheelStates[2].AngularVelocity + WheelStates[3].AngularVelocity);
			const float Shift = 0.5f * (Rear - Front);
			for (int32 i = 0; i < 2; ++i) { WheelStates[i].AngularVelocity += Shift; }
			for (int32 i = 2; i < 4; ++i) { WheelStates[i].AngularVelocity -= Shift; }
		}
	}

	// 4. Accumulate the resulting forces. They are NOT applied here: this runs
	//    once per substep, and a body can take several substeps in one frame.
	//    Chaos sums AddForceAtLocation calls until it integrates, so applying
	//    inside the loop multiplied every wheel force by the substep count --
	//    the "load 7.1 M against a weight of 2.1 M" the protocol tests measured,
	//    and the reason the vehicle was launched instead of settling.
	AccumulateWheelForces(DT);

	// 5. Winch
	UpdateWinch(DT);
}

// ---------------------------------------------------------------------------
// Drivetrain
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::UpdateAutoShift(float DT)
{
	if (!bAutomaticGearbox || Drive.GearIndex < 1 || Drive.ShiftTimer > 0.f || Wheels.Num() == 0)
	{
		return;
	}

	// Engine speed implied by the wheels in a given gear. The displayed engine
	// RPM includes a free-rev term from the throttle, so it reads high at any
	// throttle opening and would never call for a downshift.
	const float Range = Drive.bLowRange ? Gearbox.LowRangeRatio : Gearbox.HighRangeRatio;
	auto RpmIn = [&](int32 Gear)
	{
		return FMath::Abs(Drive.WheelAvgOmega) * FMath::Abs(Gearbox.Ratios[Gear]) * Range
		     * Gearbox.FinalDrive * 60.f / (2.f * PI);
	};
	const float Rpm = RpmIn(Drive.GearIndex);
	const int32 MaxGear = Gearbox.Ratios.Num() - 1;

	// Upshifting while the tyres spin would only bog the engine down; wait for
	// grip. Wheel speed well above ground speed means wheelspin.
	const float WheelKph = FMath::Abs(Drive.WheelAvgOmega) * Wheels[0].Radius * 0.036f;
	const bool bWheelspin = WheelKph > SpeedKph * 1.35f + 3.f;

	// Thresholds leave a wide gap: after an upshift the RPM drops by the ratio
	// step (0.57 to 0.78 here), which still lands above the downshift point, so
	// the box cannot hunt between two gears.
	int32 NewGear = Drive.GearIndex;
	if (Drive.GearIndex < MaxGear && Rpm > Engine.MaxRPM * 0.86f && ThrottleInput > 0.25f && !bWheelspin)
	{
		NewGear = Drive.GearIndex + 1;
	}
	else if (Drive.GearIndex > 1 && Rpm < Engine.MaxRPM * 0.36f)
	{
		NewGear = Drive.GearIndex - 1;
	}

	if (NewGear != Drive.GearIndex)
	{
		UE_LOG(LogVehicle, Log, TEXT("Auto shift %d -> %d at %.0f wheel rpm, %.1f km/h"),
			Drive.GearIndex, NewGear, Rpm, SpeedKph);
		Drive.GearIndex = NewGear;
		Drive.ShiftTimer = Gearbox.ShiftTime;
		OnGearChanged.Broadcast(Drive.GearIndex);
	}
}

void AOffroadVehiclePawn::UpdateDrivetrain(float DT)
{
	if (Drive.ShiftTimer > 0.f) Drive.ShiftTimer -= DT;

	const float Ratio = Gearbox.Ratios.IsValidIndex(Drive.GearIndex)
		? Gearbox.Ratios[Drive.GearIndex] : 1.f;
	const float Range = Drive.bLowRange ? Gearbox.LowRangeRatio : Gearbox.HighRangeRatio;
	const float TotalRatio = Ratio * Range * Gearbox.FinalDrive;

	// Average driven-wheel speed, converted back to crank speed.
	float OmegaSum = 0.f; int32 DrivenCount = 0;
	auto ConsiderWheel = [&](int32 i)
	{
		if (Wheels[i].bDriven && WheelStates.IsValidIndex(i))
		{
			OmegaSum += WheelStates[i].AngularVelocity;
			++DrivenCount;
		}
	};
	ConsiderWheel(0); ConsiderWheel(1); ConsiderWheel(2); ConsiderWheel(3);

	const float WheelOmega = DrivenCount > 0 ? OmegaSum / DrivenCount : 0.f;
	Drive.WheelAvgOmega = WheelOmega;

	// Crank speed implied by the wheels through the current gear.
	const float ImpliedCrank = WheelOmega * TotalRatio;

	// The engine is coupled to the wheels through the clutch. Throttle raises
	// the crank speed toward the torque-peak region; the gearbox then either
	// accepts that (driving the vehicle) or the tyres slip.
	const float ThrottleAbs = FMath::Abs(ThrottleInput);

	// Blend between free-rev and wheel-coupled, so neutral-ish behaviour is
	// possible but the vehicle is always connected enough to creep.
	const float Coupling = 0.82f;
	float TargetOmega = FMath::Lerp(EngineOmega, ImpliedCrank, Coupling);

	// Free-rev contribution from throttle
	const float IdleOmega = Engine.IdleRPM * 2.f * PI / 60.f;
	const float MaxOmega = Engine.MaxRPM * 2.f * PI / 60.f;
	TargetOmega += ThrottleAbs * (MaxOmega - IdleOmega) * 0.55f;

	TargetOmega = FMath::Clamp(TargetOmega, Engine.StallRPM * 2.f * PI / 60.f, MaxOmega);
	// Never below idle: at a standstill the clutch slips rather than dragging the
	// engine down (it used to sit at 0.75 x idle, 562 rpm, whenever stopped).
	EngineOmega = FMath::Clamp(TargetOmega, IdleOmega, MaxOmega);
	Drive.EngineRPM = EngineOmega * 60.f / (2.f * PI);

	// Engine torque at this speed, scaled by throttle.
	const float TorqueNcm = SampleEngineTorque(Engine, Drive.EngineRPM) * ThrottleAbs;
	Drive.EngineTorqueNcm = TorqueNcm;

	// --- distribute to the wheels through the transfer case
	// Engine torque is split front/rear according to the drive mode and centre
	// differential. With an open centre diff the torque to an axle is limited by
	// what that axle can absorb, which we approximate from wheel speed
	// difference: a spinning axle is not absorbing torque.
	float FrontShare = 0.f, RearShare = 0.f;
	switch (Drive.DriveMode)
	{
	case EDriveMode::TwoWheelDriveRear:  FrontShare = 0.f;   RearShare = 1.f;   break;
	case EDriveMode::TwoWheelDriveFront: FrontShare = 1.f;   RearShare = 0.f;   break;
	default:                             FrontShare = 0.5f;  RearShare = 0.5f;  break;
	}

	if (Drive.DriveMode == EDriveMode::FourWheelDrive && Drive.CenterDiff != EDiffLock::Locked)
	{
		// Approximate a centre differential's torque limitation.
		const float FrontOmega = (Wheels[0].bDriven && Wheels[1].bDriven)
			? 0.5f * (WheelStates[0].AngularVelocity + WheelStates[1].AngularVelocity) : 0.f;
		const float RearOmega = (Wheels[2].bDriven && Wheels[3].bDriven)
			? 0.5f * (WheelStates[2].AngularVelocity + WheelStates[3].AngularVelocity) : 0.f;

		// An open centre differential delivers equal torque to both axles; a
		// limited-slip one biases torque toward the axle turning SLOWER -- the one
		// that still has grip -- up to its bias ratio.
		//
		// This used to move torque toward the FASTER axle (with an 8:1 range for
		// "open"), so whichever axle broke loose was fed up to 89% of the torque
		// and spun harder: 4WD with an open centre travelled less than 2WD.
		const float Bias = (Drive.CenterDiff == EDiffLock::LimitedSlip) ? 2.4f : 1.0f;
		const float Diff = FMath::Abs(RearOmega) - FMath::Abs(FrontOmega);   // > 0: rear spinning faster
		const float T = FMath::Clamp(0.5f + Diff * 0.35f, 1.f / (1.f + Bias), Bias / (1.f + Bias));
		FrontShare = T;
		RearShare = 1.f - T;
	}

	// Axle torque after the gearbox.
	const float AxleTotal = TorqueNcm * TotalRatio * Gearbox.Efficiency;

	float FrontAxleTorque = AxleTotal * FrontShare;
	float RearAxleTorque = AxleTotal * RearShare;

	// --- split each axle between its two wheels
	// Open: equal torque. Locked: equal speed, enforced below by coupling omega.
	float TF[2] = { 0.f, 0.f };
	float TR[2] = { 0.f, 0.f };

	{
		const float Oa = WheelStates[0].AngularVelocity;
		const float Ob = WheelStates[1].AngularVelocity;
		SplitAxleTorque(FrontAxleTorque, Oa, Ob, Drive.FrontDiff, 3000.f, TF[0], TF[1]);
	}
	{
		const float Oa = WheelStates[2].AngularVelocity;
		const float Ob = WheelStates[3].AngularVelocity;
		SplitAxleTorque(RearAxleTorque, Oa, Ob, Drive.RearDiff, 3000.f, TR[0], TR[1]);
	}

	// Store per-wheel drive torque for the wheel integrator.
	LastDriveTorque[0] = TF[0];
	LastDriveTorque[1] = TF[1];
	LastDriveTorque[2] = TR[0];
	LastDriveTorque[3] = TR[1];

	// Rev limiter, per wheel. Through the current gearing the engine cannot turn
	// a wheel faster than its own maximum speed allows -- in low first that is
	// about 10 rad/s. Nothing enforced it: drive torque kept flowing to a wheel
	// spinning at any speed, so a wheel that broke loose ran up to the 400 rad/s
	// numerical clamp (a tyre surface at 670 km/h), and the excavation and churn
	// it drove were absurd. Torque now fades out over the last 8% before the
	// limit, wheel by wheel, so an open differential still lets one wheel spin
	// -- just not past what the engine can turn it.
	{
		const float MaxWheelOmega = (Engine.MaxRPM * 2.f * PI / 60.f) / FMath::Max(FMath::Abs(TotalRatio), 0.01f);
		for (int32 i = 0; i < 4 && i < WheelStates.Num(); ++i)
		{
			const float W = FMath::Abs(WheelStates[i].AngularVelocity);
			const float Cut = 1.f - FMath::Clamp((W - 0.92f * MaxWheelOmega) / (0.08f * MaxWheelOmega), 0.f, 1.f);
			LastDriveTorque[i] *= Cut;
		}
	}

	// --- locked differentials force equal wheel speeds on that axle
	auto LockAxle = [&](int32 A, int32 B)
	{
		const float Avg = 0.5f * (WheelStates[A].AngularVelocity + WheelStates[B].AngularVelocity);
		WheelStates[A].AngularVelocity = Avg;
		WheelStates[B].AngularVelocity = Avg;
	};
	if (Drive.FrontDiff == EDiffLock::Locked) LockAxle(0, 1);
	if (Drive.RearDiff == EDiffLock::Locked)  LockAxle(2, 3);

	if (Drive.DriveMode == EDriveMode::FourWheelDrive && Drive.CenterDiff == EDiffLock::Locked)
	{
		// Centre locked: front and rear axles turn together.
		const float Avg = 0.25f * (WheelStates[0].AngularVelocity + WheelStates[1].AngularVelocity
		                         + WheelStates[2].AngularVelocity + WheelStates[3].AngularVelocity);
		for (int32 i = 0; i < 4; ++i) WheelStates[i].AngularVelocity = Avg;
	}

	// Wheels on a non-driven axle receive no torque and simply roll.
	if (Drive.DriveMode == EDriveMode::TwoWheelDriveRear)
	{
		LastDriveTorque[0] = 0.f; LastDriveTorque[1] = 0.f;
	}
	else if (Drive.DriveMode == EDriveMode::TwoWheelDriveFront)
	{
		LastDriveTorque[2] = 0.f; LastDriveTorque[3] = 0.f;
	}
}

// ---------------------------------------------------------------------------
// Ground probing
// ---------------------------------------------------------------------------

bool AOffroadVehiclePawn::ProbeGround(int32 WheelIndex, const FVector& HubLocation,
                                      FVector& OutPoint, FVector& OutNormal,
                                      float& OutSurfaceZ, bool& bOutSoil) const
{
	const FWheelSetup& W = Wheels[WheelIndex];
	const UWorld* World = GetWorld();
	if (World == nullptr) return false;

	const FVector Fwd = GetActorForwardVector();
	const FVector Right = GetActorRightVector();

	// Contact patch half-extents, in cm. Realistic for a 33 cm wide tyre on a
	// 46.5 cm radius wheel: the footprint is roughly 30 x 20 cm at road pressure.
	const float HalfLen = FMath::Max(W.Radius * 0.30f, 6.f);   // longitudinal
	const float HalfWid = FMath::Max(W.Width * 0.34f, 5.f);   // lateral

	const int32 NA = FMath::Max(1, ContactSamplesAlong);
	const int32 NW = FMath::Max(1, ContactSamplesAcross);

	// How far below the hub we are willing to look: a little past maximum droop,
	// plus a margin for the distance the body can travel within one substep.
	//
	// The margin used to be a flat 30 cm, which is less than the vehicle moves in
	// one 1/120 s step at even a modest speed (at 100 km/h that is ~23 cm, and
	// during a hard landing far more). If the ground is further below the hub than
	// the probe reaches, ProbeGround reports no contact, the suspension produces
	// no force, and the vehicle keeps falling -- a self-reinforcing miss. The
	// margin is now scaled by current speed.
	const float SpeedCmS = BodyCollision ? BodyCollision->GetPhysicsLinearVelocity().Size() : 0.f;
	const float StepMargin = FMath::Clamp(SpeedCmS * (1.f / 120.f) * 3.f, 30.f, 600.f);
	const float TraceDown = W.SuspensionRestLength + W.MaxDroop + W.Radius + StepMargin;
	const float TraceUp = 40.f;

	FCollisionQueryParams Params(SCENE_QUERY_STAT(VehicleGround), false, this);
	Params.bReturnPhysicalMaterial = true;

	float BestHeight = -BIG_NUMBER;
	FVector BestPoint = HubLocation;
	FVector BestNormal = FVector::UpVector;
	float BestPatchZ = HubLocation.Z;
	bool bBestSoil = false;

	for (int32 ia = 0; ia < NA; ++ia)
	{
		for (int32 iw = 0; iw < NW; ++iw)
		{
			// Sample offsets spread across the patch, centred on the hub.
			const float FA = (NA == 1) ? 0.f : (float(ia) / (NA - 1) - 0.5f) * 2.f;
			const float FW = (NW == 1) ? 0.f : (float(iw) / (NW - 1) - 0.5f) * 2.f;

			FVector SampleTop = HubLocation
				+ Fwd * (FA * HalfLen)
				+ Right * (FW * HalfWid)
				+ FVector(0.f, 0.f, TraceUp);

			const FVector Start = SampleTop;
			const FVector End = SampleTop - FVector(0.f, 0.f, TraceDown + TraceUp);

			// --- channel A: the soil field (authoritative for deformable ground)
			float SoilSurfaceZ = -BIG_NUMBER;
			bool bSoilHit = false;
			if (SoilField != nullptr)
			{
				const FSoilSample S = SoilField->SampleSoil(FVector(SampleTop.X, SampleTop.Y, 0.f));
				// The soil is a heightfield, so it always has a height. It only
				// counts as contact within the probe's reach, exactly like the
				// swept geometry below: without this a wheel 180 m in the air
				// "touched" the ground under it, the suspension produced its full
				// force there, and the vehicle was flung skyward.
				if (S.bValid && S.SurfaceZ <= Start.Z && S.SurfaceZ >= End.Z)
				{
					SoilSurfaceZ = S.SurfaceZ;
					bSoilHit = true;
				}
			}

			// --- channel B: solid geometry (rocks, logs, tree roots, bridges)
			FHitResult Hit;
			const bool bHit = World->SweepSingleByChannel(
				Hit, Start, End, FQuat::Identity, ECC_WorldStatic,
				FCollisionShape::MakeSphere(FMath::Max(ProbeRadius, 2.f)), Params);

			const float SolidZ = bHit ? Hit.ImpactPoint.Z : -BIG_NUMBER;

			// The wheel rests on whichever is higher: solid obstacle, or soil.
			const float ThisZ = FMath::Max(bSoilHit ? SoilSurfaceZ : -BIG_NUMBER, SolidZ);
			if (ThisZ <= -BIG_NUMBER * 0.5f) continue;

			if (ThisZ > BestHeight)
			{
				BestHeight = ThisZ;
				BestPoint = FVector(SampleTop.X, SampleTop.Y, ThisZ);
				BestPatchZ = ThisZ;
				bBestSoil = (bSoilHit && SoilSurfaceZ >= SolidZ);
				BestNormal = (bHit && SolidZ > SoilSurfaceZ)
					? Hit.ImpactNormal
					: FVector::UpVector;
			}
		}
	}

	if (BestHeight <= -BIG_NUMBER * 0.5f)
	{
		return false;
	}

	OutPoint = BestPoint;
	OutNormal = BestNormal;
	OutSurfaceZ = BestPatchZ;
	bOutSoil = bBestSoil;
	return true;
}

// ---------------------------------------------------------------------------
// Suspension
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::UpdateSuspension(int32 WheelIndex, float DT)
{
	const FWheelSetup& W = Wheels[WheelIndex];
	FWheelState& S = WheelStates[WheelIndex];

	S.PrevSuspensionLength = S.SuspensionLength;

	const FTransform BodyXf = BodyCollision->GetComponentTransform();

	// Attachment point in world space.
	const FVector AttachWorld = BodyXf.TransformPosition(W.AttachPoint);

	// Suspension axis in world space (downward).
	FVector AxisWorld = BodyXf.TransformVector(SuspensionAxis).GetSafeNormal();
	if (AxisWorld.IsNearlyZero()) AxisWorld = -FVector::UpVector;

	// The hub is at the top of the suspension plus the current length along the axis.
	const FVector PrevHub = AttachWorld + AxisWorld * S.SuspensionLength;

	FVector ContactPoint = FVector::ZeroVector;
	FVector ContactNormal = FVector::UpVector;
	float SurfaceHeight = 0.f;
	bool bSoil = false;

	// A wheel can only bear on the ground while its suspension points roughly
	// downward. On its side or upside down the probe would still find ground
	// below the hub and push the body through its roof.
	const bool bUpright = AxisWorld.Z < -0.25f;

	const bool bFound = bUpright && ProbeGround(WheelIndex, PrevHub, ContactPoint, ContactNormal,
	                                            SurfaceHeight, bSoil);

	S.bGrounded = bFound;

	if (bFound)
	{
		// Project the contact point onto the suspension axis to find the hub height.
		const FVector ToContact = ContactPoint - AttachWorld;
		const float AlongAxis = FVector::DotProduct(ToContact, AxisWorld);

		// The centre of the wheel sits one radius above the contact point.
		// Along the axis that is radius / cos(theta); for small camber, ~radius.
		const float AxisDotUp = FMath::Abs(FVector::DotProduct(AxisWorld, ContactNormal));
		const float ProjRadius = (AxisDotUp > 0.20f) ? (W.Radius / AxisDotUp) : W.Radius * 4.f;

		const float DesiredHubAlong = AlongAxis - ProjRadius;

		// Clamp to the travel limits of the suspension.
		const float MinLen = W.SuspensionRestLength - W.MaxCompression;
		const float MaxLen = W.SuspensionRestLength + W.MaxDroop;
		float NewLength = FMath::Clamp(DesiredHubAlong, MinLen, MaxLen);

		S.SuspensionLength = NewLength;
		S.ContactPoint = ContactPoint;
		S.ContactNormal = ContactNormal;
		S.SurfaceZ = SurfaceHeight;
		S.WorldLocation = AttachWorld + AxisWorld * NewLength;
	}
	else
	{
		// Airborne: the suspension extends fully to its droop limit.
		S.SuspensionLength = W.SuspensionRestLength + W.MaxDroop;
		S.WorldLocation = AttachWorld + AxisWorld * S.SuspensionLength;
		S.ContactPoint = S.WorldLocation - AxisWorld * W.Radius;
	}

	// --- spring force
	// Compression measured from the rest length: positive means compressed.
	const float Compression = W.SuspensionRestLength - S.SuspensionLength;

	// Damper velocity is the rate at which the body closes on the ground along
	// the suspension axis. The suspension axis points DOWN, so the component of
	// body velocity along it is:
	//
	//     positive  ->  body moving down, suspension compressing
	//     negative  ->  body moving up,   suspension extending
	//
	// The sign matters more than anything else here. An earlier version treated
	// "positive" as extending and applied the REBOUND coefficient to it, so while
	// the vehicle fell the damper pulled it DOWN. Once it fell faster than
	// k*MaxCompression / DampingRebound (about 196 cm/s, reached in 0.2 s under
	// gravity) the damper cancelled the entire spring and the clamp below floored
	// the wheel force at zero -- the suspension then produced no support at all
	// and the vehicle fell freely for the rest of the frame, which is what the
	// 1000+ km/h HUD readings were.
	{
		const FVector AttachVel = BodyCollision->GetPhysicsLinearVelocityAtPoint(AttachWorld);
		// AxisWorld points DOWN, so the component of the body's velocity along it
		// is positive when the body is moving toward the ground, i.e. when the
		// suspension is compressing. That is the sign the damper needs: it must
		// resist the CURRENT direction of travel, using the compression rate while
		// compressing and the rebound rate while extending.
		//
		// An earlier version had this backwards, so a falling vehicle was damped
		// with the rebound coefficient -- the damper pulled it DOWN. Once it fell
		// faster than k*MaxCompression / DampingRebound (about 196 cm/s, reached in
		// 0.2 s under gravity) the damper cancelled the whole spring, the floor
		// clamped the wheel force to zero, and the suspension gave no support at
		// all. That was the 1000+ km/h free fall.
		S.SuspensionVelocity = FVector::DotProduct(AttachVel, AxisWorld);
	}

	float Force = 0.f;
	if (bFound || Compression > 0.f)
	{
		// Main spring. Zero force at rest length; the weight of the vehicle holds
		// it part-way compressed, which is where the static ride height comes from.
		Force = W.SpringRate * FMath::Max(Compression, 0.f);

		// Damper: opposes the rate of change of length.
		//
		// Force is a SCALAR applied along the contact normal (up), so a positive
		// value supports the vehicle. S.SuspensionVelocity is the body's velocity
		// along the suspension axis, which points down:
		//
		//     Vel > 0  ->  body moving down  ->  compressing
		//     Vel < 0  ->  body moving up    ->  extending
		//
		// The damper must resist whatever is happening, so it must push UP while
		// compressing and DOWN while extending. Both fall out of a single
		// expression: DampForce = +Vel * coefficient.
		//
		//     compressing: Vel > 0  ->  positive  ->  pushes up    (resists)
		//     extending:   Vel < 0  ->  negative  ->  pushes down  (resists)
		//
		// The sign here was inverted (DampForce = -Vel * coefficient). That made
		// the damper push UP while the suspension extended -- helping it extend
		// instead of resisting. Any small upward disturbance then grew: the wheel
		// force climbed, the body was thrown, and because the force is applied at
		// the contact point (below the centre of mass) it also applied a torque
		// that tumbled the vehicle. The telemetry showed exactly that -- force
		// pinned at its clamp (1,580,250 then 2,107,000) while vZ rose to 497 cm/s.
		const float Vel = S.SuspensionVelocity;
		const float DampCoef = (Vel > 0.f) ? W.DampingCompression : W.DampingRebound;
		float DampForce = Vel * DampCoef;

		// The damper is a bounded device: it cannot exceed a multiple of the
		// corner's static load, or a hard landing would fling the vehicle.
		const float StaticLoad = (MassKg * 980.f) / 4.f;
		DampForce = FMath::Clamp(DampForce, -StaticLoad * 3.f, StaticLoad * 3.f);

		// A damper may never overpower the spring while the wheel is compressed:
		// that would let the wheel pull the body down into the ground it is
		// standing on. Clamping it to the spring's own magnitude keeps the total
		// non-negative whenever there is compression to resist.
		DampForce = FMath::Max(DampForce, -Force);

		Force += DampForce;

		// Progressive bump stop in the last quarter of compression travel.
		const float TravelUsed = FMath::Max(Compression, 0.f);
		const float BumpStart = W.MaxCompression * 0.75f;
		if (TravelUsed > BumpStart)
		{
			const float BumpComp = TravelUsed - BumpStart;
			Force += W.BumpStopRate * BumpComp * BumpComp / FMath::Max(W.MaxCompression - BumpStart, 1.f);
		}

		// A spring cannot pull the body down.
		Force = FMath::Max(Force, 0.f);

		// Hard cap so a single substep cannot launch the vehicle. The cap is
		// ~4x the static load per wheel.
		const float StaticLoadPerWheel = (MassKg * 980.f) / 4.f;
		Force = FMath::Min(Force, StaticLoadPerWheel * 4.f);
	}

	S.SuspensionForce = Force;

	// --- record surface state for telemetry, tyre model and dirt effects
	if (bFound && SoilField != nullptr)
	{
		const FSoilSample Smp = SoilField->SampleSoil(ContactPoint);
		S.Sinkage = FMath::Max(0.f, (Smp.FirmZ + Smp.SoftDepth) - Smp.SurfaceZ);
		S.RutDepth = Smp.RutDepth;
		S.TractionScale = Smp.TractionScale;
		S.BearingCapacity = Smp.BearingCapacity;
		S.SoilClass = Smp.SoilClass;
	}
	else
	{
		S.Sinkage = 0.f;
		S.RutDepth = 0.f;
		S.TractionScale = 1.f;
		S.SoilClass = ESoilClass::FirmRoad;
	}
}

// ---------------------------------------------------------------------------
// Tyre forces
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::UpdateTyreForces(int32 WheelIndex, float DT)
{
	const FWheelSetup& W = Wheels[WheelIndex];
	FWheelState& S = WheelStates[WheelIndex];

	// ---- brake torque this substep
	float BrakeTorque = 0.f;
	{
		// Service brake acts on all wheels; the handbrake locks the rears hard.
		// Per wheel, in N*cm: 2000 N*m of service brake is enough to lock a wheel on
		// dry ground (~0.8 g overall), and the handbrake can hold the rears on a
		// steep slope. (26000/34000 N*cm, i.e. 260/340 N*m, managed about 0.1 g.)
		const float ServiceBrake = BrakeInput * 200000.f;
		const float HandBrake = (bHandbrakeOn && W.bHandbrake) ? 250000.f : 0.f;
		BrakeTorque = (ServiceBrake + HandBrake) * NcmToUnreal;
	}
	LastBrakeTorque[WheelIndex] = BrakeTorque;

	if (!S.bGrounded)
	{
		// Airborne (or no contact): no tyre force. The wheel keeps spinning under
		// whatever drive torque it has, minus a small bearing drag. This is what
		// makes an airborne wheel spin up — we do NOT fake contact for it.
		const float BearingDrag = 12.f;   // N*cm
		float Omega = S.AngularVelocity;
		const float NetTorque = (LastDriveTorque[WheelIndex] - FMath::Sign(Omega) * BearingDrag) * NcmToUnreal;
		const float Alpha = NetTorque / FMath::Max(W.Inertia, 1.f);
		Omega += Alpha * DT;

		// Free-spinning wheels are slowed by the brake too.
		if (BrakeTorque > 0.f)
		{
			const float BrakeDecel = BrakeTorque / FMath::Max(W.Inertia, 1.f);
			const float OmegaMag = FMath::Abs(Omega);
			const float NewMag = FMath::Max(0.f, OmegaMag - BrakeDecel * DT);
			Omega = Sign(Omega) * NewMag;
		}

		S.AngularVelocity = FMath::Clamp(Omega, -400.f, 400.f);
		S.LongitudinalForce = 0.f;
		S.LateralForce = 0.f;
		S.NormalLoad = 0.f;
		S.SlipRatio = 0.f;
		S.SlipAngle = 0.f;
		S.RollingResistance = 0.f;
		S.bSpinning = FMath::Abs(Omega) > 4.f;
		return;
	}

	S.NormalLoad = S.SuspensionForce;

	// ---- contact patch frame
	const FVector Fwd = GetActorForwardVector();
	const FVector Right = GetActorRightVector();

	// Steering: only steerable wheels turn; the rest point straight ahead.
	if (W.bSteerable)
	{
		const float MaxSteerDeg = 34.f;
		// Steering authority falls off with speed, as it must for stability.
		const float SpeedFactor = FMath::Lerp(1.0f, 0.45f, Saturate(SpeedKph / 85.f));
		S.SteerAngle = FMath::DegreesToRadians(SteeringInput * MaxSteerDeg * SpeedFactor);
	}
	else
	{
		S.SteerAngle = 0.f;
	}

	const float CosS = FMath::Cos(S.SteerAngle);
	const float SinS = FMath::Sin(S.SteerAngle);
	const FVector WheelFwd = (Fwd * CosS + Right * SinS).GetSafeNormal();
	const FVector WheelRight = (Right * CosS - Fwd * SinS).GetSafeNormal();

	// ---- contact patch velocity
	const FVector BodyVelAtContact = BodyCollision->GetPhysicsLinearVelocityAtPoint(S.ContactPoint);
	const FVector PatchVel = BodyVelAtContact + FVector(0.f, 0.f, 0.f);

	const float VLong = FVector::DotProduct(PatchVel, WheelFwd);
	const float VLat = FVector::DotProduct(PatchVel, WheelRight);

	// Wheel surface speed at the contact patch.
	const float VWheel = S.AngularVelocity * W.Radius;

	// ---- slip ratio, guarded against divide-by-zero at near-zero speed
	const float SpeedRef = FMath::Max(FMath::Abs(VLong), 60.f);   // 0.6 m/s floor
	float SlipRatio = (VWheel - VLong) / SpeedRef;

	// Blend to a force-based definition at very low speed so that a spinning
	// wheel on the spot still generates a real, finite force.
	if (FMath::Abs(VLong) < 60.f)
	{
		const float LowBlend = 1.f - Saturate(FMath::Abs(VLong) / 60.f);
		SlipRatio = FMath::Lerp(SlipRatio, (VWheel - VLong) / 60.f, LowBlend);
	}
	SlipRatio = FMath::Clamp(SlipRatio, -6.f, 6.f);

	// ---- slip angle
	const float SlipAngle = FMath::Atan2(-VLat, FMath::Max(FMath::Abs(VLong), 25.f));
	S.SlipAngle = SlipAngle;
	S.SlipRatio = SlipRatio;
	S.PatchSpeed = PatchVel.Size();

	// ---- friction available
	// Base from the tyre, modulated by the soil's traction scale. Soil that has
	// been churned to mud offers far less grip than firm road.
	const float GripScale = FMath::Clamp(S.TractionScale, 0.05f, 1.2f);
	const float MuLong = W.PeakFriction * GripScale;
	const float MuLat  = W.PeakFriction * GripScale * 0.94f;

	// ---- normalise vertical load; grip rises then falls slightly with load
	const float NominalLoad = FMath::Max((MassKg * 9.81f * 100.f) / 4.f, 1.f);
	const float LoadRatio = S.NormalLoad / NominalLoad;
	const float LoadSensitivity = FMath::Lerp(1.0f, 0.85f, Saturate((LoadRatio - 1.f) * 0.5f));
	const float VerticalFactor = FMath::Max(0.f, FMath::Pow(FMath::Max(LoadRatio, 0.f), 0.9f)) * LoadSensitivity;

	// ---- tyre curves
	//
	// TyreCurve returns a MAGNITUDE; the force takes the sign of the slip:
	//
	//   SlipRatio > 0  (tyre surface faster than the ground) -> pushes forward
	//   SlipRatio < 0  (tyre slower, i.e. braking)           -> pushes back
	//   SlipAngle < 0  (patch sliding right, VLat > 0)       -> pushes left
	//
	// The sign used to be dropped (Fx = +curve, Fy = -curve). Every slip then
	// pushed the vehicle forward and to the left -- a braking wheel accelerated
	// it, and its reaction slowed the wheel further, so the push grew. With the
	// throttle closed the vehicle drove itself off at ~0.6 g, rolled, and left
	// the map; that is what the telemetry showed.
	float Fx = FMath::Sign(SlipRatio) * TyreCurve(SlipRatio, MuLong, W.PeakSlipRatio) * S.NormalLoad * LoadSensitivity;
	float Fy = FMath::Sign(SlipAngle) * TyreCurve(SlipAngle, MuLat, W.PeakSlipAngle) * S.NormalLoad * LoadSensitivity;
	(void)VerticalFactor;

	// ---- friction ellipse: longitudinal and lateral share a limited budget
	const float MaxF = FMath::Max(MuLong, 0.01f) * S.NormalLoad + 1e-3f;
	const float Magnitude = FMath::Pow(FMath::Abs(Fx) / MaxF, FrictionEllipseExp)
	                      + FMath::Pow(FMath::Abs(Fy) / MaxF, FrictionEllipseExp);
	if (Magnitude > 1.f)
	{
		const float Scale = FMath::Pow(Magnitude, -1.f / FrictionEllipseExp);
		Fx *= Scale;
		Fy *= Scale;
	}

	// ---- explicit-step stability
	//
	// Near zero slip the tyre curve is extremely stiff: a 1 cm/s slide already
	// asks for a large fraction of the wheel load. Applied for a whole frame,
	// such a force overshoots, reverses the slide and grows -- the parked vehicle
	// buzzes, and the wheel's spin (whose inertia is tiny next to the body's)
	// oscillates by tens of rad/s per step. So each component is capped at the
	// force that would bring its slide to rest within this step: friction can
	// stop motion, never reverse it. Above a few km/h of slide the cap is far
	// above the curve and has no effect.
	{
		const float DTc = FMath::Max(DT, 1e-3f);
		const float CornerMass = FMath::Max(S.NormalLoad / 980.f, MassKg * 0.05f);   // kg on this wheel

		const float LatStop = CornerMass * FMath::Abs(VLat) / DTc;
		Fy = FMath::Clamp(Fy, -LatStop, LatStop);

		// Longitudinally the slide is between tyre and ground, and the wheel's own
		// rotation takes part: a free wheel spins up rather than dragging the body.
		// A wheel held by its brake does not, so only the body's mass resists.
		const bool bWheelHeld = (LastBrakeTorque[WheelIndex] > 0.f) && FMath::Abs(S.AngularVelocity) < 0.5f;
		const float InvMass = 1.f / CornerMass
			+ (bWheelHeld ? 0.f : (W.Radius * W.Radius) / FMath::Max(W.Inertia, 1.f));
		const float LongStop = FMath::Abs(VWheel - VLong) / (InvMass * DTc);
		Fx = FMath::Clamp(Fx, -LongStop, LongStop);
	}

	// The wheel's reaction torque comes from the slip force only. Rolling
	// resistance below acts on the body; folding it into the reaction would
	// have it spin the wheel FORWARD, which then slips and cancels it.
	const float FxSlip = Fx;

	// ---- rolling resistance and sinkage drag
	// This is a real force opposing motion, and it is what makes deep mud slow
	// the vehicle down for a physical reason rather than a trigger volume.
	if (SoilField != nullptr)
	{
		const FSoilSample Smp = SoilField->SampleSoil(S.ContactPoint);
		const float SinkCm = Smp.Sinkage;
		// The soil's own coefficient (SampleSoil): ~1.4% on a road, ~10% in deep
		// mud with the tyre sunk 10 cm. (A fixed 0.018 + 0.0022/cm used to apply
		// everywhere, so deep mud cost about 3% -- a 4x4 accelerated through it in
		// second gear.)
		const float BaseRR = Smp.RollingResistance;
		const float RRForce = BaseRR * S.NormalLoad;
		S.RollingResistance = RRForce;

		// Oppose the direction of travel at the patch -- and, like friction, it may
		// stop the vehicle but not push it backwards.
		const float CornerMass = FMath::Max(S.NormalLoad / 980.f, MassKg * 0.05f);
		const float RRStop = CornerMass * FMath::Abs(VLong) / FMath::Max(DT, 1e-3f);
		const float VDir = (FMath::Abs(VLong) > 1.f) ? FMath::Sign(VLong) : 0.f;
		Fx -= VDir * FMath::Min(RRForce, RRStop);

		// Sinkage also creates a "wall" the tyre must climb out of laterally,
		// which is what keeps a vehicle tracking in a rut rather than sliding out.
		const float RutWallStiffness = 42.f + 6.f * SinkCm;
		const float RutDepth = Smp.RutDepth;
		if (RutDepth > 1.f)
		{
			// Lateral restoring force proportional to how deep the rut is.
			const float LatRestore = -VLat * RutWallStiffness * 0.06f * FMath::Min(RutDepth / 10.f, 2.f);
			Fy += FMath::Clamp(LatRestore, -MaxF * 0.5f, MaxF * 0.5f);
		}
	}

	S.LongitudinalForce = Fx;
	S.LateralForce = Fy;

	// ---- wheel rotational dynamics
	// Tyre reaction torque about the axle, plus drive torque, minus brake torque.
	const float TyreTorque = -FxSlip * W.Radius;
	float NetTorque = LastDriveTorque[WheelIndex] * NcmToUnreal + TyreTorque;

	// Apply brake as a decelerating torque that cannot reverse the wheel.
	float Omega = S.AngularVelocity;
	const float Alpha = NetTorque / FMath::Max(W.Inertia, 1.f);
	Omega += Alpha * DT;

	if (BrakeTorque > 0.f)
	{
		const float BrakeDecel = BrakeTorque / FMath::Max(W.Inertia, 1.f);
		const float OmegaMag = FMath::Abs(Omega);
		const float NewMag = FMath::Max(0.f, OmegaMag - BrakeDecel * DT);
		Omega = Sign(Omega) * NewMag;
	}

	// Cap wheel speed so nothing can run away numerically.
	const float MaxOmega = 400.f;   // rad/s, ~ 3000 rpm at the wheel
	Omega = FMath::Clamp(Omega, -MaxOmega, MaxOmega);
	S.AngularVelocity = Omega;

	// Spinning = the tyre surface is moving much faster than the ground.
	S.bSpinning = FMath::Abs(SlipRatio) > 0.28f && FMath::Abs(VWheel) > 200.f;

	// ---- deform the ground under this wheel
	if (SoilField != nullptr)
	{
		// Contact pressure: load over patch area, in a gameplay "kPa" scale.
		const float PatchAreaCm2 = (2.f * FMath::Max(W.Radius * 0.30f, 6.f))
		                         * (2.f * FMath::Max(W.Width * 0.34f, 5.f));
		// FSoilClassParams::BearingStrength is in kPa, so the load must be converted:
		// 1 Unreal force unit = 1 kg*cm/s^2 = 0.01 N, and 1 N/cm^2 = 10 kPa, so
		//
		//     kPa = (units / area_cm2) * 0.01 * 10 = (units / area_cm2) * 0.1
		//
		// A resting 2150 kg 4x4 on 626 cm^2 patches comes out at ~84 kPa, which is
		// a real off-road tyre's contact pressure. The coefficient here was 10, i.e.
		// a hundred times too high, so every surface in the game failed instantly
		// and ruts appeared whether or not they should.
		const float Pressure = (S.NormalLoad / FMath::Max(PatchAreaCm2, 1.f)) * 0.1f;

		SoilField->ApplyWheelLoad(
			S.ContactPoint,
			WheelFwd, WheelRight,
			FMath::Max(W.Radius * 0.30f, 6.f),
			FMath::Max(W.Width * 0.34f, 5.f),
			Pressure,
			PatchVel.Size(),
			SlipRatio,
			DT);

		// Re-read the surface after deformation, so the wheel immediately sits in
		// the rut it just made rather than one step behind.
		const FSoilSample After = SoilField->SampleSoil(S.ContactPoint);
		S.RutDepth = After.RutDepth;
		S.Sinkage = FMath::Max(0.f, (After.FirmZ + After.SoftDepth) - After.SurfaceZ);
		S.TractionScale = After.TractionScale;
		S.SoilClass = After.SoilClass;
	}
}

// ---------------------------------------------------------------------------
// Applying forces to the rigid body
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::AccumulateWheelForces(float DT)
{
	for (int32 i = 0; i < WheelStates.Num(); ++i)
	{
		const FWheelSetup& W = Wheels[i];
		FWheelState& S = WheelStates[i];

		if (!S.bGrounded)
		{
			continue;
		}

		const FVector Fwd = GetActorForwardVector();
		const FVector Right = GetActorRightVector();

		const float CosS = FMath::Cos(S.SteerAngle);
		const float SinS = FMath::Sin(S.SteerAngle);
		const FVector WheelFwd = (Fwd * CosS + Right * SinS).GetSafeNormal();
		const FVector WheelRight = (Right * CosS - Fwd * SinS).GetSafeNormal();

		// Normal (suspension) force along the contact normal.
		const FVector NormalF = S.ContactNormal * S.SuspensionForce;

		// Tyre forces in the wheel's own frame.
		const FVector LongF = WheelFwd * S.LongitudinalForce;
		const FVector LatF = WheelRight * S.LateralForce;

		const FVector Total = NormalF + LongF + LatF;

		// Each wheel's force is applied at its own contact point, below. They used
		// to be summed into ONE force at a load-weighted mean point. The sum is
		// right but the moment is not: opposing longitudinal forces cancel in the
		// sum while their moments do not, and lateral forces shifted the mean point
		// sideways -- spurious roll and pitch moments that grew into rollovers.
		PendingForces.Add(Total);
		PendingPoints.Add(S.ContactPoint);
	}
}

void AOffroadVehiclePawn::ApplyAccumulatedForces(float FrameDelta)
{
	if (BodyCollision == nullptr)
	{
		return;
	}

	// Everything the wheels and the winch computed this frame, each force at its
	// own point of application, applied exactly once.
	for (int32 i = 0; i < PendingForces.Num(); ++i)
	{
		BodyCollision->AddForceAtLocation(PendingForces[i], PendingPoints[i]);
	}
	PendingForces.Reset();
	PendingPoints.Reset();

	// Underbody contact: when the hull itself reaches the ground (high-centred on
	// a ridge, or sunk to the frame in mud) it carries load and drags. Three
	// points along the frame rails, in body space so they pitch and roll with the
	// vehicle.
	//
	// The previous version probed 40 cm below the actor origin and added 8 cm of
	// margin, while the ground at rest is only ~35 cm below the origin -- so it
	// was in "contact" all the time, pushing up and applying a -V*220 drag on
	// every frame of normal driving.
	if (SoilField != nullptr)
	{
		const FTransform BodyXf = BodyCollision->GetComponentTransform();
		const float HalfLen = BodyCollision->GetUnscaledBoxExtent().X * 0.75f;
		const float Xs[3] = { -HalfLen, 0.f, HalfLen };

		// A firm spring/damper per point: three of them hold the full weight at
		// about 9 cm of penetration, which is what "beached" should feel like.
		constexpr float HullK = 80000.f;     // per cm of penetration
		constexpr float HullC = 9000.f;      // per cm/s of closing speed
		constexpr float HullMu = 0.55f;      // hull-on-soil sliding friction

		for (float X : Xs)
		{
			const FVector Under = BodyXf.TransformPosition(FVector(X, 0.f, UnderbodyZ));
			const FSoilSample Smp = SoilField->SampleSoil(Under);
			if (!Smp.bValid || Under.Z >= Smp.SurfaceZ)
			{
				continue;
			}

			const float Depth = Smp.SurfaceZ - Under.Z;
			const FVector PointVel = BodyCollision->GetPhysicsLinearVelocityAtPoint(Under);
			const float Normal = FMath::Clamp(Depth * HullK - PointVel.Z * HullC, 0.f, MassKg * 980.f);

			// Friction opposes horizontal sliding, bounded by the normal load.
			FVector Slide(PointVel.X, PointVel.Y, 0.f);
			const float SlideSpeed = Slide.Size();
			FVector Friction = FVector::ZeroVector;
			if (SlideSpeed > 1.f)
			{
				// Coulomb friction, capped so one frame can remove at most a fifth
				// of the sliding speed: an explicit step must not reverse it.
				const float StopCap = 0.2f * MassKg * SlideSpeed / FMath::Max(FrameDelta, 1e-3f);
				Friction = -Slide / SlideSpeed * FMath::Min(HullMu * Normal, StopCap);
			}

			BodyCollision->AddForceAtLocation(FVector(0.f, 0.f, Normal) + Friction, Under);

			// A beached hull ploughs the ground under it too.
			SoilField->ApplyPointLoad(Under, 55.f, SlideSpeed, 45.f, FrameDelta);
		}
	}

}

// ---------------------------------------------------------------------------
// Visuals
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::UpdateWheelVisuals(float DeltaSeconds)
{
	const FTransform BodyXf = BodyCollision->GetComponentTransform();

	for (int32 i = 0; i < WheelStates.Num(); ++i)
	{
		const FWheelSetup& W = Wheels[i];
		FWheelState& S = WheelStates[i];

		// Accumulate the visual spin from the ACTUAL angular velocity, so during
		// wheelspin the wheels turn even when the vehicle is stationary.
		S.WheelAngle += S.AngularVelocity * DeltaSeconds;
		// Keep the angle bounded without a visible jump.
		if (S.WheelAngle > 2.f * PI * 1000.f) S.WheelAngle -= 2.f * PI * 1000.f;
		if (S.WheelAngle < -2.f * PI * 1000.f) S.WheelAngle += 2.f * PI * 1000.f;

		if (WheelMeshes.IsValidIndex(i) && WheelMeshes[i] != nullptr)
		{
			UStaticMeshComponent* WC = WheelMeshes[i];

			// Wheel centre in world space, from the solver.
			const FVector HubWorld = S.WorldLocation;

			// Steering is a separate transform from suspension and spin.
			const FRotator BodyRot = GetActorRotation();
			const float SteerDeg = FMath::RadiansToDegrees(S.SteerAngle);

			// Build the wheel rotation: body yaw -> steer -> spin about the axle.
			FRotator WheelRot = BodyRot;
			WheelRot.Yaw += SteerDeg;

			// Spin about the axle axis. In Unreal with X forward, the axle runs
			// along Y, so spin is roll about the local Y axis (pitch in FRotator
			// terms is about Y, but for a right-handed rolling wheel we use Roll
			// about the forward axis is wrong; the correct visual is rotation
			// about the lateral axis, which we compose explicitly below).
			const FQuat BaseQ = WheelRot.Quaternion();
			const FVector AxleWorld = BaseQ.GetRightVector();   // body right = axle
			const FQuat SpinQ(AxleWorld, S.WheelAngle);
			const FQuat FinalQ = SpinQ * BaseQ;

			WC->SetWorldLocationAndRotation(HubWorld, FinalQ);

			// If a rotation offset is configured for the imported mesh, apply it
			// in the component's local space so the tyre faces sensibly.
			if (!WheelMeshRotationOffset.IsNearlyZero())
			{
				const FQuat OffQ = WheelMeshRotationOffset.Quaternion();
				WC->SetWorldRotation((FinalQ * OffQ).Rotator());
			}
		}

		// Spring visual: from its seat under the body down to its perch on the
		// axle, inboard of the wheel. Its coil axis is mesh Z, so the rotation
		// aligns Z with the spring line (Dir.Rotation() aligned X with it, which
		// laid the coils flat), and it is stretched to the live length.
		if (SpringMeshes.IsValidIndex(i) && SpringMeshes[i] != nullptr)
		{
			const float Side = (W.AttachPoint.Y >= 0.f) ? 1.f : -1.f;
			const FVector BodyUp = BodyXf.GetUnitAxis(EAxis::Z);
			const FVector BodyRight = BodyXf.GetUnitAxis(EAxis::Y);
			const FVector BodyFwd = BodyXf.GetUnitAxis(EAxis::X);

			const FVector Top = BodyXf.TransformPosition(
				FVector(W.AttachPoint.X, Side * SpringLateral, SpringTopZ));
			const FVector Bottom = S.WorldLocation
				+ BodyUp * SpringBottomAboveHub
				+ BodyRight * (Side * SpringLateral - W.AttachPoint.Y);

			const FVector Axis = (Top - Bottom).GetSafeNormal(KINDA_SMALL_NUMBER, BodyUp);
			const float Len = FVector::Dist(Top, Bottom);
			const float ScaleZ = FMath::Clamp(Len / SpringMeshHeight, 0.25f, 2.f);
			const FQuat R = FRotationMatrix::MakeFromZX(Axis, BodyFwd).ToQuat();

			// Place the geometry's centre, not the pivot, at the midpoint.
			const FVector Mid = (Top + Bottom) * 0.5f;
			const FVector Loc = Mid - R.RotateVector(SpringMeshCentre * FVector(1.f, 1.f, ScaleZ));
			SpringMeshes[i]->SetWorldLocationAndRotation(Loc, R);
			SpringMeshes[i]->SetRelativeScale3D(FVector(1.f, 1.f, ScaleZ));
		}
	}

	// Axle housings span their wheel pair and tilt with it, which is exactly what
	// a solid axle does. The housing's long axis is mesh X, so the mesh offset
	// rotation maps it onto the hub-to-hub line.
	const FQuat MeshOffQ = BodyMeshRotationOffset.Quaternion();
	for (int32 Ax = 0; Ax < 2; ++Ax)
	{
		if (!AxleMeshes.IsValidIndex(Ax) || AxleMeshes[Ax] == nullptr) continue;
		const int32 A = Ax * 2;
		const int32 B = A + 1;
		if (!WheelStates.IsValidIndex(B)) continue;

		const FVector PA = WheelStates[A].WorldLocation;   // left
		const FVector PB = WheelStates[B].WorldLocation;   // right
		const FVector BodyUp = BodyXf.GetUnitAxis(EAxis::Z);
		const FVector Across = (PB - PA).GetSafeNormal(KINDA_SMALL_NUMBER, BodyXf.GetUnitAxis(EAxis::Y));

		const FQuat R = FRotationMatrix::MakeFromYZ(Across, BodyUp).ToQuat() * MeshOffQ;
		const FVector Mid = (PA + PB) * 0.5f - BodyUp * AxleBelowHub;
		const FVector Loc = Mid - R.RotateVector(AxleMeshCentre);
		AxleMeshes[Ax]->SetWorldLocationAndRotation(Loc, R);
	}
}

// ---------------------------------------------------------------------------
// Winch
// ---------------------------------------------------------------------------

bool AOffroadVehiclePawn::FindWinchAnchor(FVector& OutAnchor, AActor*& OutActor)
{
	// The winch is aimed by the driver's steering: looking down the steering
	// direction is what players expect, and it makes the winch usable without a
	// separate aiming mode.
	const FRotator BodyRot = GetActorRotation();
	const FVector AimYaw = FRotator(0.f, BodyRot.Yaw + FMath::RadiansToDegrees(SteeringInput) * 0.5f, 0.f).Vector();

	const FVector WinchWorld = BodyCollision->GetComponentTransform().TransformPosition(WinchPointLocal);

	// Search a fan of directions around the aim, so a slightly-off tree is still
	// found. Each probe is a sphere sweep so thin trunks are not missed.
	const float FanAngles[] = { 0.f, -12.f, 12.f, -26.f, 26.f, -42.f, 42.f };
	const float WinchHeight = WinchWorld.Z;
	const float MaxReach = WinchMaxCableCm;

	struct FHit { float Dist; FVector Point; AActor* Actor; bool bValid; };
	FHit Best{ 0.f, FVector::ZeroVector, nullptr, false };

	UWorld* World = GetWorld();
	if (World == nullptr) return false;

	FCollisionQueryParams Params(SCENE_QUERY_STAT(WinchProbe), false, this);
	Params.bReturnPhysicalMaterial = false;

	for (float Angle : FanAngles)
	{
		const FVector Dir = FRotator(0.f, AimYaw.Rotation().Yaw + Angle, 0.f).Vector();
		const FVector Start = WinchWorld;
		const FVector End = Start + Dir * MaxReach;

		// Primary trace: does the line hit anything solid?
		// The sweep starts 120 cm out. Anything closer is no use as an anchor,
		// and a single sweep reports only its FIRST hit: starting at the bumper,
		// a bush beside it was that hit, got rejected as too close, and the tree
		// behind it was never seen.
		constexpr float MinReach = 120.f;
		FHitResult Hit;
		const bool bHit = World->SweepSingleByChannel(
			Hit, Start + Dir * MinReach, End, FQuat::Identity, ECC_WorldStatic,
			FCollisionShape::MakeSphere(14.f), Params);

		if (!bHit || Hit.GetActor() == nullptr) continue;

		const float D = FVector::Dist(Start, Hit.ImpactPoint);
		if (D > MaxReach) continue;

		if (!Best.bValid || D < Best.Dist)
		{
			Best.Dist = D;
			Best.Point = Hit.ImpactPoint;
			Best.Actor = Hit.GetActor();
			Best.bValid = true;
		}
	}

	if (!Best.bValid)
	{
		OutAnchor = FVector::ZeroVector;
		OutActor = nullptr;
		return false;
	}

	// A valid anchor must actually be mass-bearing or anchored. A small rock or
	// a light log would just be dragged toward the vehicle, which is both
	// unphysical and a poor experience. Report it rather than pulling through it.
	float AnchorMass = 0.f;
	bool bIsAnchorable = false;
	if (UPrimitiveComponent* Prim = Cast<UPrimitiveComponent>(Best.Actor->GetRootComponent()))
	{
		AnchorMass = Prim->GetMass();
		bIsAnchorable = Prim->IsSimulatingPhysics() && AnchorMass > 900.f;
	}
	if (!bIsAnchorable)
	{
		// Static world geometry (trees, rocks, the ground itself) is anchorable.
		bIsAnchorable = !Best.Actor->GetRootComponent()->IsSimulatingPhysics();
	}

	if (!bIsAnchorable)
	{
		OutAnchor = FVector::ZeroVector;
		OutActor = nullptr;
		WinchMessage = FString::Printf(
			TEXT("Anchor too light (%.0f kg) - it would just be dragged. Find a tree or rock."),
			AnchorMass);
		return false;
	}

	OutAnchor = Best.Point;
	OutActor = Best.Actor;
	return true;
}

void AOffroadVehiclePawn::ToggleWinch()
{
	if (bWinchAttached)
	{
		ReleaseWinch();
		return;
	}

	FVector Anchor;
	AActor* AnchorActor = nullptr;
	if (!FindWinchAnchor(Anchor, AnchorActor))
	{
		if (WinchMessage.IsEmpty())
		{
			WinchMessage = TEXT("No anchor within cable range - aim at a tree or rock.");
		}
		OnWinchStateChanged.Broadcast(false);
		return;
	}

	const FVector WinchWorld = BodyCollision->GetComponentTransform().TransformPosition(WinchPointLocal);
	const float Dist = FVector::Dist(WinchWorld, Anchor);

	if (Dist > WinchMaxCableCm)
	{
		WinchMessage = FString::Printf(TEXT("Anchor out of range (%.1f m, cable is %.1f m)"),
			Dist / 100.f, WinchMaxCableCm / 100.f);
		OnWinchStateChanged.Broadcast(false);
		return;
	}

	bWinchAttached = true;
	WinchAnchor = Anchor;
	WinchAnchorActor = AnchorActor;
	WinchCableLength = Dist;
	WinchMessage = FString::Printf(TEXT("Winch attached at %.1f m"), Dist / 100.f);
	OnWinchStateChanged.Broadcast(true);
}

void AOffroadVehiclePawn::ReleaseWinch()
{
	if (!bWinchAttached) return;
	bWinchAttached = false;
	WinchAnchorActor = nullptr;
	WinchForce = 0.f;
	WinchMessage = TEXT("Winch released");
	OnWinchStateChanged.Broadcast(false);
}

void AOffroadVehiclePawn::UpdateWinch(float DT)
{
	if (!bWinchAttached) return;

	const FVector WinchWorld = BodyCollision->GetComponentTransform().TransformPosition(WinchPointLocal);
	FVector ToAnchor = WinchAnchor - WinchWorld;
	const float Dist = ToAnchor.Size();
	if (Dist < 1.f) { ReleaseWinch(); return; }
	const FVector Dir = ToAnchor / Dist;

	// The cable can pull but never push. Stretch = how far the anchor is beyond
	// the paid-out cable length:
	//
	//   Stretch <= 0   slack: no force; the drum takes up the slack at line speed
	//   Stretch  > 0   taut:  tension from the cable's stretch, up to the rating
	//
	// (This test was inverted: a stretched cable was treated as slack and pulled
	// with nothing, while a slack one pulled with a fixed 26 kN.)
	//
	// The motor reels in at line speed until the tension reaches the rating, where
	// it stalls. So the vehicle is dragged no faster than the line and with no
	// more than the rated force -- it still has to drive itself out of a deep hole.
	const float ReelRate = WinchMaxPullSpeed;      // cm/s
	const float Stretch = Dist - WinchCableLength;

	// Tension is worked out in newtons; AddForce takes kg*cm/s^2, i.e. N * 100.
	// Passing newtons straight through made a 43 kN winch pull with 430 N.
	constexpr float NewtonsToUnreal = 100.f;

	float Tension = 0.f;
	if (Stretch < -1.f)
	{
		// Clearly slack: no force; the drum winds in the slack. It stops at the
		// distance, and from there on the branch below reels in and the line
		// tightens. (With "<= 0" here a line wound exactly taut stayed in this
		// branch forever whenever the vehicle stood still on its brakes.)
		WinchCableLength = FMath::Max(WinchCableLength - ReelRate * DT, FMath::Max(Dist, 60.f));
		WinchForce = 0.f;
	}
	else
	{
		const float SpringK = 850.f;      // N per cm of stretch
		const float DampC = 90.f;         // N per cm/s of stretching
		const FVector Vel = BodyCollision->GetPhysicsLinearVelocityAtPoint(WinchWorld);
		const float StretchRate = FVector::DotProduct(Vel, -Dir);   // > 0: moving away from the anchor

		Tension = FMath::Clamp(SpringK * Stretch + DampC * StretchRate, 0.f, WinchMaxForceN);
		if (Tension < WinchMaxForceN * 0.98f)
		{
			WinchCableLength = FMath::Max(WinchCableLength - ReelRate * DT, 60.f);
		}
		WinchForce = Tension;

		PendingForces.Add(Dir * Tension * NewtonsToUnreal);
		PendingPoints.Add(WinchWorld);
	}

	// Cable cannot exceed its rated length: if we somehow stretch past it, pull back.
	if (Dist > WinchMaxCableCm + 100.f)
	{
		WinchMessage = TEXT("Cable at maximum length - move closer or release");
	}

	// Winch rigging visual: draw the cable so the player can see it.
	if (GetWorld() != nullptr)
	{
		DrawDebugLine(GetWorld(), WinchWorld, WinchAnchor,
			WinchForce > 1.f ? FColor(255, 140, 40) : FColor(160, 160, 160),
			false, -1.f, 0, WinchForce > 1.f ? 4.f : 2.f);
	}
}

// ---------------------------------------------------------------------------
// Recovery and test helpers
// ---------------------------------------------------------------------------

void AOffroadVehiclePawn::RecoverAtBase()
{
	// This is the explicitly labelled recover option, NOT a replacement for the
	// winch: it returns the vehicle to the last safe pose it occupied while
	// upright. If it has never been upright and safe, it goes to the spawn point.
	ReleaseWinch();

	const FVector Target = (LastSafeLocation - GetActorLocation()).Size() < 4000.f
		? LastSafeLocation : FVector(0.f, 0.f, 200.f);

	BodyCollision->SetPhysicsLinearVelocity(FVector::ZeroVector);
	BodyCollision->SetPhysicsAngularVelocityInDegrees(FVector::ZeroVector);

	SetActorLocationAndRotation(Target + FVector(0.f, 0.f, 60.f), FRotator(0.f, LastSafeRotation.Yaw, 0.f), false, nullptr, ETeleportType::TeleportPhysics);

	bRecoveryUsed = true;
	WinchMessage = TEXT("Recovered to last safe position");
}

void AOffroadVehiclePawn::ResetForTest()
{
	// Tests must be reproducible: this clears both the soil and the vehicle.
	ReleaseWinch();

	if (SoilField == nullptr)
	{
		for (TActorIterator<ASoilField> It(GetWorld()); It; ++It) { SoilField = *It; break; }
	}
	if (SoilField != nullptr)
	{
		SoilField->ResetAllSoils();
	}

	SetTestLoadKg(0.f);

	BodyCollision->SetPhysicsLinearVelocity(FVector::ZeroVector);
	BodyCollision->SetPhysicsAngularVelocityInDegrees(FVector::ZeroVector);
	BodyCollision->SetCenterOfMass(CentreOfMassOffset);

	FVector Spawn(FVector::ZeroVector);
	FRotator SpawnRot = GetActorRotation();
	if (APlayerStart* PS = Cast<APlayerStart>(UGameplayStatics::GetActorOfClass(GetWorld(), APlayerStart::StaticClass())))
	{
		Spawn = PS->GetActorLocation();
		SpawnRot = PS->GetActorRotation();
	}
	SetActorLocationAndRotation(Spawn, SpawnRot, false, nullptr, ETeleportType::TeleportPhysics);

	ThrottleInput = 0.f; ThrottleTarget = 0.f;
	BrakeInput = 0.f; BrakeTarget = 0.f;
	SteeringInput = 0.f; SteeringTarget = 0.f;
	bHandbrakeOn = true;

	Drive.GearIndex = 1;
	Drive.bLowRange = false;
	Drive.DriveMode = EDriveMode::FourWheelDrive;
	Drive.CenterDiff = EDiffLock::Locked;
	Drive.FrontDiff = EDiffLock::Open;
	Drive.RearDiff = EDiffLock::Open;

	for (FWheelState& S : WheelStates)
	{
		S.AngularVelocity = 0.f;
		S.SuspensionLength = Wheels[0].SuspensionRestLength;
		S.PrevSuspensionLength = Wheels[0].SuspensionRestLength;
	}

	for (int32 i = 0; i < 4; ++i)
	{
		LastDriveTorque[i] = 0.f;
		LastBrakeTorque[i] = 0.f;
	}
}

void AOffroadVehiclePawn::SetTestLoadKg(float Kg)
{
	TestLoadKg = Kg;
	BodyCollision->SetMassOverrideInKg(NAME_None, MassKg + FMath::Max(Kg, 0.f), true);

	// Move the centre of mass as load is added, so the suspension visibly sags
	// and weight distribution actually changes.
	// In the load bed, behind the rear axle line (X is forward; this was
	// (0,-40,20), i.e. 40 cm to the left, which tilted the vehicle sideways).
	const FVector LoadOffset = FVector(-140.f, 0.f, 20.f);
	const float TotalMass = FMath::Max(MassKg + FMath::Max(Kg, 0.f), 1.f);
	const FVector NewCoM = (CentreOfMassOffset * MassKg + LoadOffset * FMath::Max(Kg, 0.f)) / TotalMass;
	BodyCollision->SetCenterOfMass(NewCoM);
}




