// SoilField.cpp
// Implementation of the deformable soft-ground model.
//
// PHYSICAL MODEL, STATED PLAINLY
// ------------------------------
// This is a *simplified bearing-capacity model*, not a continuum soil solver.
// For a contact patch of area A carrying normal force N, the mean contact
// pressure is p = N / A. The ground resists with a capacity q that depends on
// the surface class, how compacted it is, and how wet it is:
//
//     q = BearingStrength + CompactionGain * Compaction - MoistureLoss
//
// Two things lower the surface:
//
//   bearing failure  while p > q the ground compresses quickly toward the depth
//                    at which it is dense enough to carry the load:
//                        dRut = SinkRate * min((p - q) / q, 3) * dt
//                    and every centimetre given raises its compaction, so q
//                    climbs to meet p and the sinkage stops by itself;
//   excavation       a spinning tyre (|slip| > 0.35) throws material out and
//                    breaks the soil's structure (compaction falls), slowly:
//                        dRut = DigRate * spin * (1 - 0.6 * compaction) * dt
//
// Deepening is capped by the available soft material plus a limited firm-layer
// erosion budget, so ruts are FINITE and PERSISTENT. Displaced material is
// partly thrown aside into a berm, which the wheel then has to climb, and
// partly compacted. Because the same FSoilCell drives the collision surface,
// a second pass through a rut sees a genuinely different, lower, harder surface.
//
// Known limits are listed in PHYSICS_NOTES.md.

#include "SoilField.h"
#include "../Mudtrack.h"
#include "Engine/World.h"
#include "Math/UnrealMathUtility.h"

DEFINE_LOG_CATEGORY_STATIC(LogSoil, Log, All);

namespace
{
	/**
	 * Bearing-failure sinkage rate, cm/s at 100% overload. Fast on purpose: a tyre
	 * settles into soft ground as it rolls on, and the sinkage stops by itself
	 * once the compacted soil can carry the load.
	 */
	constexpr float SinkRate = 900.0f;

	/**
	 * Excavation by a fully spinning tyre, cm/s in loose wet ground. Slow on
	 * purpose: seconds of wheelspin to dig in, not frames -- at frame rates the
	 * ground was gone before any drivetrain setting could make a difference.
	 */
	constexpr float DigRate = 6.0f;

	/** Overload is capped too; 30x capacity does not mean 30x the excavation rate. */
	constexpr float MaxPressureExcess = 3.0f;

	/** How fast wet soil loses bearing capacity at full moisture. */
	constexpr float MoistureLoss = 55.f;

	/** Compaction is driven by work done; this scales it. */
	constexpr float CompactionRate = 0.9f;

	/** Compaction is destroyed by sustained sliding (churn). */
	constexpr float ChurnRate = 0.7f;

	/** How much a berm raises the surface relative to displaced depth. */
	constexpr float BermHeightGain = 0.55f;

	/** Fraction of the rut depth that erodes the firm layer once soft is gone. */
	constexpr float FirmErosionRate = 0.35f;

	/** Hard cap on how deep the firm layer may ever be cut. */
	constexpr float MaxFirmErosion = 22.f;

	/** Contact patch default geometry if the caller gives nothing sensible. */
	constexpr float DefaultPatchHalfLen = 22.f;
	constexpr float DefaultPatchHalfWid = 12.f;

	FORCEINLINE float Saturate01(float V) { return FMath::Clamp(V, 0.f, 1.f); }
}

ASoilField::ASoilField()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.TickGroup = TG_PrePhysics;

	TerrainMesh = CreateDefaultSubobject<UProceduralMeshComponent>(TEXT("TerrainMesh"));
	RootComponent = TerrainMesh;
	TerrainMesh->bUseAsyncCooking = false;
	TerrainMesh->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	TerrainMesh->SetCastShadow(true);
	TerrainMesh->bCastDynamicShadow = true;

	// The far mesh carries the whole field at low resolution. Without it the
	// world would end at the edge of the detailed region and every tree past it
	// would stand on nothing.
	FarMesh = CreateDefaultSubobject<UProceduralMeshComponent>(TEXT("FarMesh"));
	FarMesh->SetupAttachment(TerrainMesh);
	FarMesh->bUseAsyncCooking = false;
	FarMesh->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	// The far mesh does not cast shadows: it spans the whole field plus an apron,
	// and in the shadow map it only competes with the detailed ground around the
	// vehicle for resolution. Hills far away do not need cast shadows to read.
	FarMesh->SetCastShadow(false);
	FarMesh->bCastDynamicShadow = false;

	// Sensible default per-class parameters. These are gameplay-tuned values,
	// chosen so a heavy 4x4 sinks noticeably in mud and barely at all on rock.
	{
		FSoilClassParams P;
		P.MaxSoftDepth = 0.f;   P.BearingStrength = 9000.f; P.CompactionGain = 0.f;
		P.CompactRate = 0.f;    P.ChurnRate = 0.f;          P.BaseFriction = 0.95f;
		P.BaseRollingResistance = 0.010f; P.SinkageResistance = 0.f;
		P.LateralCohesion = 0.05f; P.BermFraction = 0.f;    P.Moisture = 0.f;
		P.SprayDensity = 0.f;
		P.InitialCompaction = 1.0f;
		ClassParams.Add(ESoilClass::Rock, P);
	}
	{
		FSoilClassParams P;
		P.MaxSoftDepth = 4.f;   P.BearingStrength = 420.f; P.CompactionGain = 260.f;
		P.CompactRate = 0.30f;  P.ChurnRate = 0.25f;       P.BaseFriction = 0.88f;
		P.BaseRollingResistance = 0.014f; P.SinkageResistance = 0.006f;
		P.LateralCohesion = 0.45f; P.BermFraction = 0.10f; P.Moisture = 0.03f;
		P.SprayDensity = 0.25f;
		P.InitialCompaction = 0.9f;
		ClassParams.Add(ESoilClass::FirmRoad, P);
	}
	{
		FSoilClassParams P;
		P.MaxSoftDepth = 10.f;  P.BearingStrength = 300.f; P.CompactionGain = 200.f;
		P.CompactRate = 0.28f;  P.ChurnRate = 0.45f;       P.BaseFriction = 0.80f;
		P.BaseRollingResistance = 0.020f; P.SinkageResistance = 0.014f;
		P.LateralCohesion = 0.55f; P.BermFraction = 0.18f; P.Moisture = 0.10f;
		P.SprayDensity = 0.45f;
		P.InitialCompaction = 0.6f;
		ClassParams.Add(ESoilClass::DrySoil, P);
	}
	{
		FSoilClassParams P;
		P.MaxSoftDepth = 18.f;  P.BearingStrength = 70.f;  P.CompactionGain = 150.f;
		P.CompactRate = 0.22f;  P.ChurnRate = 0.65f;       P.BaseFriction = 0.62f;
		P.BaseRollingResistance = 0.030f; P.SinkageResistance = 0.026f;
		P.LateralCohesion = 0.75f; P.BermFraction = 0.26f; P.Moisture = 0.34f;
		P.SprayDensity = 0.85f;
		P.InitialCompaction = 0.3f;
		ClassParams.Add(ESoilClass::WetSoil, P);
	}
	{
		FSoilClassParams P;
		P.MaxSoftDepth = 34.f;  P.BearingStrength = 65.f;  P.CompactionGain = 120.f;
		P.CompactRate = 0.16f;  P.ChurnRate = 0.85f;       P.BaseFriction = 0.44f;
		P.BaseRollingResistance = 0.052f; P.SinkageResistance = 0.048f;
		P.LateralCohesion = 1.05f; P.BermFraction = 0.34f; P.Moisture = 0.62f;
		P.SprayDensity = 1.4f;
		P.InitialCompaction = 0.12f;
		ClassParams.Add(ESoilClass::DeepMud, P);
	}
	{
		FSoilClassParams P;
		P.MaxSoftDepth = 8.f;   P.BearingStrength = 340.f; P.CompactionGain = 120.f;
		P.CompactRate = 0.35f;  P.ChurnRate = 0.40f;       P.BaseFriction = 0.72f;
		P.BaseRollingResistance = 0.028f; P.SinkageResistance = 0.020f;
		P.LateralCohesion = 0.30f; P.BermFraction = 0.12f; P.Moisture = 0.12f;
		P.SprayDensity = 0.35f;
		P.InitialCompaction = 0.7f;
		ClassParams.Add(ESoilClass::Gravel, P);
	}
	{
		FSoilClassParams P;
		P.MaxSoftDepth = 26.f;  P.BearingStrength = 90.f;  P.CompactionGain = 80.f;
		P.CompactRate = 0.10f;  P.ChurnRate = 0.80f;       P.BaseFriction = 0.34f;
		P.BaseRollingResistance = 0.060f; P.SinkageResistance = 0.055f;
		P.LateralCohesion = 0.85f; P.BermFraction = 0.30f; P.Moisture = 0.95f;
		P.SprayDensity = 1.8f;
		P.InitialCompaction = 0.15f;
		ClassParams.Add(ESoilClass::WaterBed, P);
	}
}

void ASoilField::EnsureInitialised()
{
	// Idempotent: the first caller wins, everyone else is a no-op. See the header
	// for why this cannot just live in BeginPlay — actor BeginPlay order is not
	// guaranteed, and the scatter samples the soil during its own BeginPlay.
	if (bInitialised)
	{
		return;
	}
	bInitialised = true;

	// GenerateTerrain sizes the grid. The game mode may already have called it
	// with its own seed; only fall back to the default when nobody has.
	if (GridWidth <= 0)
	{
		GenerateTerrain(TerrainSeed);
	}

	ActiveCentre = GetActorLocation();
	EnsureTerrainMaterial();

	UE_LOG(LogSoil, Log,
		TEXT("EnsureInitialised done: grid=%d tiles/edge=%d radius=%.0f rutview=%.0f far=%.0f"),
		GridWidth, TilesPerEdge, ActiveRadius, RutViewRadius, FarSpacing);
}

void ASoilField::EnsureTerrainMaterial()
{
	// Resolve once, then reuse. Sections pick the material up as they are
	// created; a material assigned to slot 0 before any section exists does
	// nothing for the sections created later.
	if (ResolvedTerrainMaterial == nullptr)
	{
		ResolvedTerrainMaterial = TerrainMaterial.LoadSynchronous();
		if (ResolvedTerrainMaterial == nullptr)
		{
			ResolvedTerrainMaterial = TerrainMaterialFallback.LoadSynchronous();
		}
	}

	if (ResolvedTerrainMaterial == nullptr && !bWarnedAboutTerrainMaterial)
	{
		bWarnedAboutTerrainMaterial = true;
		// Loud on purpose: without a material the ground renders as the engine's
		// grey checkerboard, and that failure looks exactly like "the game is
		// broken" rather than "an asset is missing".
		UE_LOG(LogSoil, Warning,
			TEXT("Terrain material MISSING - the ground will render as the default ")
			TEXT("checkerboard. Set ASoilField::TerrainMaterial (Scripts/import_assets.py does this)."));
	}
}

void ASoilField::BeginPlay()
{
	Super::BeginPlay();
	EnsureInitialised();

	// Build the ground immediately rather than waiting for the first Tick: the
	// first rendered frame must already show terrain.
	UpdateWantedTiles();
	RebuildDirtyTiles(MAX_int32);
	RebuildFarMesh();

	UE_LOG(LogSoil, Log, TEXT("Ground ready: %d detailed tiles, far mesh %dx%d"),
		MeshedTiles.Num(), FarN, FarN);
}

void ASoilField::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	// The detailed region follows the player's vehicle, so terrain resolution is
	// spent where the player actually is.
	const UWorld* World = GetWorld();
	if (World != nullptr)
	{
		if (const APawn* Pawn = World->GetFirstPlayerController()
				? World->GetFirstPlayerController()->GetPawn() : nullptr)
		{
			ActiveCentre = Pawn->GetActorLocation();
		}
	}

	UpdateWantedTiles();
	RebuildDirtyTiles(MaxTileRebuildsPerFrame);
	if (bFarDirty)
	{
		RebuildFarMesh();
	}
}

float ASoilField::DistanceToTile(const FVector& P, const FIntPoint& Tile) const
{
	const float TileSize = TileCells * CellSize;
	const float MinX = -FieldHalfExtent + Tile.X * TileSize;
	const float MinY = -FieldHalfExtent + Tile.Y * TileSize;
	const float PX = (float)P.X;
	const float PY = (float)P.Y;
	const float DX = FMath::Max3(MinX - PX, 0.f, PX - (MinX + TileSize));
	const float DY = FMath::Max3(MinY - PY, 0.f, PY - (MinY + TileSize));
	return FMath::Sqrt(DX * DX + DY * DY);
}

void ASoilField::UpdateWantedTiles()
{
	if (TilesPerEdge <= 0 || TerrainMesh == nullptr)
	{
		return;
	}

	const float TileSize = TileCells * CellSize;
	const FIntPoint Centre(
		FMath::FloorToInt((ActiveCentre.X + FieldHalfExtent) / TileSize),
		FMath::FloorToInt((ActiveCentre.Y + FieldHalfExtent) / TileSize));

	// Only recompute when the vehicle crosses into another tile or new ground
	// was rutted. Everything below is cheap, but there is no reason to do it
	// sixty times a second while nothing changes.
	if (Centre == LastCentreTile && !bWantedDirty)
	{
		return;
	}
	LastCentreTile = Centre;
	bWantedDirty = false;

	TSet<FIntPoint> Wanted;

	// The detailed region: every tile whose nearest point is within the radius.
	const int32 Span = FMath::CeilToInt(ActiveRadius / TileSize) + 1;
	for (int32 DY = -Span; DY <= Span; ++DY)
	{
		for (int32 DX = -Span; DX <= Span; ++DX)
		{
			const FIntPoint T(Centre.X + DX, Centre.Y + DY);
			if (IsTileInField(T) && DistanceToTile(ActiveCentre, T) <= ActiveRadius)
			{
				Wanted.Add(T);
			}
		}
	}

	// Rutted ground stays detailed further out, so tracks persist on screen.
	for (const FIntPoint& T : TouchedTiles)
	{
		if (DistanceToTile(ActiveCentre, T) <= RutViewRadius)
		{
			Wanted.Add(T);
		}
	}

	// Release what no longer qualifies. This happens before any new tile is
	// built so a section freed here is reused this same frame.
	TArray<FIntPoint> ToRelease;
	for (const TPair<FIntPoint, int32>& KV : MeshedTiles)
	{
		if (!Wanted.Contains(KV.Key))
		{
			ToRelease.Add(KV.Key);
		}
	}
	for (const FIntPoint& Key : ToRelease)
	{
		ReleaseTile(Key);
	}

	// Queue the newcomers.
	for (const FIntPoint& T : Wanted)
	{
		if (!MeshedTiles.Contains(T))
		{
			DirtyTiles.Add(T);
		}
	}

	WantedTiles = MoveTemp(Wanted);
}

void ASoilField::ReleaseTile(const FIntPoint& Key)
{
	int32 Section = INDEX_NONE;
	if (MeshedTiles.RemoveAndCopyValue(Key, Section))
	{
		// Hiding is a render-thread flag flip; the section's buffers stay
		// allocated and are overwritten in place when the slot is reused.
		TerrainMesh->SetMeshSectionVisible(Section, false);
		FreeSections.Add(Section);
		if (IsTileInField(Key))
		{
			MeshedMask[MaskIndex(Key)] = 0;
		}
		bFarDirty = true;
	}
	DirtyTiles.Remove(Key);

	if (FSoilTile* Tile = Tiles.Find(Key))
	{
		Tile->SectionIndex = INDEX_NONE;
		// An untouched tile is an exact copy of the generator; drop it.
		if (!Tile->bTouched)
		{
			Tiles.Remove(Key);
		}
	}
}

void ASoilField::ResetStreaming()
{
	if (TerrainMesh != nullptr)
	{
		for (const TPair<FIntPoint, int32>& KV : MeshedTiles)
		{
			TerrainMesh->SetMeshSectionVisible(KV.Value, false);
			FreeSections.Add(KV.Value);
		}
	}
	MeshedTiles.Reset();
	DirtyTiles.Reset();
	WantedTiles.Reset();
	MeshedMask.Init(0, FMath::Max(0, TilesPerEdge * TilesPerEdge));
	LastCentreTile = FIntPoint(MIN_int32, MIN_int32);
	bWantedDirty = true;
	bFarCacheValid = false;
	bFarDirty = true;
}

void ASoilField::RebuildDirtyTiles(int32 Budget)
{
	if (TerrainMesh == nullptr || DirtyTiles.Num() == 0 || Budget <= 0)
	{
		return;
	}

	EnsureTerrainMaterial();

	// Closest first, so the ground under the vehicle is never the part waiting.
	TArray<FIntPoint> Order = DirtyTiles.Array();
	if (Order.Num() > Budget)
	{
		const FVector C = ActiveCentre;
		Order.Sort([this, C](const FIntPoint& A, const FIntPoint& B)
		{
			return DistanceToTile(C, A) < DistanceToTile(C, B);
		});
	}

	if (TileTris.Num() == 0)
	{
		// One topology for every tile, so any section can host any tile and a
		// recycled section only needs its vertex data rewritten.
		const int32 N = TileCells + 1;
		TileTris.Reserve(TileCells * TileCells * 6);
		for (int32 LY = 0; LY < TileCells; ++LY)
		{
			for (int32 LX = 0; LX < TileCells; ++LX)
			{
				const int32 I0 = LY * N + LX;
				const int32 I1 = I0 + 1;
				const int32 I2 = I0 + N;
				const int32 I3 = I2 + 1;
				TileTris.Add(I0); TileTris.Add(I2); TileTris.Add(I1);
				TileTris.Add(I1); TileTris.Add(I2); TileTris.Add(I3);
			}
		}
	}

	int32 Built = 0;
	int32 Created = 0;
	for (const FIntPoint& Key : Order)
	{
		if (Built >= Budget)
		{
			break;
		}
		DirtyTiles.Remove(Key);

		int32* Existing = MeshedTiles.Find(Key);
		if (Existing == nullptr && !WantedTiles.Contains(Key))
		{
			// Modified but not on screen: nothing to draw. The data is kept.
			continue;
		}

		FSoilTile* Tile = GetOrCreateTile(Key.X, Key.Y);
		if (Tile == nullptr)
		{
			continue;
		}

		BuildTileGeometry(Key);

		// Vertex colours are stored sRGB-encoded (8 bits per channel loses the
		// dark mud tones in linear) and decoded in M_Terrain. The flag is passed
		// explicitly: Create defaults it to false and Update to true, so leaving
		// it to the defaults makes recycled tiles a different colour.
		constexpr bool bSRGB = true;

		int32 Section = INDEX_NONE;
		if (Existing != nullptr)
		{
			Section = *Existing;
			TerrainMesh->UpdateMeshSection_LinearColor(Section, ScratchVerts, ScratchNormals,
				ScratchUVs, ScratchColors, ScratchTangents, bSRGB);
		}
		else if (FreeSections.Num() > 0)
		{
			Section = FreeSections.Pop(EAllowShrinking::No);
			TerrainMesh->UpdateMeshSection_LinearColor(Section, ScratchVerts, ScratchNormals,
				ScratchUVs, ScratchColors, ScratchTangents, bSRGB);
			TerrainMesh->SetMeshSectionVisible(Section, true);
		}
		else
		{
			Section = TerrainMesh->GetNumSections();
			TerrainMesh->CreateMeshSection_LinearColor(Section, ScratchVerts, TileTris, ScratchNormals,
				ScratchUVs, ScratchColors, ScratchTangents, /*bCreateCollision=*/false, bSRGB);
			if (ResolvedTerrainMaterial != nullptr)
			{
				TerrainMesh->SetMaterial(Section, ResolvedTerrainMaterial);
			}
			++Created;
		}

		if (Existing == nullptr)
		{
			MeshedTiles.Add(Key, Section);
			MeshedMask[MaskIndex(Key)] = 1;
			bFarDirty = true;
		}
		Tile->SectionIndex = Section;
		Tile->BuiltRevision = Tile->Revision;
		++Built;
	}

	if (Created > 0)
	{
		UE_LOG(LogSoil, Verbose, TEXT("RebuildTiles: built=%d new_sections=%d displayed=%d allocated=%d"),
			Built, Created, MeshedTiles.Num(), Tiles.Num());
	}
}

// ---------------------------------------------------------------------------
// Far terrain
// ---------------------------------------------------------------------------

void ASoilField::BuildFarCache()
{
	// The far grid is aligned to tile edges (a tile is exactly two far cells),
	// so the boundary of the detailed region always falls on a far vertex row.
	// That is what lets the far mesh be lowered under detailed tiles without
	// leaving a trench along the boundary.
	const int32 Inner = FMath::RoundToInt((FieldHalfExtent * 2.f) / FarSpacing) + 1;

	// An apron ring outside the field carries the edge height out to the
	// horizon, so the world does not visibly end in a cliff.
	const float Apron = 60000.f;
	FarN = Inner + 2;

	auto Coord = [&](int32 I) -> float
	{
		if (I == 0) return -FieldHalfExtent - Apron;
		if (I == FarN - 1) return FieldHalfExtent + Apron;
		return -FieldHalfExtent + (I - 1) * FarSpacing;
	};

	FarBaseVerts.SetNumUninitialized(FarN * FarN);
	FarColors.SetNumUninitialized(FarN * FarN);
	FarUVs.SetNumUninitialized(FarN * FarN);
	FarTangents.Init(FProcMeshTangent(1.f, 0.f, 0.f), FarN * FarN);
	FarNormals.SetNumUninitialized(FarN * FarN);

	for (int32 J = 0; J < FarN; ++J)
	{
		for (int32 I = 0; I < FarN; ++I)
		{
			// The apron copies the nearest field-edge sample.
			const int32 SI = FMath::Clamp(I, 1, FarN - 2);
			const int32 SJ = FMath::Clamp(J, 1, FarN - 2);
			const float SX = Coord(SI);
			const float SY = Coord(SJ);

			float FirmZ = 0.f, Soft = 0.f;
			ESoilClass Cls = ESoilClass::DrySoil;
			EvaluateAuthored(SX, SY, FirmZ, Soft, Cls);

			FSoilCell Cell;
			Cell.FirmZ = FirmZ;
			Cell.SoftDepth = Soft;
			Cell.SoilClass = Cls;
			Cell.Compaction = 1.f;

			const float WX = Coord(I);
			const float WY = Coord(J);
			const int32 Idx = J * FarN + I;
			FarBaseVerts[Idx] = FVector(WX, WY, Cell.GetSurfaceZ());
			FarColors[Idx] = ShadeCell(Cell, SX, SY);
			FarUVs[Idx] = FVector2D(WX / 100.f, WY / 100.f);
		}
	}

	// Normals by central differences on the cached heights.
	for (int32 J = 0; J < FarN; ++J)
	{
		for (int32 I = 0; I < FarN; ++I)
		{
			const FVector& L = FarBaseVerts[J * FarN + FMath::Max(I - 1, 0)];
			const FVector& R = FarBaseVerts[J * FarN + FMath::Min(I + 1, FarN - 1)];
			const FVector& D = FarBaseVerts[FMath::Max(J - 1, 0) * FarN + I];
			const FVector& U = FarBaseVerts[FMath::Min(J + 1, FarN - 1) * FarN + I];
			const float DX = FMath::Max(R.X - L.X, 1.f);
			const float DY = FMath::Max(U.Y - D.Y, 1.f);
			FVector Nrm(-(R.Z - L.Z) / DX, -(U.Z - D.Z) / DY, 1.f);
			FarNormals[J * FarN + I] = Nrm.GetSafeNormal();
		}
	}

	// Same winding as the detailed tiles.
	FarTris.Reset(FarN * FarN * 6);
	for (int32 J = 0; J < FarN - 1; ++J)
	{
		for (int32 I = 0; I < FarN - 1; ++I)
		{
			const int32 I0 = J * FarN + I;
			const int32 I1 = I0 + 1;
			const int32 I2 = I0 + FarN;
			const int32 I3 = I2 + 1;
			FarTris.Add(I0); FarTris.Add(I2); FarTris.Add(I1);
			FarTris.Add(I1); FarTris.Add(I2); FarTris.Add(I3);
		}
	}

	bFarCacheValid = true;
}

void ASoilField::RebuildFarMesh()
{
	if (FarMesh == nullptr || TilesPerEdge <= 0)
	{
		return;
	}
	if (!bFarCacheValid)
	{
		BuildFarCache();
		bFarSectionCreated = false;
	}
	bFarDirty = false;

	// Where a detailed tile is drawn, the far mesh has to get out of the way: it
	// would otherwise sit on top of every rut. A far vertex is lowered when every
	// tile it touches is drawn in detail; vertices on the boundary of the detailed
	// region stay put, so the far mesh outside the region is unchanged.
	//
	// Elsewhere it sits a few centimetres below the authored surface, so where
	// both meshes exist the detailed one always wins the depth test.
	constexpr float Hole = 600.f;
	constexpr float Sink = 6.f;

	auto TilesTouching = [this](int32 FarI, int32& OutA, int32& OutB)
	{
		// Interior far index K = FarI - 1 sits at -Half + K * FarSpacing. A tile is
		// two far cells wide: odd K is inside tile (K-1)/2, even K is on the edge
		// between tiles K/2-1 and K/2.
		const int32 K = FarI - 1;
		const int32 PerTile = FMath::Max(1, FMath::RoundToInt((TileCells * CellSize) / FarSpacing));
		if (K % PerTile != 0)
		{
			OutA = OutB = K / PerTile;
		}
		else
		{
			OutA = K / PerTile - 1;
			OutB = K / PerTile;
		}
	};

	FarVertsScratch = FarBaseVerts;
	for (int32 J = 1; J < FarN - 1; ++J)
	{
		int32 TY0, TY1;
		TilesTouching(J, TY0, TY1);
		for (int32 I = 1; I < FarN - 1; ++I)
		{
			int32 TX0, TX1;
			TilesTouching(I, TX0, TX1);

			bool bAllMeshed = true;
			for (int32 TY : { TY0, TY1 })
			{
				for (int32 TX : { TX0, TX1 })
				{
					const FIntPoint T(TX, TY);
					if (!IsTileInField(T) || MeshedMask[MaskIndex(T)] == 0)
					{
						bAllMeshed = false;
					}
				}
			}

			FVector& V = FarVertsScratch[J * FarN + I];
			V.Z -= bAllMeshed ? Hole : Sink;
		}
	}

	if (!bFarSectionCreated)
	{
		FarMesh->CreateMeshSection_LinearColor(0, FarVertsScratch, FarTris, FarNormals, FarUVs,
			FarColors, FarTangents, /*bCreateCollision=*/false, /*bSRGBConversion=*/true);
		EnsureTerrainMaterial();
		if (ResolvedTerrainMaterial != nullptr)
		{
			FarMesh->SetMaterial(0, ResolvedTerrainMaterial);
		}
		bFarSectionCreated = true;
	}
	else
	{
		FarMesh->UpdateMeshSection_LinearColor(0, FarVertsScratch, FarNormals, FarUVs,
			FarColors, FarTangents, /*bSRGBConversion=*/true);
	}
}

// ---------------------------------------------------------------------------
// Noise
// ---------------------------------------------------------------------------

float ASoilField::ValueNoise2D(float X, float Y, int32 Seed)
{
	const int32 xi = FMath::FloorToInt(X);
	const int32 yi = FMath::FloorToInt(Y);
	const float xf = X - xi;
	const float yf = Y - yi;

	auto Hash = [Seed](int32 ix, int32 iy) -> float
	{
		uint32 h = (uint32)(ix * 374761393) + (uint32)(iy * 668265263) + (uint32)(Seed * 1442695041u);
		h = (h ^ (h >> 13)) * 1274126177u;
		h = h ^ (h >> 16);
		return (float)(h & 0xFFFFFF) / (float)0xFFFFFF;
	};

	const float a = Hash(xi,     yi);
	const float b = Hash(xi + 1, yi);
	const float c = Hash(xi,     yi + 1);
	const float d = Hash(xi + 1, yi + 1);

	// Smoothstep interpolation to avoid grid-aligned creases.
	const float u = xf * xf * (3.f - 2.f * xf);
	const float v = yf * yf * (3.f - 2.f * yf);

	return FMath::Lerp(FMath::Lerp(a, b, u), FMath::Lerp(c, d, u), v);
}

float ASoilField::FBM(float X, float Y, int32 Seed, int32 Octaves, float Lacunarity, float Gain)
{
	float Sum = 0.f;
	float Amp = 1.f;
	float Freq = 1.f;
	float Norm = 0.f;
	for (int32 i = 0; i < Octaves; ++i)
	{
		Sum += Amp * ValueNoise2D(X * Freq, Y * Freq, Seed + i * 977);
		Norm += Amp;
		Amp *= Gain;
		Freq *= Lacunarity;
	}
	return Norm > 0.f ? Sum / Norm : 0.f;
}

// ---------------------------------------------------------------------------
// Terrain authoring
// ---------------------------------------------------------------------------

void ASoilField::GenerateTerrain(int32 Seed)
{
	TerrainSeed = Seed;
	GridWidth = FMath::CeilToInt((FieldHalfExtent * 2.f) / CellSize);
	TilesPerEdge = FMath::CeilToInt((float)GridWidth / (float)TileCells);

	// Nothing is generated up front. The field is a cache over a deterministic
	// function: cells come straight from EvaluateAuthored until traffic changes
	// them, and only then is a tile's data kept. Allocating all of a 600 m field
	// at 25 cm would be 5.76M cells for ground nobody drives on.
	Tiles.Empty();
	TouchedTiles.Empty();
	ResetStreaming();

	UE_LOG(LogSoil, Log, TEXT("Soil field: %d x %d cells, cell=%.1fcm, tiles/edge=%d"),
	       GridWidth, GridWidth, CellSize, TilesPerEdge);
}

void ASoilField::BuildFromHeightFunc(const TArray<float>& InFirmZ, const TArray<float>& InSoftDepth,
                                     const TArray<uint8>& InClass, int32 InWidth)
{
	// Not used by the procedural path; provided so an authored heightmap or a
	// Blender-exported terrain can be baked in later without changing the model.
	UE_LOG(LogSoil, Warning, TEXT("BuildFromHeightFunc: external heightfield path is a stub"));
}

// ---------------------------------------------------------------------------
// Generator and tile access
// ---------------------------------------------------------------------------

void ASoilField::EvaluateAuthored(float WX, float WY, float& OutFirmZ, float& OutSoftDepth,
                                  ESoilClass& OutClass) const
{
	// --- macro terrain: rolling ground with a low plateau in the centre
	const float MacroN = FBM(WX / 9000.f, WY / 9000.f, TerrainSeed, 4, 2.05f, 0.5f);
	float FirmZ = (MacroN - 0.45f) * 520.f;

	// Flatten a base pad near the origin so the garage sits level
	const float DistOrigin = FMath::Sqrt(WX * WX + WY * WY);
	const float PadBlend = FMath::Clamp((DistOrigin - 1800.f) / 1400.f, 0.f, 1.f);
	FirmZ = FMath::Lerp(0.f, FirmZ, PadBlend);

	// --- medium detail: humps and hollows, kept gentle for wheel travel
	const float MedN = FBM(WX / 2600.f, WY / 2600.f, TerrainSeed + 31, 3, 2.1f, 0.5f);
	FirmZ += (MedN - 0.5f) * 155.f * PadBlend;

	// --- fine detail: ruts-scale roughness, small
	const float FineN = FBM(WX / 520.f, WY / 520.f, TerrainSeed + 77, 2, 2.0f, 0.5f);
	FirmZ += (FineN - 0.5f) * 26.f * PadBlend;

	// --- side slope, for the lateral-stability test area
	// A long bank running along +X on the east side.
	const float BankX = 9200.f;
	const float BankWidth = 3400.f;
	const float BankAmt = FMath::Clamp(1.f - FMath::Abs(WX - BankX) / BankWidth, 0.f, 1.f);
	if (BankAmt > 0.f)
	{
		// Smoothstep: level at the foot and the crest, steepest (~21 degrees)
		// half-way up. The sine profile this replaced was steepest right at the
		// foot (~29 degrees), so the main road ran into what was effectively a
		// wall at X = 58 m.
		const float SlopeProfile = BankAmt * BankAmt * (3.f - 2.f * BankAmt);
		FirmZ += SlopeProfile * 860.f;
	}

	// --- a raised ridge to make the uphill section
	const float RidgeY = -7600.f;
	const float RidgeAmt = FMath::Clamp(1.f - FMath::Abs(WY - RidgeY) / 5200.f, 0.f, 1.f);
	FirmZ += RidgeAmt * 900.f;

	// --- ford: a channel with water over it, west of base
	const float FordX = -6400.f;
	const float FordAmt = FMath::Clamp(1.f - FMath::Abs(WX - FordX) / 1500.f, 0.f, 1.f);
	FirmZ -= FMath::Sin(FordAmt * PI) * 150.f;

	// --- twister: two low humps for diagonal articulation, south of the main
	// road. One sits under the right-hand wheels, the other under the left-hand
	// wheels a wheelbase (285 cm) further on, so a vehicle driving +X along
	// Y = -1300 has its front-left and rear-right lifted at the same moment. They
	// are narrow across track so each lifts one side only. The course marker in
	// ATestCourse (MT_Diagonal) sits at the start of this section.
	{
		struct FHump { float X, Y; };
		static const FHump Humps[] =
		{
			{ 2600.f,         -1300.f + 83.f },   // right side, first
			{ 2600.f + 285.f, -1300.f - 83.f },   // left side, a wheelbase on
		};
		for (const FHump& H : Humps)
		{
			const float U = (WX - H.X) / 150.f;
			const float V = (WY - H.Y) / 60.f;
			FirmZ += 42.f * FMath::Exp(-(U * U + V * V));
		}
	}

	// --- soil classification
	ESoilClass Cls = ESoilClass::DrySoil;
	float Soft = 6.f;

	// Firm lanes: the base pad and the two main connecting routes
	const bool bBasePad = (DistOrigin < 2000.f);
	const bool bMainRoute = (FMath::Abs(WY) < 380.f && WX > -4000.f);
	if (bBasePad || bMainRoute)
	{
		Cls = ESoilClass::FirmRoad; Soft = 4.f;
	}
	if (DistOrigin < 900.f)
	{
		Cls = ESoilClass::FirmRoad; Soft = 3.f;
	}

	// Deep mud patches: a handful of distinct blobs
	struct FMudBlob { float X, Y, R; };
	static const FMudBlob Blobs[] =
	{
		{  2400.f,  2100.f, 1250.f },
		{ -3100.f,  3300.f,  980.f },
		{  4300.f, -2600.f, 1450.f },
		{  -900.f, -4300.f,  900.f },
		{  6800.f,  5200.f, 1100.f },
		{ -7200.f, -1800.f, 1050.f },
	};
	for (const FMudBlob& B : Blobs)
	{
		const float D = FMath::Sqrt((WX - B.X) * (WX - B.X) + (WY - B.Y) * (WY - B.Y));
		if (D < B.R)
		{
			const float T = 1.f - (D / B.R);
			const float Edge = FMath::Sin(FMath::Clamp(T * 2.4f, 0.f, 1.f) * PI * 0.5f);
			if (Edge > 0.05f)
			{
				Cls = ESoilClass::DeepMud;
				Soft = FMath::Max(Soft, FMath::Lerp(12.f, 30.f, Edge));
			}
		}
	}

	// Wet soil ring around each mud blob
	for (const FMudBlob& B : Blobs)
	{
		const float D = FMath::Sqrt((WX - B.X) * (WX - B.X) + (WY - B.Y) * (WY - B.Y));
		if (D >= B.R && D < B.R * 1.55f)
		{
			if (Cls != ESoilClass::DeepMud)
			{
				Cls = ESoilClass::WetSoil;
				Soft = FMath::Max(Soft, 12.f);
			}
		}
	}

	// Gravel near the base exits
	if (DistOrigin > 1100.f && DistOrigin < 1700.f)
	{
		if (Cls == ESoilClass::DrySoil)
		{
			Cls = ESoilClass::Gravel; Soft = 7.f;
		}
	}

	// Water bed in the ford channel
	if (FordAmt > 0.25f)
	{
		Cls = ESoilClass::WaterBed;
		Soft = FMath::Max(Soft, 20.f);
	}

	// Rock outcrops on the steeper slopes
	const float RockN = ValueNoise2D(WX / 1900.f, WY / 1900.f, TerrainSeed + 444);
	if (RockN > 0.72f && BankAmt > 0.15f)
	{
		Cls = ESoilClass::Rock; Soft = 0.f;
	}

	OutFirmZ = FirmZ;
	OutSoftDepth = Soft;
	OutClass = Cls;
}

void ASoilField::GenerateAuthoredCell(int32 CX, int32 CY, FSoilCell& Out) const
{
	const FVector W = CellToWorld(CX, CY);
	float FirmZ = 0.f, Soft = 0.f;
	ESoilClass Cls = ESoilClass::DrySoil;
	EvaluateAuthored(W.X, W.Y, FirmZ, Soft, Cls);

	Out = FSoilCell();
	Out.FirmZ = FirmZ;
	Out.InitialFirmZ = FirmZ;
	Out.SoftDepth = Soft;
	Out.InitialSoftDepth = Soft;
	Out.RutDepth = 0.f;
	Out.Compaction = ParamsFor(Cls).InitialCompaction;
	Out.InitialCompaction = Out.Compaction;
	Out.Moisture = ParamsFor(Cls).Moisture;
	Out.InitialMoisture = Out.Moisture;
	Out.Berm = 0.f;
	Out.SoilClass = Cls;
	Out.LastTouchTime = -1000.f;
}

bool ASoilField::ReadCell(int32 CX, int32 CY, FSoilCell& Out) const
{
	if (CX < 0 || CY < 0 || CX >= GridWidth || CY >= GridWidth)
	{
		return false;
	}
	if (const FSoilTile* T = Tiles.Find(FIntPoint(CX / TileCells, CY / TileCells)))
	{
		const int32 LIX = TileIndex(CX, CY);
		if (T->Cells.IsValidIndex(LIX))
		{
			Out = T->Cells[LIX];
			return true;
		}
	}
	GenerateAuthoredCell(CX, CY, Out);
	return true;
}

FSoilTile* ASoilField::GetOrCreateTile(int32 TileX, int32 TileY)
{
	const FIntPoint Key(TileX, TileY);
	FSoilTile* Found = Tiles.Find(Key);
	if (Found != nullptr)
	{
		return Found;
	}

	if (!IsTileInField(Key))
	{
		return nullptr;
	}

	FSoilTile Tile;
	Tile.bAllocated = true;
	Tile.Cells.SetNumZeroed(TileCells * TileCells);
	for (int32 LY = 0; LY < TileCells; ++LY)
	{
		for (int32 LX = 0; LX < TileCells; ++LX)
		{
			const int32 CX = TileX * TileCells + LX;
			const int32 CY = TileY * TileCells + LY;
			if (CX < GridWidth && CY < GridWidth)
			{
				GenerateAuthoredCell(CX, CY, Tile.Cells[LY * TileCells + LX]);
			}
		}
	}

	// Allocation has no rendering side effect. It used to queue the tile for a
	// mesh rebuild, so every read anywhere on the map -- the forest scatter,
	// a neighbouring tile's edge row -- turned into a new mesh section, and the
	// whole 600 m field ended up meshed at 25 cm.
	return &Tiles.Add(Key, MoveTemp(Tile));
}

void ASoilField::NoteCellModified(int32 CX, int32 CY)
{
	const int32 TX = CX / TileCells;
	const int32 TY = CY / TileCells;
	const FIntPoint Key(TX, TY);

	if (FSoilTile* Tile = Tiles.Find(Key))
	{
		Tile->Revision++;
		if (!Tile->bTouched)
		{
			Tile->bTouched = true;
			TouchedTiles.Add(Key);
			bWantedDirty = true;
		}
	}
	DirtyTiles.Add(Key);

	// A tile's vertices include the first row and column of its +X/+Y neighbours,
	// and each vertex normal reads one more cell on every side. So a change near
	// a tile edge also moves vertices or normals of the adjacent tiles: rebuild
	// them too, or the ground cracks (or shows a lighting seam) where a rut
	// crosses a tile boundary.
	const int32 LX = CX % TileCells;
	const int32 LY = CY % TileCells;
	const int32 DXLo = (LX <= 1) ? -1 : 0;
	const int32 DXHi = (LX >= TileCells - 1) ? 1 : 0;
	const int32 DYLo = (LY <= 1) ? -1 : 0;
	const int32 DYHi = (LY >= TileCells - 1) ? 1 : 0;
	for (int32 DY = DYLo; DY <= DYHi; ++DY)
	{
		for (int32 DX = DXLo; DX <= DXHi; ++DX)
		{
			const FIntPoint N(TX + DX, TY + DY);
			if ((DX != 0 || DY != 0) && IsTileInField(N))
			{
				DirtyTiles.Add(N);
			}
		}
	}
}

const FSoilTile* ASoilField::FindTile(int32 TileX, int32 TileY) const
{
	return Tiles.Find(FIntPoint(TileX, TileY));
}

const FSoilClassParams& ASoilField::ParamsFor(ESoilClass InClass) const
{
	const FSoilClassParams* Found = ClassParams.Find(InClass);
	if (Found != nullptr)
	{
		return *Found;
	}
	static const FSoilClassParams Fallback;
	return Fallback;
}

// ---------------------------------------------------------------------------
// Sampling
// ---------------------------------------------------------------------------

FSoilSample ASoilField::SampleSoil(FVector WorldLocation) const
{
	FSoilSample Out;

	int32 CX, CY;
	WorldToCell(WorldLocation, CX, CY);
	if (CX < 0 || CY < 0 || CX >= GridWidth || CY >= GridWidth)
	{
		Out.bValid = false;
		Out.SurfaceZ = WorldLocation.Z;
		return Out;
	}

	// Bilinear over the 2x2 cell neighbourhood, so the wheel does not feel
	// stair-steps at cell boundaries. Cells are read through ReadCell, which
	// never allocates: sampling ground nobody has driven on costs a generator
	// evaluation, not a tile.
	const float GX = (WorldLocation.X + FieldHalfExtent) / CellSize - 0.5f;
	const float GY = (WorldLocation.Y + FieldHalfExtent) / CellSize - 0.5f;
	const int32 X0 = FMath::FloorToInt(GX);
	const int32 Y0 = FMath::FloorToInt(GY);
	const float FX = GX - X0;
	const float FY = GY - Y0;

	float Acc[7] = { 0,0,0,0,0,0,0 }; // surface, firm, soft, rut, comp, moist, berm
	float WSum = 0.f;
	uint8 ClassAcc = 0;
	float ClassW = -1.f;

	for (int32 DY = 0; DY <= 1; ++DY)
	{
		for (int32 DX = 0; DX <= 1; ++DX)
		{
			FSoilCell C;
			if (!ReadCell(X0 + DX, Y0 + DY, C))
			{
				continue;
			}

			const float W = (DX ? FX : (1.f - FX)) * (DY ? FY : (1.f - FY));

			Acc[0] += C.GetSurfaceZ() * W;
			Acc[1] += C.FirmZ * W;
			Acc[2] += C.SoftDepth * W;
			Acc[3] += C.RutDepth * W;
			Acc[4] += C.Compaction * W;
			Acc[5] += C.Moisture * W;
			Acc[6] += C.Berm * W;
			WSum += W;

			if (W > ClassW)
			{
				ClassW = W;
				ClassAcc = (uint8)C.SoilClass;
			}
		}
	}

	if (WSum <= KINDA_SMALL_NUMBER)
	{
		Out.bValid = false;
		Out.SurfaceZ = WorldLocation.Z;
		return Out;
	}

	Out.SurfaceZ = Acc[0] / WSum;
	Out.FirmZ = Acc[1] / WSum;
	Out.SoftDepth = Acc[2] / WSum;
	Out.RutDepth = Acc[3] / WSum;
	Out.Compaction = Acc[4] / WSum;
	Out.Moisture = Acc[5] / WSum;
	Out.SoilClass = (ESoilClass)ClassAcc;
	Out.bValid = true;
	// Sinkage is how far the current surface sits below the undisturbed surface.
	Out.Sinkage = FMath::Max(0.f, (Out.FirmZ + Out.SoftDepth) - Out.SurfaceZ);

	const FSoilClassParams& P = ParamsFor(Out.SoilClass);
	Out.BearingCapacity = P.BearingStrength + P.CompactionGain * Out.Compaction
	                    - MoistureLoss * Out.Moisture;
	Out.BearingCapacity = FMath::Max(Out.BearingCapacity, 8.f);

	// Traction: firm ground grips, churned ground does not. Sinkage adds some
	// bite back (the tyre digs in and finds material), but never past the base.
	const float Churn = 1.f - Out.Compaction;
	float T = P.BaseFriction * (1.f - 0.45f * Churn);
	T *= FMath::Lerp(1.f, 0.72f, Saturate01(Out.Moisture));
	Out.TractionScale = FMath::Clamp(T, 0.05f, 1.2f);

	// A tenth of the class's per-centimetre figure. Tuned against what the tyres
	// can pull: deep mud gives about 26% of the weight in traction, and at the
	// full figure a 4x4 sunk 11 cm met 32% of its weight in drag and could not
	// move at all. Now deep mud costs ~10-12% at that depth -- a heavy drag that
	// still leaves something to drive with.
	Out.RollingResistance = P.BaseRollingResistance + 0.1f * P.SinkageResistance * Out.Sinkage;

	return Out;
}

float ASoilField::GetSurfaceZ(FVector WorldLocation) const
{
	return SampleSoil(WorldLocation).SurfaceZ;
}

bool ASoilField::GetCellAt(FVector WorldLocation, FSoilCell& OutCell, FIntPoint& OutCoord) const
{
	int32 CX, CY;
	WorldToCell(WorldLocation, CX, CY);
	if (!ReadCell(CX, CY, OutCell))
	{
		return false;
	}
	OutCoord = FIntPoint(CX, CY);
	return true;
}

// ---------------------------------------------------------------------------
// Deformation
// ---------------------------------------------------------------------------

float ASoilField::ApplyWheelLoad(FVector WorldLocation, FVector Forward, FVector Right,
                                 float ContactHalfLen, float ContactHalfWid,
                                 float Pressure, float SlipSpeed, float WheelSlipRatio,
                                 float DeltaSeconds)
{
	if (DeltaSeconds <= 0.f) return 0.f;

	const float HalfLen = FMath::Max(ContactHalfLen, 1.f);
	const float HalfWid = FMath::Max(ContactHalfWid, 1.f);

	// Bounding box of the contact patch, in cells.
	const float Reach = FMath::Sqrt(HalfLen * HalfLen + HalfWid * HalfWid);
	int32 MinCX, MinCY, MaxCX, MaxCY;
	WorldToCell(WorldLocation - FVector(Reach, Reach, 0.f), MinCX, MinCY);
	WorldToCell(WorldLocation + FVector(Reach, Reach, 0.f), MaxCX, MaxCY);
	MinCX = FMath::Max(MinCX, 0); MinCY = FMath::Max(MinCY, 0);
	MaxCX = FMath::Min(MaxCX, GridWidth - 1); MaxCY = FMath::Min(MaxCY, GridWidth - 1);

	const float Now = GetWorld() ? GetWorld()->GetTimeSeconds() : 0.f;
	float TotalRut = 0.f;
	float TotalDisplaced = 0.f;

	// Pass 1: how much are we lowering?
	for (int32 CY = MinCY; CY <= MaxCY; ++CY)
	{
		for (int32 CX = MinCX; CX <= MaxCX; ++CX)
		{
			FSoilTile* Tile = GetOrCreateTile(CX / TileCells, CY / TileCells);
			if (Tile == nullptr) continue;
			const int32 LIX = (CY % TileCells) * TileCells + (CX % TileCells);
			if (!Tile->Cells.IsValidIndex(LIX)) continue;

			FSoilCell& C = Tile->Cells[LIX];

			// Elliptical contact-patch weighting in the wheel's own frame.
			const FVector CellW = CellToWorld(CX, CY);
			const FVector D = CellW - WorldLocation;
			const float Along = FVector::DotProduct(D, Forward);
			const float Across = FVector::DotProduct(D, Right);
			const float RU = Along / HalfLen;
			const float RV = Across / HalfWid;
			const float R2 = RU * RU + RV * RV;
			if (R2 > 1.f) continue;

			// Smooth falloff, 1 at the centre of the patch.
			const float W = 1.f - R2;
			if (W < 0.02f) continue;

			const FSoilClassParams& P = ParamsFor((ESoilClass)C.SoilClass);

			// Local pressure is the mean patch pressure scaled by the local weight.
			const float LocalP = Pressure * (0.5f + 0.5f * W);

			// Bearing capacity available right here.
			float Q = P.BearingStrength + P.CompactionGain * C.Compaction
			        - MoistureLoss * C.Moisture;
			Q = FMath::Max(Q, 8.f);

			const float SlipAbs = FMath::Abs(WheelSlipRatio);
			const bool bSpinning = SlipAbs > 0.35f;
			// Wet, loose ground gives way more readily.
			const float WetSoft = FMath::Lerp(0.65f, 1.35f, Saturate01(C.Moisture));

			// A spinning tyre tears the surface up, failing in bearing or not.
			if (bSpinning)
			{
				C.Compaction = Saturate01(C.Compaction - P.ChurnRate * (SlipAbs - 0.35f) * DeltaSeconds * W);
			}

			float WantRut = 0.f;

			// 1. Bearing failure. Loaded past its capacity the ground compresses until
			//    it is dense enough to carry the load. Quick -- a tyre settles into soft
			//    ground as it rolls, not over seconds -- and self-limiting: every
			//    centimetre given compacts what is left (below), raising Q to meet LocalP.
			if (LocalP > Q)
			{
				const float Excess = FMath::Min((LocalP - Q) / Q, MaxPressureExcess);
				float Sink = SinkRate * Excess * WetSoft * DeltaSeconds * W;

				// Never past equilibrium in one step: the depth at which the
				// compaction this sinkage produces (see below) lifts Q to LocalP.
				// That makes the settling effectively implicit -- fast enough to
				// finish under a wheel rolling at speed, with no overshoot.
				if (P.CompactionGain > KINDA_SMALL_NUMBER)
				{
					const float NeedC = (LocalP - Q) / P.CompactionGain;
					const float EquilibriumDepth = NeedC * FMath::Max(C.SoftDepth, 4.f) / 1.4f;
					Sink = FMath::Min(Sink, EquilibriumDepth);
				}
				WantRut += Sink;
			}

			// 2. Excavation. A spinning tyre throws material out of the rut -- this is
			//    how a stuck vehicle digs itself in. Deliberately slow, seconds of
			//    wheelspin rather than frames, and slower in packed ground.
			if (bSpinning)
			{
				const float Spin = Saturate01((SlipAbs - 0.35f) / 1.0f);
				WantRut += DigRate * Spin * WetSoft * (1.f - 0.6f * C.Compaction) * DeltaSeconds * W;
			}

			if (WantRut <= 0.f)
			{
				// Ground holds; rolling traffic still packs it a little.
				if (!bSpinning && C.RutDepth > 0.2f)
				{
					C.Compaction = Saturate01(C.Compaction + P.CompactRate * DeltaSeconds * W * 0.5f);
				}
				continue;
			}

			// Take it from the soft layer first, then a finite cut into the firm base.
			//
			// RutDepth is the TOTAL lowering of the surface (SurfaceZ = FirmZ +
			// SoftDepth - RutDepth + Berm); FirmErosion is the part of it below the soft
			// layer. SoftDepth and FirmZ stay at their authored values. The old code
			// also subtracted the taken depth from SoftDepth (and FirmZ), so the surface
			// dropped by twice what was dug and the soft layer ran out at half depth.
			const float SoftRoom = FMath::Max(0.f, C.SoftDepth - (C.RutDepth - C.FirmErosion));
			const float TakenSoft = FMath::Min(WantRut, SoftRoom);
			float TakenFirm = 0.f;
			if (WantRut > TakenSoft)
			{
				const float FirmRoom = FMath::Max(0.f, MaxFirmErosion - C.FirmErosion);
				TakenFirm = FMath::Min((WantRut - TakenSoft) * FirmErosionRate, FirmRoom);
			}

			const float Applied = TakenSoft + TakenFirm;
			if (Applied <= 0.f) continue;

			C.RutDepth += Applied;
			C.FirmErosion += TakenFirm;
			C.LastTouchTime = Now;
			TotalRut += Applied;
			TotalDisplaced += Applied * W;

			// Rolling: what the soil gave up in depth it gains in density.
			if (!bSpinning)
			{
				const float Volumetric = TakenSoft / FMath::Max(C.SoftDepth, 4.f) * 1.4f;
				C.Compaction = Saturate01(C.Compaction + Volumetric + P.CompactRate * DeltaSeconds * W);
			}

			// --- moisture rises as the surface is churned (water comes up)
			if (P.Moisture > C.Moisture)
			{
				C.Moisture = FMath::Min(P.Moisture, C.Moisture + 0.25f * DeltaSeconds * W * SlipAbs);
			}

			// --- berm: material thrown to the sides of the rut
			if (P.BermFraction > 0.f)
			{
				const float BermAdd = Applied * P.BermFraction * BermHeightGain;
				// Push the berm sideways, perpendicular to travel.
				// Cheap and stable: raise the immediate neighbour cells across-track.
				const FVector SideA = WorldLocation + Right * (HalfWid + CellSize);
				const FVector SideB = WorldLocation - Right * (HalfWid + CellSize);
				for (const FVector& SideW : { SideA, SideB })
				{
					int32 SX, SY;
					WorldToCell(SideW, SX, SY);
					if (SX < 0 || SY < 0 || SX >= GridWidth || SY >= GridWidth) continue;
					FSoilTile* STile = GetOrCreateTile(SX / TileCells, SY / TileCells);
					if (STile == nullptr) continue;
					const int32 SLIX = (SY % TileCells) * TileCells + (SX % TileCells);
					if (!STile->Cells.IsValidIndex(SLIX)) continue;
					FSoilCell& SC = STile->Cells[SLIX];
					// The berm cannot exceed a fraction of what was removed.
					SC.Berm = FMath::Min(SC.Berm + BermAdd * 0.5f, 12.f);
					SC.Compaction = Saturate01(SC.Compaction - 0.02f * W);
					// The berm cell may sit in another tile; it needs a rebuild too.
					NoteCellModified(SX, SY);
				}
			}

			// Mark for render rebuild. NoteCellModified looks the tile up again
			// rather than using Tile: the berm loop above can allocate a tile,
			// which may move the map's storage and leave Tile dangling.
			NoteCellModified(CX, CY);
		}
	}

	return TotalRut;
}

float ASoilField::ApplyPointLoad(FVector WorldLocation, float Pressure, float SlipSpeed,
                                 float Radius, float DeltaSeconds)
{
	// Used for underbody scraping: same failure law, isotropic patch.
	const FVector Fwd(1, 0, 0);
	const FVector Rgt(0, 1, 0);
	return ApplyWheelLoad(WorldLocation, Fwd, Rgt, Radius, Radius,
	                      Pressure, SlipSpeed, FMath::Clamp(SlipSpeed / 200.f, 0.f, 3.f),
	                      DeltaSeconds);
}

void ASoilField::ResetAllSoils()
{
	for (TPair<FIntPoint, FSoilTile>& KV : Tiles)
	{
		for (FSoilCell& C : KV.Value.Cells)
		{
			C.Reset();
		}
		KV.Value.Revision++;
		// Restored tiles are generator copies again, so they may be freed once
		// they leave the detailed region.
		KV.Value.bTouched = false;
		DirtyTiles.Add(KV.Key);
	}
	TouchedTiles.Reset();
	bWantedDirty = true;
	UE_LOG(LogSoil, Log, TEXT("ResetAllSoils: %d tiles restored"), Tiles.Num());
}

void ASoilField::MarkAllDirty()
{
	// Rebuild everything currently on screen; the rest is built on demand.
	for (const TPair<FIntPoint, int32>& KV : MeshedTiles)
	{
		DirtyTiles.Add(KV.Key);
	}
	bWantedDirty = true;
}

float ASoilField::GetDisplacedVolume() const
{
	// Sum of (soft removed + firm eroded) over the cell area, in cm^3.
	float Sum = 0.f;
	const float Area = CellSize * CellSize;
	for (const TPair<FIntPoint, FSoilTile>& KV : Tiles)
	{
		for (const FSoilCell& C : KV.Value.Cells)
		{
			Sum += C.RutDepth * Area;
		}
	}
	return Sum;
}

// ---------------------------------------------------------------------------
// Rendering: the visible rut IS the collision surface
// ---------------------------------------------------------------------------

FLinearColor ASoilField::ShadeCell(const FSoilCell& C, float WX, float WY) const
{
	// Albedo per class, authored in sRGB. Enum order:
	// FirmRoad, DrySoil, WetSoil, DeepMud, Gravel, Rock, WaterBed.
	static const FColor ClassAlbedo[] =
	{
		FColor(126, 116, 100),   // FirmRoad: packed grey-brown dirt
		FColor(112,  98,  78),   // DrySoil: forest soil
		FColor( 84,  70,  54),   // WetSoil: damp, darker
		FColor( 64,  52,  40),   // DeepMud: dark wet mud
		FColor(128, 126, 120),   // Gravel: grey
		FColor(110, 110, 108),   // Rock
		FColor( 60,  56,  48),   // WaterBed: silt under water
	};
	const int32 Idx = FMath::Clamp((int32)C.SoilClass, 0, (int32)UE_ARRAY_COUNT(ClassAlbedo) - 1);
	FLinearColor Col = FLinearColor::FromSRGBColor(ClassAlbedo[Idx]);

	// Forest floor: undisturbed dry soil is patchily covered in moss and low
	// growth. A rut strips it and shows the soil underneath.
	const float RutCover = Saturate01(C.RutDepth / 4.f);
	if (C.SoilClass == ESoilClass::DrySoil)
	{
		const float G = 0.7f * ValueNoise2D(WX / 900.f, WY / 900.f, TerrainSeed + 903)
		              + 0.3f * ValueNoise2D(WX / 260.f, WY / 260.f, TerrainSeed + 904);
		const float Cover = Saturate01((G - 0.35f) * 2.2f) * (1.f - RutCover);
		Col = FMath::Lerp(Col, FLinearColor::FromSRGBColor(FColor(80, 92, 54)), Cover * 0.85f);
	}

	// Low-frequency variation so large areas of one class do not read as flat paint.
	const float Var = 0.6f * ValueNoise2D(WX / 180.f, WY / 180.f, TerrainSeed + 901)
	                + 0.4f * ValueNoise2D(WX / 700.f, WY / 700.f, TerrainSeed + 902);
	const float Bright = FMath::Lerp(0.82f, 1.14f, Var);

	// Ruts: churned soil is darker and wetter. Berms are drier spoil, a touch lighter.
	const float RutT = Saturate01(C.RutDepth / 30.f);
	const float Churn = Saturate01(1.f - C.Compaction);
	const float Dark = (1.f - 0.42f * RutT - 0.18f * Churn) * (1.f + 0.12f * Saturate01(C.Berm / 8.f));

	Col.R *= Bright * Dark;
	Col.G *= Bright * Dark * (1.f - 0.04f * RutT);
	Col.B *= Bright * Dark * (1.f - 0.12f * RutT);
	Col.A = RutT;
	return Col;
}

void ASoilField::BuildTileGeometry(const FIntPoint& TileCoord)
{
	// The extra row and column are the neighbour's first, so adjacent tiles share
	// their boundary vertices exactly. The padded grid adds one more cell on every
	// side for the normals.
	const int32 N = TileCells + 1;
	const int32 P = N + 2;
	const int32 BaseX = TileCoord.X * TileCells;
	const int32 BaseY = TileCoord.Y * TileCells;

	ScratchHeights.SetNumUninitialized(P * P);
	ScratchVerts.SetNumUninitialized(N * N);
	ScratchNormals.SetNumUninitialized(N * N);
	ScratchUVs.SetNumUninitialized(N * N);
	ScratchColors.SetNumUninitialized(N * N);
	ScratchTangents.Init(FProcMeshTangent(1.f, 0.f, 0.f), N * N);

	// Vertices sit at cell centres -- the same points SampleSoil interpolates
	// between -- so a rut is drawn exactly where the wheel feels it. (They used to
	// sit on cell corners, half a cell away from the physics surface.) Cells past
	// the field edge clamp to the edge.
	for (int32 GY = 0; GY < P; ++GY)
	{
		for (int32 GX = 0; GX < P; ++GX)
		{
			const int32 CX = FMath::Clamp(BaseX + GX - 1, 0, GridWidth - 1);
			const int32 CY = FMath::Clamp(BaseY + GY - 1, 0, GridWidth - 1);
			FSoilCell C;
			ReadCell(CX, CY, C);
			const float Z = C.GetSurfaceZ();
			ScratchHeights[GY * P + GX] = Z;

			if (GX >= 1 && GX <= N && GY >= 1 && GY <= N)
			{
				const int32 I = (GY - 1) * N + (GX - 1);
				const FVector W = CellToWorld(CX, CY);
				ScratchVerts[I] = FVector(W.X, W.Y, Z);
				// 1 UV unit per metre, so a tiling texture lines up across tiles.
				ScratchUVs[I] = FVector2D(W.X / 100.f, W.Y / 100.f);
				ScratchColors[I] = ShadeCell(C, W.X, W.Y);
			}
		}
	}

	// Normals by central differences over the padded grid: a vertex on a tile
	// edge gets the same normal from both tiles, so no lighting seam shows.
	const float Inv2 = 1.f / (2.f * CellSize);
	for (int32 LY = 0; LY < N; ++LY)
	{
		for (int32 LX = 0; LX < N; ++LX)
		{
			const int32 GX = LX + 1;
			const int32 GY = LY + 1;
			const float HL = ScratchHeights[GY * P + GX - 1];
			const float HR = ScratchHeights[GY * P + GX + 1];
			const float HD = ScratchHeights[(GY - 1) * P + GX];
			const float HU = ScratchHeights[(GY + 1) * P + GX];
			ScratchNormals[LY * N + LX] =
				FVector(-(HR - HL) * Inv2, -(HU - HD) * Inv2, 1.f).GetSafeNormal();
		}
	}
}
