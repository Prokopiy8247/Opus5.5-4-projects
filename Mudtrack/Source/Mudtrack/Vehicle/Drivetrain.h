// Drivetrain.h
// Engine, gearbox, transfer case and differentials.
//
// The differential is a *real* torque split, not a multiplier. With an open
// differential the two wheels on an axle are constrained to share torque, so a
// wheel with no grip spins and the other receives almost nothing. With the
// differential locked they share *speed*, so the gripping wheel can transmit
// the full axle torque. That is exactly the difference the task asks to be
// observable in an unloaded-wheel test.

#pragma once

#include "CoreMinimal.h"
#include "Drivetrain.generated.h"

UENUM(BlueprintType)
enum class EDriveMode : uint8
{
	TwoWheelDriveRear UMETA(DisplayName = "2WD (Rear)"),
	FourWheelDrive    UMETA(DisplayName = "4WD"),
	TwoWheelDriveFront UMETA(DisplayName = "2WD (Front)")
};

UENUM(BlueprintType)
enum class EDiffLock : uint8
{
	Open      UMETA(DisplayName = "Open"),
	LimitedSlip UMETA(DisplayName = "Limited Slip"),
	Locked    UMETA(DisplayName = "Locked")
};

/** Engine torque curve, sampled by RPM. */
USTRUCT(BlueprintType)
struct FEngineSpec
{
	GENERATED_BODY()

	UPROPERTY(EditAnywhere, Category = "Engine") float IdleRPM = 750.f;
	UPROPERTY(EditAnywhere, Category = "Engine") float MaxRPM = 4200.f;
	UPROPERTY(EditAnywhere, Category = "Engine") float StallRPM = 420.f;
	UPROPERTY(EditAnywhere, Category = "Engine") float PeakTorqueNcm = 42000.f;  // 420 Nm
	UPROPERTY(EditAnywhere, Category = "Engine") float PeakTorqueRPM = 1800.f;
	UPROPERTY(EditAnywhere, Category = "Engine") float EngineInertia = 28000.f;
	/** How fast RPM falls back to idle when unloaded, per second. */
	UPROPERTY(EditAnywhere, Category = "Engine") float FreeRevDecay = 1400.f;
};

USTRUCT(BlueprintType)
struct FGearBox
{
	GENERATED_BODY()

	/** Ratio at index 0 is reverse. */
	UPROPERTY(EditAnywhere, Category = "Gearbox")
	TArray<float> Ratios = { -3.20f, 4.10f, 2.35f, 1.48f, 1.00f, 0.78f };

	/** High/low range transfer case ratios. */
	UPROPERTY(EditAnywhere, Category = "Gearbox") float HighRangeRatio = 1.00f;
	UPROPERTY(EditAnywhere, Category = "Gearbox") float LowRangeRatio = 2.72f;

	UPROPERTY(EditAnywhere, Category = "Gearbox") float FinalDrive = 3.85f;
	UPROPERTY(EditAnywhere, Category = "Gearbox") float Efficiency = 0.88f;

	/** Shift time, seconds. */
	UPROPERTY(EditAnywhere, Category = "Gearbox") float ShiftTime = 0.35f;
};

/** Full drivetrain state, evaluated once per physics step. */
USTRUCT(BlueprintType)
struct FDrivetrainState
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") float EngineRPM = 750.f;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") float EngineTorqueNcm = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") int32 GearIndex = 1;   // 0 = reverse
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") bool bLowRange = false;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") bool bClutchEngaged = true;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") float ShiftTimer = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") float WheelAvgOmega = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") EDriveMode DriveMode = EDriveMode::FourWheelDrive;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") EDiffLock CenterDiff = EDiffLock::LimitedSlip;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") EDiffLock FrontDiff = EDiffLock::Open;
	UPROPERTY(BlueprintReadOnly, Category = "Drivetrain") EDiffLock RearDiff = EDiffLock::Open;
};

/**
 * Distribute axle torque between two wheels according to the differential type.
 *
 * @param InTorque        total torque delivered to the axle
 * @param OmegaA/OmegaB   the two wheels' angular velocities (rad/s), signed
 * @param Lock            differential type
 * @param OutTorqueA/B    torque to each wheel
 */
void SplitAxleTorque(float InTorque, float OmegaA, float OmegaB, EDiffLock Lock,
                     float LockPreload, float& OutTorqueA, float& OutTorqueB);

/** Engine torque at a given RPM, from the spec curve. */
float SampleEngineTorque(const FEngineSpec& Spec, float RPM);
