// OffroadWheel.h
// Per-wheel contact, suspension and tyre model.
//
// WHY NOT A SINGLE RAY
// --------------------
// A single downward line trace at the hub is what most cheap vehicle models do.
// It fails exactly where this project is judged: on a log or a rut edge the ray
// misses the obstacle entirely and the wheel sinks through, then snaps upward
// when the hull catches. Here each wheel uses a *multi-sample sweep*: a small
// sphere swept down along the suspension axis at several points across and along
// the contact patch, so the effective contact height is the maximum of what the
// samples actually find. That makes ruts and logs behave continuously.

#pragma once

#include "CoreMinimal.h"
#include "../Soil/SoilTypes.h"
#include "OffroadWheel.generated.h"

class ASoilField;
class UStaticMeshComponent;

/** Static description of one wheel, filled from the vehicle setup. */
USTRUCT(BlueprintType)
struct FWheelSetup
{
	GENERATED_BODY()

	/** Attachment point in vehicle space (cm), where the suspension top sits. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	FVector AttachPoint = FVector(0.f, 0.f, 0.f);

	/** Radius of the tyre (cm). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float Radius = 46.5f;

	/** Width of the tyre (cm). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float Width = 33.f;

	/** Rest length of the spring/damper (cm). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float SuspensionRestLength = 46.f;

	/** Maximum compression travel from rest (cm). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float MaxCompression = 22.f;

	/** Maximum droop travel from rest (cm). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float MaxDroop = 18.f;

	/** Spring rate, N/cm. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float SpringRate = 620.f;

	/** Damping coefficient, N/(cm/s), compression direction. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float DampingCompression = 92.f;

	/** Damping coefficient, N/(cm/s), rebound direction. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float DampingRebound = 128.f;

	/** Progressive bump-stop rate, N/cm, applied in the last 25% of travel. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float BumpStopRate = 3600.f;

	/** Rotational inertia of the wheel+tyre assembly, kg*cm^2. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float Inertia = 2450.f;

	/** Peak friction coefficient on a hard, dry surface. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float PeakFriction = 1.05f;

	/** Slip ratio at which peak longitudinal friction occurs. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float PeakSlipRatio = 0.16f;

	/** Slip angle (rad) at which peak lateral friction occurs. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	float PeakSlipAngle = 0.14f;

	/** Is this wheel driven? Set per drivetrain mode at runtime. */
	UPROPERTY(BlueprintReadWrite, Category = "Wheel")
	bool bDriven = false;

	/** Is this wheel steerable? */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	bool bSteerable = false;

	/** Can this wheel receive handbrake? */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Wheel")
	bool bHandbrake = false;
};

/** Live state of one wheel. */
USTRUCT(BlueprintType)
struct FWheelState
{
	GENERATED_BODY()

	// --- suspension
	float SuspensionLength = 46.f;
	float PrevSuspensionLength = 46.f;
	float SuspensionVelocity = 0.f;
	float SuspensionForce = 0.f;
	bool bGrounded = false;
	bool bWasGrounded = false;

	// --- contact
	FVector ContactPoint = FVector::ZeroVector;
	FVector ContactNormal = FVector::UpVector;
	FVector WorldLocation = FVector::ZeroVector;

	// --- rotation
	float AngularVelocity = 0.f;   // rad/s, about the axle
	float WheelAngle = 0.f;        // accumulated visual angle
	float SteerAngle = 0.f;        // current steer, radians

	// --- tyre forces (vehicle space)
	float LongitudinalForce = 0.f;
	float LateralForce = 0.f;
	float NormalLoad = 0.f;

	// --- slip
	float SlipRatio = 0.f;
	float SlipAngle = 0.f;
	float PatchSpeed = 0.f;

	// --- surface
	float Sinkage = 0.f;
	float RutDepth = 0.f;
	float SurfaceZ = 0.f;
	float TractionScale = 1.f;
	float BearingCapacity = 0.f;
	ESoilClass SoilClass = ESoilClass::DrySoil;

	/** Rolling resistance force magnitude for this step, N. */
	float RollingResistance = 0.f;

	/** True when the tyre is spinning noticeably faster than the ground. */
	bool bSpinning = false;
};

/**
 * Rotational dynamics of one wheel.
 *
 * The wheel is an independent rigid body about its axle: engine torque and
 * brake torque act on it, and the tyre reaction acts back on it. This is what
 * makes wheelspin real — during wheelspin the vehicle can be stationary while
 * the wheel turns, because the torque is going into breaking traction, not into
 * forward motion.
 */
USTRUCT()
struct FWheelDynamics
{
	GENERATED_BODY()

	/** Integrate angular velocity under the given torques.
	 *  @param EngineTorque   N*cm at the wheel
	 *  @param BrakeTorque    N*cm opposing rotation (always >= 0)
	 *  @param TyreTorque     N*cm from the ground reaction (already signed)
	 *  @return new angular velocity, rad/s
	 */
	static float Integrate(float CurrentOmega, float EngineTorque, float BrakeTorque,
	                       float TyreTorque, float Inertia, float MaxOmega, float DeltaSeconds);
};
