// PropsAndCourse.cpp

#include "PropsAndCourse.h"
#include "../Soil/SoilField.h"
#include "../Mudtrack.h"

#include "Engine/World.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "EngineUtils.h"
#include "Components/HierarchicalInstancedStaticMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"

DEFINE_LOG_CATEGORY_STATIC(LogWorld, Log, All);

namespace
{
	/** Deterministic 32-bit hash, so scatter is identical every run. */
	FORCEINLINE uint32 Hash32(uint32 X)
	{
		X ^= X >> 16; X *= 0x7feb352dU;
		X ^= X >> 15; X *= 0x846ca68bU;
		X ^= X >> 16;
		return X;
	}
	FORCEINLINE float Rand01(int32 A, int32 B, int32 S)
	{
		return (Hash32((uint32)(A * 73856093) ^ (uint32)(B * 19349663) ^ (uint32)(S * 83492791)) & 0xFFFFFF)
			/ (float)0xFFFFFF;
	}
	FORCEINLINE float RandRange(int32 A, int32 B, int32 S, float Lo, float Hi)
	{
		return Lo + (Hi - Lo) * Rand01(A, B, S);
	}
}

// ===========================================================================
// AForestScatter
// ===========================================================================

AForestScatter::AForestScatter()
{
	PrimaryActorTick.bCanEverTick = false;

	USceneComponent* Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;

	// Default species: three trees, a bush and a rock, sized for a mixed forest.
	// Paths match what import_assets.py produces under /Game/Mudtrack/Props.
	FScatterSpecies T1;
	T1.Mesh = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Props/Prop_MT_Tree_A.Prop_MT_Tree_A")));
	T1.MinScale = 0.85f; T1.MaxScale = 1.30f; T1.Share = 0.26f; T1.MinSpacing = 520.f;
	T1.SinkDepth = 14.f;

	FScatterSpecies T2;
	T2.Mesh = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Props/Prop_MT_Tree_B.Prop_MT_Tree_B")));
	T2.MinScale = 0.80f; T2.MaxScale = 1.22f; T2.Share = 0.22f; T2.MinSpacing = 620.f;
	T2.SinkDepth = 14.f;

	FScatterSpecies T3;
	T3.Mesh = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Props/Prop_MT_Tree_C.Prop_MT_Tree_C")));
	T3.MinScale = 0.70f; T3.MaxScale = 1.35f; T3.Share = 0.30f; T3.MinSpacing = 320.f;
	T3.SinkDepth = 10.f;

	FScatterSpecies B;
	B.Mesh = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Props/Prop_MT_Bush_A.Prop_MT_Bush_A")));
	B.MinScale = 0.65f; B.MaxScale = 1.45f; B.Share = 0.14f; B.MinSpacing = 240.f;
	B.SinkDepth = 6.f;

	FScatterSpecies R;
	R.Mesh = TSoftObjectPtr<UStaticMesh>(FSoftObjectPath(TEXT("/Game/Mudtrack/Props/Prop_MT_Rock_A.Prop_MT_Rock_A")));
	R.MinScale = 0.30f; R.MaxScale = 0.75f; R.Share = 0.08f; R.MinSpacing = 500.f;
	R.SinkDepth = 18.f;

	Species = { T1, T2, T3, B, R };
}

void AForestScatter::BeginPlay()
{
	Super::BeginPlay();
	Populate();
}

bool AForestScatter::GetGroundSample(const FVector& XY, FVector& OutPoint, FVector& OutNormal)
{
	if (Soil == nullptr)
	{
		OutPoint = XY;
		OutNormal = FVector::UpVector;
		return false;
	}

	const FSoilSample S = Soil->SampleSoil(XY);
	if (!S.bValid)
	{
		return false;
	}

	// Estimate the normal by sampling the surface a little away in X and Y.
	const float D = Soil->CellSize * 1.5f;
	const FSoilSample Sx = Soil->SampleSoil(XY + FVector(D, 0.f, 0.f));
	const FSoilSample Sy = Soil->SampleSoil(XY + FVector(0.f, D, 0.f));
	if (!Sx.bValid || !Sy.bValid)
	{
		OutPoint = FVector(XY.X, XY.Y, S.SurfaceZ);
		OutNormal = FVector::UpVector;
		return true;
	}

	const FVector Tx(D, 0.f, Sx.SurfaceZ - S.SurfaceZ);
	const FVector Ty(0.f, D, Sy.SurfaceZ - S.SurfaceZ);
	OutNormal = FVector::CrossProduct(Ty, Tx).GetSafeNormal();
	if (OutNormal.Z < 0.f) OutNormal = -OutNormal;
	if (OutNormal.IsNearlyZero()) OutNormal = FVector::UpVector;

	OutPoint = FVector(XY.X, XY.Y, S.SurfaceZ);
	return true;
}

bool AForestScatter::IsPlacementAllowed(const FVector& P, float SeedVal, float Spacing,
                                        float SinkDepth, FVector& OutLocation, FRotator& OutRotation)
{
	// Keep the base and the immediate approach clear.
	if (FVector::Dist2D(P, FVector::ZeroVector) < BaseKeepOut)
	{
		return false;
	}

	FVector GroundPoint, Normal;
	if (!GetGroundSample(P, GroundPoint, Normal))
	{
		return false;
	}

	// Reject cliff faces for the big species; small plants may cling.
	const float SlopeDeg = FMath::RadiansToDegrees(FMath::Acos(FMath::Clamp(Normal.Z, -1.f, 1.f)));
	if (SlopeDeg > MaxSlopeDeg)
	{
		return false;
	}

	// Do not plant in the middle of a mud pit: it would look wrong to have a
	// mature conifer standing in churned slop, and it complicates the physics.
	if (Soil != nullptr)
	{
		const FSoilSample S = Soil->SampleSoil(P);
		if (S.SoilClass == ESoilClass::DeepMud || S.SoilClass == ESoilClass::WaterBed)
		{
			return false;
		}
	}

	// Sink slightly so the base is buried and there is no visible float gap.
	OutLocation = GroundPoint - FVector(0.f, 0.f, SinkDepth);
	OutRotation = FRotator(0.f, RandRange((int32)P.X, (int32)P.Y, (int32)SeedVal, 0.f, 360.f), 0.f);
	return true;
}

void AForestScatter::Populate()
{
	if (bPopulated) return;
	bPopulated = true;

	UWorld* World = GetWorld();
	if (World == nullptr) return;

	for (TActorIterator<ASoilField> It(World); It; ++It) { Soil = *It; break; }
	if (Soil == nullptr)
	{
		UE_LOG(LogWorld, Warning, TEXT("ForestScatter: no ASoilField; scatter skipped"));
		return;
	}

	// Actor BeginPlay order is not guaranteed, so the soil field may not have run
	// its own BeginPlay yet. Without this the grid is unsized, every sample comes
	// back invalid, and the scatter silently places nothing.
	Soil->EnsureInitialised();

	if (Species.Num() == 0)
	{
		UE_LOG(LogWorld, Warning, TEXT("ForestScatter: no species configured"));
		return;
	}

	const FVector Origin = GetActorLocation();

	// One HISM component per species.
	Components.Reset();
	TArray<float> ShareCumulative;
	float Running = 0.f;
	for (int32 i = 0; i < Species.Num(); ++i)
	{
		Running += FMath::Max(Species[i].Share, 0.f);
		ShareCumulative.Add(Running);

		UStaticMesh* M = Species[i].Mesh.LoadSynchronous();
		UHierarchicalInstancedStaticMeshComponent* C =
			NewObject<UHierarchicalInstancedStaticMeshComponent>(this,
				*FString::Printf(TEXT("Scatter_%d"), i));
		C->SetupAttachment(RootComponent);
		C->RegisterComponent();
		C->SetMobility(EComponentMobility::Static);
		C->SetCollisionEnabled(ECollisionEnabled::QueryAndPhysics);
		C->SetCollisionObjectType(ECC_WorldStatic);
		C->SetCollisionResponseToAllChannels(ECR_Block);
		C->bCastDynamicShadow = true;
		C->SetCullDistances(0, 60000);
		if (M != nullptr)
		{
			C->SetStaticMesh(M);
		}
		else
		{
			UE_LOG(LogWorld, Warning, TEXT("ForestScatter: species %d mesh missing"), i);
		}
		Components.Add(C);
	}

	if (Running <= 0.f) return;
	for (float& S : ShareCumulative) S /= Running;

	// Rejection-sample positions. The trial grid is deterministic in the seed.
	const int32 Grid = FMath::CeilToInt(FMath::Sqrt((float)TotalInstances * 2.6f));
	const float Cell = (AreaHalfExtent * 2.f) / FMath::Max(Grid, 1);

	int32 Accepted = 0;
	int32 Trials = 0;
	const int32 MaxTrials = TotalInstances * 12;

	while (Accepted < TotalInstances && Trials < MaxTrials)
	{
		const int32 GX = Trials % Grid;
		const int32 GY = (Trials / Grid) % Grid;
		const int32 Layer = Trials / (Grid * Grid);
		++Trials;

		const float JitterX = RandRange(GX, GY, Seed + 11, -Cell * 0.48f, Cell * 0.48f);
		const float JitterY = RandRange(GY, GX, Seed + 23, -Cell * 0.48f, Cell * 0.48f);

		const FVector P = Origin + FVector(
			-AreaHalfExtent + (GX + 0.5f) * Cell + JitterX,
			-AreaHalfExtent + (GY + 0.5f) * Cell + JitterY,
			0.f);

		if (FMath::Abs(P.X - Origin.X) > AreaHalfExtent) continue;
		if (FMath::Abs(P.Y - Origin.Y) > AreaHalfExtent) continue;

		// Pick a species from the cumulative distribution.
		const float Roll = Rand01(GX + Layer, GY, Seed + 777);
		int32 Pick = Species.Num() - 1;
		for (int32 i = 0; i < ShareCumulative.Num(); ++i)
		{
			if (Roll <= ShareCumulative[i]) { Pick = i; break; }
		}
		if (!Components.IsValidIndex(Pick) || Components[Pick] == nullptr) continue;
		if (Components[Pick]->GetStaticMesh() == nullptr) continue;

		const FScatterSpecies& Sp = Species[Pick];

		FVector Loc; FRotator Rot;
		if (!IsPlacementAllowed(P, (float)Pick * 17.3f, Sp.MinSpacing, Sp.SinkDepth, Loc, Rot))
		{
			continue;
		}

		// Spacing check against what this species has already placed.
		bool bTooClose = false;
		const int32 Existing = Components[Pick]->GetInstanceCount();
		for (int32 e = FMath::Max(0, Existing - 260); e < Existing; ++e)
		{
			FTransform T;
			if (Components[Pick]->GetInstanceTransform(e, T, false))
			{
				if (FVector::Dist2D(T.GetLocation(), Loc) < Sp.MinSpacing)
				{
					bTooClose = true;
					break;
				}
			}
		}
		if (bTooClose) continue;

		if (Sp.YawJitter <= 0.f) Rot.Yaw = 0.f;

		FTransform Xf;
		Xf.SetLocation(Loc);
		Xf.SetRotation(Rot.Quaternion());
		Xf.SetScale3D(FVector(RandRange(GX, GY, Seed + 991,
			Sp.MinScale, Sp.MaxScale)));

		Components[Pick]->AddInstance(Xf, /*bWorldSpace=*/true);
		++Accepted;
		++PlacedCount;
	}

	UE_LOG(LogWorld, Log, TEXT("ForestScatter: placed %d instances from %d trials across %d species"),
		PlacedCount, Trials, Species.Num());
}

// ===========================================================================
// ATestCourse
// ===========================================================================

ATestCourse::ATestCourse()
{
	PrimaryActorTick.bCanEverTick = false;
	USceneComponent* Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;
}

void ATestCourse::BeginPlay()
{
	Super::BeginPlay();
	if (bBuildOnBeginPlay) BuildCourse();
}

AActor* ATestCourse::SpawnBox(const FString& Name, const FVector& Loc, const FRotator& Rot,
                              const FVector& Size, const FLinearColor& Colour, FName Tag)
{
	UWorld* World = GetWorld();
	if (World == nullptr) return nullptr;

	// A plain cube mesh with a dynamic colour, so the course is legible without
	// authoring materials. Uses the engine's basic cube.
	static const TCHAR* CubePath = TEXT("/Engine/BasicShapes/Cube.Cube");
	UStaticMesh* Cube = LoadObject<UStaticMesh>(nullptr, CubePath);
	if (Cube == nullptr) return nullptr;

	AStaticMeshActor* A = World->SpawnActor<AStaticMeshActor>(Loc, Rot);
	if (A == nullptr) return nullptr;
	// SetActorLabel is editor-only ("WITH_EDITOR"); in a packaged build the
	// label API does not exist at all. The tag below is what the tests actually
	// match on, so the label is a convenience in the editor, not a requirement.
#if WITH_EDITOR
	A->SetActorLabel(Name);
#endif
	A->Tags.Add(Tag);

	UStaticMeshComponent* C = A->GetStaticMeshComponent();
	C->SetStaticMesh(Cube);
	C->SetMobility(EComponentMobility::Movable);
	C->SetCollisionEnabled(ECollisionEnabled::QueryAndPhysics);
	C->SetCollisionObjectType(ECC_WorldStatic);
	C->SetCollisionResponseToAllChannels(ECR_Block);

	// The engine cube is 100 cm; scale by half-extent in cm.
	C->SetWorldScale3D(Size / 100.f);

	if (UMaterialInterface* BaseMat = LoadObject<UMaterialInterface>(
			nullptr, TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial")))
	{
		UMaterialInstanceDynamic* MID = UMaterialInstanceDynamic::Create(BaseMat, A);
		MID->SetVectorParameterValue(TEXT("Color"), Colour);
		C->SetMaterial(0, MID);
	}

	return A;
}

AActor* ATestCourse::SpawnMeshAt(const FString& Name, UStaticMesh* Mesh, const FVector& Loc,
                                 const FRotator& Rot, const FVector& Scale, FName Tag)
{
	UWorld* World = GetWorld();
	if (World == nullptr || Mesh == nullptr) return nullptr;

	AStaticMeshActor* A = World->SpawnActor<AStaticMeshActor>(Loc, Rot);
	if (A == nullptr) return nullptr;
#if WITH_EDITOR
	A->SetActorLabel(Name);
#endif
	A->Tags.Add(Tag);

	UStaticMeshComponent* C = A->GetStaticMeshComponent();
	C->SetStaticMesh(Mesh);
	C->SetMobility(EComponentMobility::Movable);
	C->SetCollisionEnabled(ECollisionEnabled::QueryAndPhysics);
	C->SetCollisionObjectType(ECC_WorldStatic);
	C->SetCollisionResponseToAllChannels(ECR_Block);
	C->SetWorldScale3D(Scale);
	return A;
}

void ATestCourse::BuildCourse()
{
	UWorld* World = GetWorld();
	if (World == nullptr) return;

	// Avoid building twice if BeginPlay and an explicit call both happen.
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (It->ActorHasTag(TEXT("MT_CourseRoot")))
		{
			UE_LOG(LogWorld, Log, TEXT("TestCourse already built"));
			return;
		}
	}
	Tags.Add(TEXT("MT_CourseRoot"));

	for (TActorIterator<ASoilField> It(World); It; ++It) { Soil = *It; break; }

	auto GroundZ = [&](float X, float Y) -> float
	{
		if (Soil == nullptr) return 0.f;
		const FSoilSample S = Soil->SampleSoil(FVector(X, Y, 0.f));
		return S.bValid ? S.SurfaceZ : 0.f;
	};

	// -----------------------------------------------------------------------
	// 1. Diagonal articulation ("twister").
	//
	// The mounds themselves are part of the terrain -- ASoilField::EvaluateAuthored
	// raises two low humps a wheelbase apart, one under each side of the vehicle,
	// so crossing them lifts opposite corners. They used to be two 20 x 14 m boxes
	// with 44 cm vertical sides, one of which lay across the main road straight
	// ahead of the spawn: a wall the vehicle hit at speed rather than an obstacle
	// it could climb. What remains here is a non-colliding marker the test uses to
	// find the start of the section, and a post beside it.
	// -----------------------------------------------------------------------
	{
		const float CX = 2600.f, CY = -1300.f;   // keep in step with EvaluateAuthored
		const float Z = GroundZ(CX, CY);

		if (AActor* Mark = SpawnBox(TEXT("Course_Diagonal_Start"), FVector(CX, CY, Z + 20.f),
			FRotator::ZeroRotator, FVector(20.f, 20.f, 20.f),
			FLinearColor(0.9f, 0.6f, 0.1f), TEXT("MT_Diagonal")))
		{
			Mark->SetActorHiddenInGame(true);
			Mark->SetActorEnableCollision(false);
		}

		SpawnBox(TEXT("Course_Diagonal_Marker"), FVector(CX, CY + 420.f, Z + 120.f),
			FRotator::ZeroRotator, FVector(40.f, 40.f, 240.f),
			FLinearColor(0.9f, 0.6f, 0.1f), TEXT("MT_CourseMarker"));
	}

	// -----------------------------------------------------------------------
	// 2. Log crossing: a log lying across a firm lane.
	// -----------------------------------------------------------------------
	{
		const float CX = 1500.f, CY = 3200.f;
		const float Z = GroundZ(CX, CY);

		UStaticMesh* LogMesh = LoadObject<UStaticMesh>(nullptr,
			TEXT("/Game/Mudtrack/Props/Prop_MT_Log_A.Prop_MT_Log_A"));
		if (LogMesh != nullptr)
		{
			// The log is authored along its length; rotate so it lies across the
			// lane (perpendicular to travel along +X).
			SpawnMeshAt(TEXT("Course_Log"), LogMesh,
				FVector(CX, CY, Z + 30.f), FRotator(0.f, 90.f, 90.f),
				FVector(1.f, 1.f, 1.f), TEXT("MT_Log"));
		}
		else
		{
			// Fallback so the test still has something to cross.
			SpawnBox(TEXT("Course_Log_Fallback"), FVector(CX, CY, Z + 30.f),
				FRotator::ZeroRotator, FVector(60.f, 550.f, 60.f),
				FLinearColor(0.35f, 0.24f, 0.14f), TEXT("MT_Log"));
		}
	}

	// -----------------------------------------------------------------------
	// 3. Transverse bumps (washboard) along a firm lane.
	// -----------------------------------------------------------------------
	{
		const float CX = 4200.f, CY = 1200.f;
		const float Z = GroundZ(CX, CY);
		for (int32 i = 0; i < 8; ++i)
		{
			const float X = CX + i * 320.f;
			SpawnBox(FString::Printf(TEXT("Course_Bump_%d"), i),
				FVector(X, CY, Z + 9.f), FRotator::ZeroRotator,
				FVector(120.f, 900.f, 18.f),
				FLinearColor(0.45f, 0.36f, 0.26f), TEXT("MT_Bump"));
		}
	}

	// -----------------------------------------------------------------------
	// 4. Side slope: provided by the terrain itself -- the bank the generator
	//    raises along X = 92 m (see EvaluateAuthored). The 26 x 90 m, 7.6 m tall
	//    block that used to stand here ran straight across the main road.
	// -----------------------------------------------------------------------

	// -----------------------------------------------------------------------
	// 5. Paired dry / muddy lanes for the identical-input comparison.
	// -----------------------------------------------------------------------
	{
		// These sit in already-classified ground; the markers only label them.
		const float CZ = GroundZ(-2200.f, 2400.f);
		SpawnBox(TEXT("Course_Lane_Dry_Marker"), FVector(-2200.f, 2400.f, CZ + 100.f),
			FRotator::ZeroRotator, FVector(30.f, 30.f, 200.f),
			FLinearColor(0.9f, 0.9f, 0.3f), TEXT("MT_CourseMarker"));

		const float MZ = GroundZ(-2000.f, -2200.f);
		SpawnBox(TEXT("Course_Lane_Mud_Marker"), FVector(-2000.f, -2200.f, MZ + 100.f),
			FRotator::ZeroRotator, FVector(30.f, 30.f, 200.f),
			FLinearColor(0.35f, 0.22f, 0.10f), TEXT("MT_CourseMarker"));
	}

	// -----------------------------------------------------------------------
	// 6. Winch pit: a hollow with a solid anchor tree on the far side.
	// -----------------------------------------------------------------------
	{
		const float CX = -3000.f, CY = -1200.f;
		const float Z = GroundZ(CX, CY);

		// Low walls to form a pit the vehicle can drop into.
		SpawnBox(TEXT("Course_Pit_Wall_A"), FVector(CX, CY + 900.f, Z + 120.f),
			FRotator::ZeroRotator, FVector(1800.f, 200.f, 240.f),
			FLinearColor(0.38f, 0.30f, 0.22f), TEXT("MT_Pit"));
		SpawnBox(TEXT("Course_Pit_Wall_B"), FVector(CX, CY - 900.f, Z + 120.f),
			FRotator::ZeroRotator, FVector(1800.f, 200.f, 240.f),
			FLinearColor(0.38f, 0.30f, 0.22f), TEXT("MT_Pit"));
		SpawnBox(TEXT("Course_Pit_Wall_C"), FVector(CX - 900.f, CY, Z + 120.f),
			FRotator::ZeroRotator, FVector(200.f, 1800.f, 240.f),
			FLinearColor(0.38f, 0.30f, 0.22f), TEXT("MT_Pit"));

		// The anchor: a big trunk-like post, deliberately heavy and static so the
		// winch has something that will not be dragged.
		UStaticMesh* TreeMesh = LoadObject<UStaticMesh>(nullptr,
			TEXT("/Game/Mudtrack/Props/Prop_MT_Tree_A.Prop_MT_Tree_A"));
		const FVector AnchorLoc(CX + 900.f, CY, Z);
		if (TreeMesh != nullptr)
		{
			SpawnMeshAt(TEXT("Course_WinchAnchor"), TreeMesh,
				AnchorLoc + FVector(0.f, 0.f, -10.f), FRotator(0.f, 0.f, 0.f),
				FVector(1.15f, 1.15f, 1.15f), TEXT("MT_WinchAnchor"));
		}
		else
		{
			SpawnBox(TEXT("Course_WinchAnchor_Fallback"), AnchorLoc + FVector(0.f, 0.f, 220.f),
				FRotator::ZeroRotator, FVector(90.f, 90.f, 440.f),
				FLinearColor(0.22f, 0.15f, 0.08f), TEXT("MT_WinchAnchor"));
		}
	}

	UE_LOG(LogWorld, Log, TEXT("Test course built"));
}
