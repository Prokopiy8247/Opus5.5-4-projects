// SoilTypes.h
// Shared soil model data types for the Mudtrack deformable-ground system.
//
// Design note: the SAME cell grid drives both the wheel's supporting surface and
// the rendered rut mesh. There is no separate visual-only decal layer. See
// PHYSICS_NOTES.md for the governing equations and their honest limitations.

#pragma once

#include "CoreMinimal.h"
#include "SoilTypes.generated.h"

/** Ground surface classification. Controls bearing capacity, cohesion and moisture. */
UENUM(BlueprintType)
enum class ESoilClass : uint8
{
	FirmRoad      UMETA(DisplayName = "Firm Road"),
	DrySoil       UMETA(DisplayName = "Dry Soil"),
	WetSoil       UMETA(DisplayName = "Wet Soil"),
	DeepMud       UMETA(DisplayName = "Deep Mud"),
	Gravel        UMETA(DisplayName = "Gravel"),
	Rock          UMETA(DisplayName = "Rock"),
	WaterBed      UMETA(DisplayName = "Water Bed")
};

/**
 * Per-cell state of the soft ground.
 *
 * The authoritative surface the wheel rests on is:
 *
 *     SurfaceZ = FirmZ + SoftDepth - RutDepth
 *
 * where SoftDepth is the current thickness of loose/soft material sitting on the
 * firm base, and RutDepth is how much of that soft material has been displaced
 * (or of the firm base excavated, up to a limit) by wheel passage.
 *
 * All heights are absolute world Z in centimetres (Unreal units).
 */
USTRUCT(BlueprintType)
struct FSoilCell
{
	GENERATED_BODY()

	/** Height of the firm, non-deformable base layer. Never changes at runtime. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float FirmZ = 0.f;

	/** Thickness of soft material currently present on top of the firm base. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float SoftDepth = 0.f;

	/** How far the soft layer has been pushed down / excavated by traffic. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float RutDepth = 0.f;

	/** How much of the firm base may still be excavated (erosion budget). */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float FirmErosion = 0.f;

	/** 0 = fully loose/disturbed, 1 = fully compacted. Traffic compacts then churns. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float Compaction = 1.f;

	/** Volumetric water content, 0..1. Raises pore pressure, lowers bearing capacity. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float Moisture = 0.f;

	/** Material thrown up beside the rut (berm). Physics-visible, limits lateral slide. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float Berm = 0.f;

	/** Timestamp of last disturbance, used for settling/recovery over time. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float LastTouchTime = -1000.f;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	ESoilClass SoilClass = ESoilClass::DrySoil;

	/** Authoring initial soft depth, for reset without reloading the level. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float InitialSoftDepth = 0.f;

	/** Authoring initial firm height, for reset. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float InitialFirmZ = 0.f;

	/** Authoring initial compaction and moisture, for reset. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float InitialCompaction = 1.f;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Soil")
	float InitialMoisture = 0.f;

	/** The height the wheel actually rests on. */
	FORCEINLINE float GetSurfaceZ() const
	{
		return FirmZ + SoftDepth - RutDepth + Berm;
	}

	FORCEINLINE void Reset()
	{
		FirmZ = InitialFirmZ;
		SoftDepth = InitialSoftDepth;
		RutDepth = 0.f;
		FirmErosion = 0.f;
		Compaction = InitialCompaction;
		Moisture = InitialMoisture;
		Berm = 0.f;
		LastTouchTime = -1000.f;
	}
};

/** Sample returned by a world-space query against the soil field. */
USTRUCT(BlueprintType)
struct FSoilSample
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float SurfaceZ = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float FirmZ = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float SoftDepth = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float RutDepth = 0.f;

	/** How far the surface has been lowered below its undisturbed height. */
	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float Sinkage = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float Compaction = 1.f;

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float Moisture = 0.f;

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	ESoilClass SoilClass = ESoilClass::DrySoil;

	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	bool bValid = false;

	/** Effective bearing capacity at this location, in kPa-equivalent units. */
	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float BearingCapacity = 0.f;

	/** Hardness multiplier for the tyre contact model, 0..1.5. */
	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float TractionScale = 1.f;

	/**
	 * Rolling-resistance coefficient here (fraction of the wheel load): the
	 * class's base value plus a term that grows with sinkage -- the tyre is
	 * pushing a bow wave of soil ahead of it.
	 */
	UPROPERTY(BlueprintReadOnly, Category = "Soil")
	float RollingResistance = 0.015f;
};

/**
 * Static per-surface-class properties. Populated from a data asset / defaults.
 * Values are project guidelines chosen for feel, not measured soil mechanics.
 */
USTRUCT(BlueprintType)
struct FSoilClassParams
{
	GENERATED_BODY()

	/** Maximum soft layer thickness that can exist on this surface (cm). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float MaxSoftDepth = 20.f;

	/** Pressure at which the soft layer gives way, in "kPa" gameplay units. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float BearingStrength = 60.f;

	/** Extra capacity gained per unit of compaction. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float CompactionGain = 90.f;

	/** How quickly traffic compacts this soil, 0..1 per unit of work. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float CompactRate = 0.25f;

	/** How readily disturbed soil turns back into churned mud. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float ChurnRate = 0.60f;

	/** Base friction coefficient of the surface material itself. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float BaseFriction = 0.85f;

	/** Rolling resistance coefficient on undisturbed surface. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float BaseRollingResistance = 0.015f;

	/** Rolling resistance added per cm of sinkage. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float SinkageResistance = 0.020f;

	/** Sidewall cohesion: how strongly the rut walls resist lateral slip. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float LateralCohesion = 0.35f;

	/** Fraction of displaced volume that becomes a berm instead of vanishing. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float BermFraction = 0.25f;

	/** Water content of this surface, 0..1. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float Moisture = 0.f;

	/**
	 * Compaction of undisturbed ground, 0..1. Loose mud starts loose; a road
	 * starts packed. Every cell used to start at 1.0 -- fully compacted -- which
	 * put deep mud's bearing capacity well above a tyre's contact pressure, so
	 * the vehicle floated across it and it never rutted.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float InitialCompaction = 1.f;

	/** Density used for splash/spray particle quantity. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Soil")
	float SprayDensity = 1.f;
};

/** One wheel's instantaneous interaction with the ground, for HUD and CSV logging. */
USTRUCT(BlueprintType)
struct FWheelTelemetry
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") int32 WheelIndex = 0;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") bool bGrounded = false;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float SuspensionLength = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float SuspensionForce = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float NormalLoad = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float SlipRatio = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float SlipAngleDeg = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float Sinkage = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float RutDepth = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float AngularVelocity = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float SurfaceZ = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float TractionUsed = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float TractionAvail = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float LateralForce = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") float LongitudinalForce = 0.f;
	UPROPERTY(BlueprintReadOnly, Category = "Telemetry") ESoilClass SoilClass = ESoilClass::DrySoil;
};
