// PropsAndCourse.h
// Procedural forest scatter and the reproducible test course.
//
// Both are built from C++ rather than hand-placed so a fresh level can be
// generated deterministically from a seed and so the tests always find the same
// obstacles. Instances use Hierarchical Instanced Static Meshes so several
// thousand trees cost one draw call per species.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "PropsAndCourse.generated.h"

class UHierarchicalInstancedStaticMeshComponent;
class UStaticMeshComponent;
class ASoilField;
class UStaticMesh;

/** A scatter species: mesh, density, scale range. */
USTRUCT(BlueprintType)
struct FScatterSpecies
{
	GENERATED_BODY()

	UPROPERTY(EditAnywhere, Category = "Scatter") TSoftObjectPtr<UStaticMesh> Mesh;
	UPROPERTY(EditAnywhere, Category = "Scatter") float MinScale = 0.85f;
	UPROPERTY(EditAnywhere, Category = "Scatter") float MaxScale = 1.25f;
	/** Share of the total scatter budget for this species, 0..1. */
	UPROPERTY(EditAnywhere, Category = "Scatter") float Share = 0.2f;
	/** Minimum spacing to another instance of the same species (cm). */
	UPROPERTY(EditAnywhere, Category = "Scatter") float MinSpacing = 420.f;
	/** How much the instance is sunk into the ground (cm). */
	UPROPERTY(EditAnywhere, Category = "Scatter") float SinkDepth = 12.f;
	/** Random yaw variation, degrees. */
	UPROPERTY(EditAnywhere, Category = "Scatter") float YawJitter = 360.f;
};

/**
 * Populates the forest. Deterministic for a given seed, avoids the roads and
 * the mud lanes, and refuses to place anything where the ground is too steep.
 */
UCLASS()
class MUDTRACK_API AForestScatter : public AActor
{
	GENERATED_BODY()

public:
	AForestScatter();

	virtual void BeginPlay() override;

	/** Total instances to attempt across all species. */
	UPROPERTY(EditAnywhere, Category = "Scatter") int32 TotalInstances = 2600;

	UPROPERTY(EditAnywhere, Category = "Scatter") int32 Seed = 20261002;

	/** Half-extent of the scatter area, cm. */
	UPROPERTY(EditAnywhere, Category = "Scatter") float AreaHalfExtent = 29500.f;

	/** Keep-out radius around the base, cm. */
	UPROPERTY(EditAnywhere, Category = "Scatter") float BaseKeepOut = 2400.f;

	/** Maximum ground slope, degrees, for a placement to be accepted. */
	UPROPERTY(EditAnywhere, Category = "Scatter") float MaxSlopeDeg = 26.f;

	/** Species definitions, filled in BeginPlay if empty. */
	UPROPERTY(EditAnywhere, Category = "Scatter") TArray<FScatterSpecies> Species;

	/** Build the scatter now. Safe to call more than once. */
	UFUNCTION(BlueprintCallable, Category = "Scatter")
	void Populate();

	UFUNCTION(BlueprintCallable, Category = "Scatter")
	int32 GetPlacedCount() const { return PlacedCount; }

protected:
	bool IsPlacementAllowed(const FVector& P, float Seed, float Spacing, float SinkDepth,
	                        FVector& OutLocation, FRotator& OutRotation);

	UPROPERTY()
	TArray<TObjectPtr<UHierarchicalInstancedStaticMeshComponent>> Components;

	UPROPERTY()
	TObjectPtr<ASoilField> Soil;

	int32 PlacedCount = 0;
	bool bPopulated = false;

	/** Compute the ground normal at a point by sampling the soil field. */
	bool GetGroundSample(const FVector& XY, FVector& OutPoint, FVector& OutNormal);
};

/**
 * Builds the reproducible test course near the base:
 *   - a diagonal articulation ramp
 *   - a log lying across a lane
 *   - transverse bumps (a washboard)
 *   - a side slope
 *   - paired dry / muddy lanes
 *   - a winch pit with an anchor tree
 * Everything it spawns is tagged so the automated tests can find it.
 */
UCLASS()
class MUDTRACK_API ATestCourse : public AActor
{
	GENERATED_BODY()

public:
	ATestCourse();

	virtual void BeginPlay() override;

	/** Build the course. Idempotent. */
	UFUNCTION(BlueprintCallable, Category = "Course")
	void BuildCourse();

	UPROPERTY(EditAnywhere, Category = "Course") bool bBuildOnBeginPlay = true;

protected:
	AActor* SpawnBox(const FString& Name, const FVector& Loc, const FRotator& Rot,
	                 const FVector& Size, const FLinearColor& Colour, FName Tag);
	AActor* SpawnMeshAt(const FString& Name, UStaticMesh* Mesh, const FVector& Loc,
	                    const FRotator& Rot, const FVector& Scale, FName Tag);

	UPROPERTY()
	TObjectPtr<ASoilField> Soil;
};
