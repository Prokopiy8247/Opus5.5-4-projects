// OffroadVehiclePawn.h
// The player's 4x4. A single Chaos rigid body with a custom wheel solver.
//
// The body is a real rigid body: suspension forces are applied at the wheel
// attachment points, so weight transfer, pitch under braking and roll in a
// corner all emerge from where the forces act. The body is never snapped to
// the ground normal and never moved with SetActorLocation during normal driving.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Pawn.h"
#include "OffroadWheel.h"
#include "Drivetrain.h"
#include "../Soil/SoilTypes.h"
#include "OffroadVehiclePawn.generated.h"

class UBoxComponent;
class UStaticMeshComponent;
class ASoilField;
class USpringArmComponent;
class UCameraComponent;
class UAudioComponent;
class UParticleSystemComponent;
class UArrowComponent;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnGearChanged, int32, NewGear);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnWinchStateChanged, bool, bAttached);

UCLASS(ClassGroup = (Mudtrack))
class MUDTRACK_API AOffroadVehiclePawn : public APawn
{
	GENERATED_BODY()

public:
	AOffroadVehiclePawn();

	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;
	virtual void SetupPlayerInputComponent(UInputComponent* PlayerInputComponent) override;

	// =====================================================================
	// Components
	// =====================================================================

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<UBoxComponent> BodyCollision;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<UStaticMeshComponent> BodyMesh;

	/** Wheel visuals, one per wheel, positioned every frame from the solver. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TArray<TObjectPtr<UStaticMeshComponent>> WheelMeshes;

	/** Suspension visual elements (springs/arms/axles), moved with the wheels. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TArray<TObjectPtr<UStaticMeshComponent>> SuspensionMeshes;

	/** Solid axle housings (front, rear): travel with the mean of their wheel pair. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TArray<TObjectPtr<UStaticMeshComponent>> AxleMeshes;

	/** Coil spring visuals, stretched between the attachment and the hub. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TArray<TObjectPtr<UStaticMeshComponent>> SpringMeshes;

	// ---- imported assets (loaded at BeginPlay, so a missing asset is survivable)
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Assets")
	TSoftObjectPtr<UStaticMesh> BodyMeshAsset;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Assets")
	TSoftObjectPtr<UStaticMesh> WheelMeshAsset;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Assets")
	TSoftObjectPtr<UStaticMesh> AxleMeshAsset;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Assets")
	TSoftObjectPtr<UStaticMesh> SpringMeshAsset;

	/**
	 * Orientation correction applied to every imported vehicle mesh (body, axle).
	 *
	 * The Blender meshes are modelled lengthwise along Y with the front axle at
	 * -Y; the vehicle drives along +X. Yaw 90 maps mesh -Y onto +X. Without it
	 * the body and wheels render turned sideways to the direction of travel.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Assets")
	FRotator BodyMeshRotationOffset = FRotator(0.f, 90.f, 0.f);

	/** Orientation correction for the wheel mesh, whose axle is modelled along X. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Assets")
	FRotator WheelMeshRotationOffset = FRotator(0.f, 90.f, 0.f);

	/** Axis (in wheel-mesh local space) the wheel spins about. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Assets")
	FVector WheelSpinAxisLocal = FVector(0.f, 1.f, 0.f);

	// --- imported mesh layout, measured from the meshes in SetupVisualComponents
	//
	// The spring and axle meshes were exported in place on the vehicle rather than
	// centred on their pivots (the spring's geometry sits at (-62,-142.5,72.5)),
	// so they are positioned by their bounding-box centre, not their origin.

	/** Actor-space Z of the mesh ground plane (mesh Z = 0) at static ride height. */
	float MeshGroundZ = 0.f;
	FVector SpringMeshCentre = FVector::ZeroVector;
	float SpringMeshHeight = 45.f;
	float SpringLateral = 62.f;     // spring centreline distance from the vehicle centreline
	float SpringTopZ = 60.f;        // actor-space Z of the spring's upper seat
	float SpringBottomAboveHub = 3.5f;
	FVector AxleMeshCentre = FVector::ZeroVector;
	float AxleBelowHub = 0.f;

	// --- per-frame forces ------------------------------------------------
	//
	// Wheel and winch forces are collected while the solver runs and applied to
	// the rigid body once per frame, each at its own point. Chaos sums
	// AddForceAtLocation calls until it integrates, so applying from inside a
	// repeated solver step would multiply them.
	TArray<FVector> PendingForces;
	TArray<FVector> PendingPoints;

	/** Collect each grounded wheel's force and contact point. */
	void AccumulateWheelForces(float DT);

	/** Apply the collected forces to the body. Called once per frame. */
	void ApplyAccumulatedForces(float FrameDelta);

	/** Create and assign the visual components. Called from BeginPlay. */
	UFUNCTION(BlueprintCallable, Category = "Assets")
	void SetupVisualComponents();

	/**
	 * Idempotent runtime initialisation: mass, centre of mass, gravity, wheel
	 * state, soil lookup, camera. Called from BeginPlay, and also callable
	 * directly by the headless test commandlet, which spawns actors into a world
	 * where BeginPlay may not have been dispatched.
	 */
	UFUNCTION(BlueprintCallable, Category = "Vehicle")
	void InitializeVehicleRuntime();

	/** True once InitializeVehicleRuntime has run. */
	UFUNCTION(BlueprintCallable, Category = "Vehicle")
	bool IsRuntimeInitialized() const { return bRuntimeInitialized; }

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<USpringArmComponent> ChaseArm;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<UCameraComponent> ChaseCamera;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<UCameraComponent> SuspensionCamera;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<UCameraComponent> FreeCamera;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<USpringArmComponent> WinchLine;

	// =====================================================================
	// Setup
	// =====================================================================

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	TArray<FWheelSetup> Wheels;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	FEngineSpec Engine;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	FGearBox Gearbox;

	/**
	 * Shift forward gears automatically from wheel speed. On by default: with a
	 * manual box the vehicle sat on the limiter at 40 km/h in first unless the
	 * player knew to press Q. A manual shift (Q/E) switches to manual; M toggles.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	bool bAutomaticGearbox = true;

	UFUNCTION(BlueprintCallable, Category = "Input")
	void ToggleAutomaticGearbox() { bAutomaticGearbox = !bAutomaticGearbox; }

	/** Mass of the vehicle in kg. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	float MassKg = 2150.f;

	/**
	 * Centre of mass offset from the body origin, cm. Kept low and slightly
	 * rearward. (X is forward: this was (0,-6,-18), which put the mass 6 cm to
	 * the LEFT rather than behind, so the vehicle leaned and pulled to one side.)
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	FVector CentreOfMassOffset = FVector(-6.f, 0.f, -18.f);

	/**
	 * Fixed camera exposure, EV100. The level's sun is set in physical units and
	 * the sky atmosphere scales with it, so one fixed exposure keeps sky, sunlit
	 * ground and shade in a believable ratio. (Auto exposure locked onto the
	 * black first frames of an empty scene and stayed there.)
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "View")
	float ExposureEV100 = 14.0f;

	/** Actor-space Z of the lowest point of the hull, used for grounding. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	float UnderbodyZ = -24.f;

	/** Suspension axis in vehicle space; straight down for this build. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	FVector SuspensionAxis = FVector(0.f, 0.f, -1.f);

	/** Number of contact samples across the patch, per direction. Must be odd. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	int32 ContactSamplesAcross = 3;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	int32 ContactSamplesAlong = 3;

	/** Radius of the probe sphere used for the multi-sample sweep, cm. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	float ProbeRadius = 12.f;

	/** Fixed physics step for the vehicle solver, seconds. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	float PhysicsStep = 1.f / 120.f;

	/** Maximum physics substeps per frame, so a hitch cannot explode the sim. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Vehicle")
	int32 MaxSubsteps = 8;

	// =====================================================================
	// State (read by HUD and tests)
	// =====================================================================

	UPROPERTY(BlueprintReadOnly, Category = "State")
	TArray<FWheelState> WheelStates;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	FDrivetrainState Drive;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	float SpeedKph = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	float BodyPitchDeg = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	float BodyRollDeg = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	bool bHandbrakeOn = false;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	int32 CurrentSubsteps = 0;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	float LastPhysicsMs = 0.f;

	// ---- Winch state
	UPROPERTY(BlueprintReadOnly, Category = "State")
	bool bWinchAttached = false;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	FVector WinchAnchor = FVector::ZeroVector;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	float WinchCableLength = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "State")
	float WinchForce = 0.f;

	/** Last message shown to the player about the winch. */
	UPROPERTY(BlueprintReadOnly, Category = "State")
	FString WinchMessage;

	UPROPERTY(BlueprintAssignable, Category = "Events")
	FOnGearChanged OnGearChanged;

	UPROPERTY(BlueprintAssignable, Category = "Events")
	FOnWinchStateChanged OnWinchStateChanged;

	// =====================================================================
	// Input API (also called directly by the automated tests)
	// =====================================================================

	UFUNCTION(BlueprintCallable, Category = "Input")
	void SetThrottle(float Value);

	UFUNCTION(BlueprintCallable, Category = "Input")
	void SetBrake(float Value);

	UFUNCTION(BlueprintCallable, Category = "Input")
	void SetSteering(float Value);

	UFUNCTION(BlueprintCallable, Category = "Input")
	void SetHandbrake(bool bOn);

	UFUNCTION(BlueprintCallable, Category = "Input")
	void ShiftUp();

	UFUNCTION(BlueprintCallable, Category = "Input")
	void ShiftDown();

	UFUNCTION(BlueprintCallable, Category = "Input")
	void ToggleRange();

	UFUNCTION(BlueprintCallable, Category = "Input")
	void CycleDriveMode();

	UFUNCTION(BlueprintCallable, Category = "Input")
	void CycleCenterDiff();

	UFUNCTION(BlueprintCallable, Category = "Input")
	void CycleAxleDiff(bool bFront);

	UFUNCTION(BlueprintCallable, Category = "Input")
	void ToggleWinch();          // fire / release toward the best anchor

	UFUNCTION(BlueprintCallable, Category = "Input")
	void ReleaseWinch();

	UFUNCTION(BlueprintCallable, Category = "Input")
	void RecoverAtBase();        // explicit, labelled roll-over recovery

	UFUNCTION(BlueprintCallable, Category = "Input")
	void SetCameraMode(int32 Mode);

	UFUNCTION(BlueprintCallable, Category = "Input")
	void CycleCamera();

	/** Throttle/brake/steer values currently applied, for reproducible tests. */
	UPROPERTY(BlueprintReadOnly, Category = "Input")
	float ThrottleInput = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Input")
	float BrakeInput = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Input")
	float SteeringInput = 0.f;

	/** Range and drive options, exposed for the diagnostics screen. */
	UFUNCTION(BlueprintCallable, Category = "State")
	FString GetDrivetrainSummary() const;

	/** Reset the soil field and recentre the vehicle at the base. */
	UFUNCTION(BlueprintCallable, Category = "Debug")
	void ResetForTest();

	/** Add or remove a test load, to show suspension sag under load. */
	UFUNCTION(BlueprintCallable, Category = "Debug")
	void SetTestLoadKg(float Kg);

	UPROPERTY(BlueprintReadOnly, Category = "Debug")
	float TestLoadKg = 0.f;

	/** True once the labelled recovery option has been used this session. */
	UPROPERTY(BlueprintReadOnly, Category = "Debug")
	bool bRecoveryUsed = false;

	/** Camera mode currently active: 0 chase, 1 suspension, 2 free. */
	UPROPERTY(BlueprintReadOnly, Category = "View")
	int32 CameraMode = 0;

protected:
	// ---- solver
	void StepPhysics(float DeltaSeconds);
	void UpdateSuspension(int32 WheelIndex, float DeltaSeconds);
	void UpdateTyreForces(int32 WheelIndex, float DeltaSeconds);
	void UpdateDrivetrain(float DeltaSeconds);
	/** Automatic forward-gear selection from wheel speed (see bAutomaticGearbox). */
	void UpdateAutoShift(float DeltaSeconds);
	void UpdateWheelVisuals(float DeltaSeconds);
	void UpdateWinch(float DeltaSeconds);

	/** Multi-sample ground probe. Returns the best contact height and normal. */
	bool ProbeGround(int32 WheelIndex, const FVector& HubLocation, FVector& OutPoint,
	                 FVector& OutNormal, float& OutSurfaceZ, bool& bOutSoil) const;

	/** Find a usable winch anchor by tracing forward from the winch point. */
	bool FindWinchAnchor(FVector& OutAnchor, AActor*& OutActor);

	AActor* WinchAnchorActor = nullptr;

	// ---- inputs
	float ThrottleTarget = 0.f;
	float BrakeTarget = 0.f;
	float SteeringTarget = 0.f;

	// ---- internals
	float AccumulatedTime = 0.f;
	float TelemetryTimer = 0.f;
	/** How long the driver has asked for the opposite direction while stopped. */
	float DirectionRequestTime = 0.f;
	float EngineOmega = 0.f;      // rad/s at the crank
	float WinchDrumOmega = 0.f;
	bool bRuntimeInitialized = false;
	FVector LastSafeLocation = FVector::ZeroVector;
	FRotator LastSafeRotation = FRotator::ZeroRotator;

	/** Per-wheel drive torque (N*cm) computed by the drivetrain this substep. */
	float LastDriveTorque[4] = { 0.f, 0.f, 0.f, 0.f };

	/** Per-wheel brake torque (N*cm) applied this substep. */
	float LastBrakeTorque[4] = { 0.f, 0.f, 0.f, 0.f };

	UPROPERTY()
	TObjectPtr<ASoilField> SoilField;

	/** Vehicle-space position the winch pulls from. */
	UPROPERTY(EditAnywhere, Category = "Winch")
	FVector WinchPointLocal = FVector(245.f, 0.f, 20.f);   // front bumper (X is forward)

	UPROPERTY(EditAnywhere, Category = "Winch")
	float WinchMaxForceN = 43000.f;

	UPROPERTY(EditAnywhere, Category = "Winch")
	float WinchMaxCableCm = 3200.f;

	UPROPERTY(EditAnywhere, Category = "Winch")
	float WinchMaxPullSpeed = 130.f;   // cm/s of line speed

	UPROPERTY(EditAnywhere, Category = "Winch")
	float WinchSearchRange = 3200.f;

	// ---- input handler names so Enhanced Input / legacy both work
	//
	// The axis handlers run every frame with the current stick/key value -- 0 when
	// nothing is pressed. Once a test or script has taken the pedals through
	// SetThrottle/SetBrake/SetSteering they must not be overwritten by that 0,
	// or every scripted input is cancelled before the vehicle ticks (which is why
	// the protocol tests measured 0 km/h under full throttle).
	bool bExternalControl = false;
	void InputThrottle(float V)  { if (!bExternalControl) { ThrottleTarget = FMath::Clamp(V, -1.f, 1.f); } }
	void InputBrake(float V)     { if (!bExternalControl) { BrakeTarget = FMath::Clamp(V, 0.f, 1.f); } }
	void InputSteer(float V)     { if (!bExternalControl) { SteeringTarget = FMath::Clamp(V, -1.f, 1.f); } }
	void InputHandbrakePressed() { SetHandbrake(!bHandbrakeOn); }
	// A shift by hand means the driver wants to choose gears.
	void InputShiftUp()          { bAutomaticGearbox = false; ShiftUp(); }
	void InputShiftDown()        { bAutomaticGearbox = false; ShiftDown(); }
	void InputToggleAutoGearbox() { ToggleAutomaticGearbox(); }
	void InputToggleRange()      { ToggleRange(); }
	void InputCycleDrive()       { CycleDriveMode(); }
	void InputCycleCenterDiff()  { CycleCenterDiff(); }
	void InputCycleFrontDiff()   { CycleAxleDiff(true); }
	void InputCycleRearDiff()    { CycleAxleDiff(false); }
	void InputWinch()            { ToggleWinch(); }
	void InputReleaseWinch()     { ReleaseWinch(); }
	void InputRecover()          { RecoverAtBase(); }
	void InputCycleCamera()      { CycleCamera(); }
	void InputToggleDiagnostics();
	void InputLoadUp()           { SetTestLoadKg(TestLoadKg + 250.f); }
	void InputLoadDown()         { SetTestLoadKg(FMath::Max(TestLoadKg - 250.f, 0.f)); }
};
