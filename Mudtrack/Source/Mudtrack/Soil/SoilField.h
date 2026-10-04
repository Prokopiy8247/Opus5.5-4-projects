// SoilField.h
// Authoritative, persistent, tiled soft-ground model.
//
// The field is the single source of truth for:
//   * the height a wheel rests on,
//   * how much that height has been lowered by previous traffic,
//   * the rendered rut geometry.
//
// It is deliberately NOT a full soil-mechanics solver. See PHYSICS_NOTES.md.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "ProceduralMeshComponent.h"
#include "Materials/MaterialInterface.h"
#include "SoilTypes.h"
#include "SoilField.generated.h"

/** One tile of cells. Tiles are allocated lazily and never freed during play. */
USTRUCT()
struct FSoilTile
{
	GENERATED_BODY()

	UPROPERTY()
	TArray<FSoilCell> Cells;

	/** Bumped whenever cell state changes, so the render mesh can be rebuilt lazily. */
	UPROPERTY()
	int32 Revision = 0;

	/** Revision the currently built mesh corresponds to. */
	UPROPERTY()
	int32 BuiltRevision = -1;

	/** Index into the procedural mesh section array, or -1 while not displayed. */
	UPROPERTY()
	int32 SectionIndex = -1;

	bool bAllocated = false;

	/**
	 * True once traffic has modified a cell here. A touched tile is the only kind
	 * that must keep its data: an untouched one is an exact copy of the generator
	 * and is freed when it scrolls out of view.
	 */
	bool bTouched = false;
};

UCLASS(ClassGroup = (Mudtrack), meta = (BlueprintSpawnableComponent))
class MUDTRACK_API ASoilField : public AActor
{
	GENERATED_BODY()

public:
	ASoilField();

	// ---- Configuration -------------------------------------------------

	/** Cell size in cm. 25 cm is a compromise between rut fidelity and memory. */
	UPROPERTY(EditAnywhere, Category = "Soil")
	float CellSize = 25.f;

	/** Cells per tile edge. */
	UPROPERTY(EditAnywhere, Category = "Soil")
	int32 TileCells = 32;

	/** Half-extent of the authored field, in cm. 600 m map => 30000. */
	UPROPERTY(EditAnywhere, Category = "Soil")
	float FieldHalfExtent = 30000.f;

	/**
	 * Radius around the vehicle rendered at full (cell) resolution. Beyond it the
	 * coarse far mesh takes over, so this bounds the cost of the detailed ground
	 * no matter how large the field is.
	 */
	UPROPERTY(EditAnywhere, Category = "Soil")
	float ActiveRadius = 4000.f;

	/**
	 * Tiles carrying ruts stay at full resolution out to this distance, so tracks
	 * remain visible when the player drives away and looks back. Past it they fall
	 * back to the far mesh; the rut data itself is kept regardless.
	 */
	UPROPERTY(EditAnywhere, Category = "Soil")
	float RutViewRadius = 15000.f;

	/**
	 * Max tiles whose render mesh is rebuilt per frame (throttle).
	 *
	 * A rebuild of an already-displayed tile is a vertex-buffer update, not a
	 * scene-proxy rebuild, so this can stay modest; the far mesh covers any tile
	 * that has not been built yet, so a backlog never shows as a hole.
	 */
	UPROPERTY(EditAnywhere, Category = "Soil")
	int32 MaxTileRebuildsPerFrame = 16;

	/** Vertex spacing of the far terrain mesh, in cm. Must divide a tile edge evenly. */
	UPROPERTY(EditAnywhere, Category = "Soil|Visual")
	float FarSpacing = 400.f;

	/** Whether ruts recover (settle) over time. */
	UPROPERTY(EditAnywhere, Category = "Soil")
	bool bAllowSettling = false;

	/** Seconds for a fully churned cell to relax halfway back toward undisturbed. */
	UPROPERTY(EditAnywhere, Category = "Soil")
	float SettleHalfLife = 240.f;

	// ---- Per-class properties -----------------------------------------

	UPROPERTY(EditAnywhere, Category = "Soil|Params")
	TMap<ESoilClass, FSoilClassParams> ClassParams;

	/**
	 * Material applied to the terrain mesh. Left unset the mesh renders with the
	 * engine default and the vertex colours that carry rut depth are ignored,
	 * which is what made the ground look like a black void. Assigned by
	 * Scripts/import_assets.py; see EnsureTerrainMaterial() for the fallback.
	 */
	UPROPERTY(EditAnywhere, Category = "Soil|Visual")
	TSoftObjectPtr<UMaterialInterface> TerrainMaterial;

	/** Fallback material used when TerrainMaterial is unset. */
	UPROPERTY(EditAnywhere, Category = "Soil|Visual")
	TSoftObjectPtr<UMaterialInterface> TerrainMaterialFallback;

	// ---- Lifecycle -----------------------------------------------------

	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;

	/**
	 * Make the field usable if it is not already: size the grid and generate the
	 * authored surface. Idempotent and safe to call from anywhere.
	 *
	 * BeginPlay ordering between actors is NOT guaranteed, so anything that wants
	 * to sample the soil before or during its own BeginPlay (the forest scatter,
	 * the test driver, the vehicle's first ground probe) must call this first.
	 * Relying on ASoilField::BeginPlay having run is what made the scatter place
	 * zero instances: GridWidth was still 0, so every sample came back invalid.
	 */
	void EnsureInitialised();

	/** Generate the base terrain heightfield. Deterministic for a given seed. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	void GenerateTerrain(int32 Seed);

	/** Bake an authored heightmap into the firm layer. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	void BuildFromHeightFunc(const TArray<float>& InFirmZ, const TArray<float>& InSoftDepth,
	                         const TArray<uint8>& InClass, int32 InWidth);

	// ---- Queries -------------------------------------------------------

	/** Bilinear sample of the authoritative surface at a world location. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	FSoilSample SampleSoil(FVector WorldLocation) const;

	/** Fast path: surface Z only. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	float GetSurfaceZ(FVector WorldLocation) const;

	/** Exact per-cell lookup; returns false if the tile was never touched. */
	bool GetCellAt(FVector WorldLocation, FSoilCell& OutCell, FIntPoint& OutCoord) const;

	// ---- Modification --------------------------------------------------

	/**
	 * Apply a wheel contact to the soil.
	 *
	 * @param WorldLocation   contact centre
	 * @param Forward         vehicle forward direction (unit)
	 * @param Right           vehicle right direction (unit)
	 * @param ContactHalfLen  half length of the contact patch along Forward
	 * @param ContactHalfWid  half width of the contact patch along Right
	 * @param Pressure        contact pressure, gameplay kPa units
	 * @param SlipSpeed       contact-patch tangential speed (cm/s)
	 * @param WheelSlipRatio  longitudinal slip ratio
	 * @return Rut depth actually created this call (cm)
	 */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	float ApplyWheelLoad(FVector WorldLocation, FVector Forward, FVector Right,
	                     float ContactHalfLen, float ContactHalfWid,
	                     float Pressure, float SlipSpeed, float WheelSlipRatio,
	                     float DeltaSeconds);

	/** Register a non-wheel contact (underbody, plow) at a point. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	float ApplyPointLoad(FVector WorldLocation, float Pressure, float SlipSpeed,
	                     float Radius, float DeltaSeconds);

	/** Clear every rut and restore the authored state. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	void ResetAllSoils();

	/** Mark the whole field dirty (after a bulk edit). */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	void MarkAllDirty();

	// ---- Stats ---------------------------------------------------------

	UFUNCTION(BlueprintCallable, Category = "Soil")
	int32 GetAllocatedTileCount() const { return Tiles.Num(); }

	/** Tiles currently drawn at full resolution. Bounded by ActiveRadius/RutViewRadius. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	int32 GetMeshedTileCount() const { return MeshedTiles.Num(); }

	UFUNCTION(BlueprintCallable, Category = "Soil")
	int32 GetTotalCellCount() const { return Tiles.Num() * TileCells * TileCells; }

	/** Total soft material displaced across the field, a rough "damage" metric. */
	UFUNCTION(BlueprintCallable, Category = "Soil")
	float GetDisplacedVolume() const;

protected:
	/**
	 * Allocate a tile's cell data from the generator. Returns nullptr if out of
	 * bounds. Has no rendering side effects: allocation and display are separate,
	 * so reading the ground somewhere never causes it to be meshed.
	 */
	FSoilTile* GetOrCreateTile(int32 TileX, int32 TileY);

	const FSoilTile* FindTile(int32 TileX, int32 TileY) const;

	/** Authored (undisturbed) surface at any world XY, straight from the generator. */
	void EvaluateAuthored(float WX, float WY, float& OutFirmZ, float& OutSoftDepth, ESoilClass& OutClass) const;

	/** Authored state of one cell, computed without allocating anything. */
	void GenerateAuthoredCell(int32 CX, int32 CY, FSoilCell& Out) const;

	/**
	 * Current state of one cell: from the tile if it is allocated, otherwise from
	 * the generator. Returns false outside the field.
	 */
	bool ReadCell(int32 CX, int32 CY, FSoilCell& Out) const;

	/**
	 * Record that a cell changed: marks its tile touched, bumps the revision and
	 * queues a rebuild of every displayed tile whose vertices include this cell.
	 */
	void NoteCellModified(int32 CX, int32 CY);

	/** World <-> grid helpers. */
	FORCEINLINE void WorldToCell(FVector World, int32& OutCX, int32& OutCY) const
	{
		OutCX = FMath::FloorToInt((World.X + FieldHalfExtent) / CellSize);
		OutCY = FMath::FloorToInt((World.Y + FieldHalfExtent) / CellSize);
	}
	FORCEINLINE FVector CellToWorld(int32 CX, int32 CY) const
	{
		return FVector((CX + 0.5f) * CellSize - FieldHalfExtent,
		               (CY + 0.5f) * CellSize - FieldHalfExtent, 0.f);
	}
	FORCEINLINE int32 TileIndex(int32 CX, int32 CY) const
	{
		return (CY % TileCells) * TileCells + (CX % TileCells);
	}

	/** Fill the scratch buffers with one tile's vertices. */
	void BuildTileGeometry(const FIntPoint& TileCoord);

	/**
	 * Recompute which tiles should be drawn at full resolution (those within
	 * ActiveRadius, plus rutted ones within RutViewRadius), release the ones that
	 * no longer qualify and queue the newcomers. Cheap when nothing changed.
	 */
	void UpdateWantedTiles();

	/**
	 * Build up to Budget queued tiles, closest first. Existing sections are
	 * updated in place; only a brand-new section triggers a scene-proxy rebuild.
	 */
	void RebuildDirtyTiles(int32 Budget);

	/** Stop drawing a tile at full resolution and recycle its section. */
	void ReleaseTile(const FIntPoint& Key);

	/** Drop every displayed tile and all streaming bookkeeping. */
	void ResetStreaming();

	/** Cache the far mesh's authored heights and colours. Once per terrain. */
	void BuildFarCache();

	/** Push the far mesh, lowered wherever a full-resolution tile is drawn. */
	void RebuildFarMesh();

	/** Vertex colour for a cell: sRGB albedo of its soil class, darkened by ruts. */
	FLinearColor ShadeCell(const FSoilCell& C, float WX, float WY) const;

	/** Distance in XY from a point to the nearest point of a tile. */
	float DistanceToTile(const FVector& P, const FIntPoint& Tile) const;

	FORCEINLINE int32 MaskIndex(const FIntPoint& T) const { return T.Y * TilesPerEdge + T.X; }
	FORCEINLINE bool IsTileInField(const FIntPoint& T) const
	{
		return T.X >= 0 && T.Y >= 0 && T.X < TilesPerEdge && T.Y < TilesPerEdge;
	}

	/** Create or fetch the params for a soil class. */
	const FSoilClassParams& ParamsFor(ESoilClass InClass) const;

	/** Deterministic value noise. */
	static float ValueNoise2D(float X, float Y, int32 Seed);
	static float FBM(float X, float Y, int32 Seed, int32 Octaves, float Lacunarity, float Gain);

	// ---- State ---------------------------------------------------------

	/** Full-resolution tiles, one section per displayed tile. */
	UPROPERTY()
	TObjectPtr<UProceduralMeshComponent> TerrainMesh;

	/** The whole field at FarSpacing resolution, in a single section. */
	UPROPERTY()
	TObjectPtr<UProceduralMeshComponent> FarMesh;

	/** tile coord -> tile. Holds displayed tiles and touched (rutted) tiles only. */
	TMap<FIntPoint, FSoilTile> Tiles;

	/** Bounding cell count along one edge. */
	int32 GridWidth = 0;

	/** Tiles along one edge of the field. */
	int32 TilesPerEdge = 0;

	/** Tiles awaiting a mesh rebuild. */
	TSet<FIntPoint> DirtyTiles;

	/** Tiles that should be drawn at full resolution right now. */
	TSet<FIntPoint> WantedTiles;

	/** Tiles drawn at full resolution, and the section each one occupies. */
	TMap<FIntPoint, int32> MeshedTiles;

	/** Hidden sections ready for reuse. Reuse is an in-place vertex update. */
	TArray<int32> FreeSections;

	/** Per-tile flag mirroring MeshedTiles, for the far mesh's per-vertex test. */
	TArray<uint8> MeshedMask;

	/** Every tile traffic has modified. */
	TSet<FIntPoint> TouchedTiles;

	/** Centre tile the wanted set was last computed for. */
	FIntPoint LastCentreTile = FIntPoint(MIN_int32, MIN_int32);

	/** Set when the touched set grows, so the wanted set is recomputed. */
	bool bWantedDirty = true;

	/** Set when the displayed set changes, so the far mesh is re-lowered. */
	bool bFarDirty = true;

	bool bFarCacheValid = false;
	bool bFarSectionCreated = false;
	bool bInitialised = false;

	/** Far mesh: vertex count per edge, including the outer apron ring. */
	int32 FarN = 0;
	TArray<FVector> FarBaseVerts;      // authored positions
	TArray<FVector> FarNormals;
	TArray<FVector2D> FarUVs;
	TArray<FLinearColor> FarColors;
	TArray<FProcMeshTangent> FarTangents;
	TArray<int32> FarTris;
	TArray<FVector> FarVertsScratch;

	/** Same topology for every tile, built once. */
	TArray<int32> TileTris;

	/** World-space centre of the vehicle, for active-region updates. */
	FVector ActiveCentre = FVector::ZeroVector;

	int32 TerrainSeed = 1337;

	/** One-shot guard so the missing-material warning is not spammed every frame. */
	bool bWarnedAboutTerrainMaterial = false;

	/** The material actually resolved from TerrainMaterial/Fallback, cached. */
	UPROPERTY()
	TObjectPtr<UMaterialInterface> ResolvedTerrainMaterial = nullptr;

	/** Resolve TerrainMaterial (or the fallback) once and cache it. */
	void EnsureTerrainMaterial();

	/** Reusable scratch for mesh building. */
	TArray<float> ScratchHeights;
	TArray<FVector> ScratchVerts;
	TArray<FVector> ScratchNormals;
	TArray<FVector2D> ScratchUVs;
	TArray<FProcMeshTangent> ScratchTangents;
	TArray<FLinearColor> ScratchColors;
};
