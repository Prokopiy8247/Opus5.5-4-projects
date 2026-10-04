// TestDriver.cpp

#include "TestDriver.h"
#include "MudtrackTests.h"
#include "../Soil/SoilField.h"
#include "../Vehicle/OffroadVehiclePawn.h"
#include "../World/PropsAndCourse.h"
#include "../Mudtrack.h"

#include "Engine/World.h"
#include "Engine/Engine.h"
#include "EngineUtils.h"
#include "Misc/Paths.h"
#include "Misc/FileHelper.h"
#include "GameFramework/PlayerController.h"
#include "Kismet/GameplayStatics.h"

DEFINE_LOG_CATEGORY_STATIC(LogTestDriver, Log, All);

ATestDriver::ATestDriver()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.TickGroup = TG_PostPhysics;
}

void ATestDriver::BeginPlay()
{
	Super::BeginPlay();
	EnsureScenario();
}

void ATestDriver::EnsureScenario()
{
	UWorld* World = GetWorld();
	if (World == nullptr) return;

	// Soil field
	ASoilField* Soil = nullptr;
	for (TActorIterator<ASoilField> It(World); It; ++It) { Soil = *It; break; }
	if (Soil != nullptr)
	{
		// Actor BeginPlay order is not guaranteed; make the field usable before
		// the course and the vehicle start sampling it.
		Soil->EnsureInitialised();
	}
	if (Soil == nullptr)
	{
		Soil = World->SpawnActor<ASoilField>(FVector::ZeroVector, FRotator::ZeroRotator);
		if (Soil != nullptr)
		{
			Soil->FieldHalfExtent = 30000.f;
			Soil->CellSize = 25.f;
			Soil->TileCells = 32;
			Soil->bAllowSettling = false;
			Soil->GenerateTerrain(20261002);
			Soil->MarkAllDirty();
			UE_LOG(LogTestDriver, Log, TEXT("spawned soil field"));
		}
	}

	// Test course
	bool bHasCourse = false;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (It->ActorHasTag(TEXT("MT_CourseRoot"))) { bHasCourse = true; break; }
	}
	if (!bHasCourse)
	{
		// SpawnActor triggers BeginPlay, which builds the course.
		World->SpawnActor<ATestCourse>(FVector::ZeroVector, FRotator::ZeroRotator);
		UE_LOG(LogTestDriver, Log, TEXT("spawned test course"));
	}

	// Forest
	bool bHasForest = false;
	for (TActorIterator<AForestScatter> It(World); It; ++It) { bHasForest = true; break; }
	if (!bHasForest)
	{
		AForestScatter* Sc = World->SpawnActor<AForestScatter>(FVector::ZeroVector, FRotator::ZeroRotator);
		if (Sc != nullptr)
		{
			Sc->TotalInstances = 2600;
			Sc->Seed = 20261002;
			Sc->Populate();
			UE_LOG(LogTestDriver, Log, TEXT("scatter placed %d instances"), Sc->GetPlacedCount());
		}
	}

	// Vehicle: the GameMode should have spawned one; create one if not.
	AOffroadVehiclePawn* V = nullptr;
	for (TActorIterator<AOffroadVehiclePawn> It(World); It; ++It) { V = *It; break; }
	if (V == nullptr)
	{
		FVector Loc(0.f, 0.f, 300.f);
		if (Soil != nullptr)
		{
			const FSoilSample S = Soil->SampleSoil(FVector::ZeroVector);
			if (S.bValid) Loc.Z = S.SurfaceZ + 160.f;
		}
		V = World->SpawnActor<AOffroadVehiclePawn>(Loc, FRotator::ZeroRotator);
		if (V != nullptr)
		{
			if (APlayerController* PC = World->GetFirstPlayerController())
			{
				PC->Possess(V);
			}
		}
		UE_LOG(LogTestDriver, Log, TEXT("spawned vehicle: %s"), V ? TEXT("ok") : TEXT("FAILED"));
	}

	if (V != nullptr)
	{
		// Idempotent: safe whether or not BeginPlay reached it.
		V->InitializeVehicleRuntime();
	}
}

void ATestDriver::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	UWorld* World = GetWorld();
	if (World == nullptr) return;

	// Phase 1: warm up, so the vehicle settles onto the ground before we test.
	if (!bStarted)
	{
		Elapsed += DeltaSeconds;
		if (Elapsed < WarmupSeconds) return;

		bStarted = true;

		const FString OutDir = FPaths::ProjectSavedDir() / TEXT("MudtrackTests");
		const FString OutFile = FPaths::Combine(OutDir, OutputRelativePath);

		UE_LOG(LogTestDriver, Display,
			TEXT("warmup complete (%.2f s of game time); starting protocol tests"), Elapsed);

		if (!UMudtrackTests::BeginRun(this, OutFile))
		{
			UE_LOG(LogTestDriver, Error, TEXT("could not start the protocol tests"));
			if (bExitWhenFinished)
			{
				FGenericPlatformMisc::RequestExitWithStatus(false, 2);
			}
			Finished = true;
		}
		return;
	}

	// Phase 2: advance the suite by one step this frame. It returns false once
	// the whole plan is complete.
	if (!Finished)
	{
		const bool bMore = UMudtrackTests::Advance(DeltaSeconds);
		if (!bMore)
		{
			Finished = true;

			const TArray<FTestResult> Results = UMudtrackTests::GetLastResults();
			for (const FTestResult& R : Results)
			{
				++Total;
				if (R.bPassed) ++Passed;
				UE_LOG(LogTestDriver, Display, TEXT("[%s] %s :: %s"),
					R.bPassed ? TEXT("PASS") : TEXT("FAIL"), *R.Name, *R.Detail);
				for (const FString& M : R.Measurements)
				{
					UE_LOG(LogTestDriver, Display, TEXT("      %s"), *M);
				}
			}

			UE_LOG(LogTestDriver, Display, TEXT("%s"), *UMudtrackTests::GetLastSummary());
			UE_LOG(LogTestDriver, Display, TEXT("=== RESULT %d/%d PASSED ==="), Passed, Total);

			if (bExitWhenFinished)
			{
				const bool bAllPassed = (Total > 0 && Passed == Total);
				FGenericPlatformMisc::RequestExitWithStatus(false, bAllPassed ? 0 : 1);
			}
		}
	}
}
