// MudtrackGameMode.cpp

#include "MudtrackGameMode.h"
#include "../Vehicle/OffroadVehiclePawn.h"
#include "../Soil/SoilField.h"
#include "../UI/MudtrackHUD.h"
#include "../Tests/TestDriver.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "UnrealClient.h"
#include "TimerManager.h"
#include "Engine/StaticMesh.h"
#include "Components/StaticMeshComponent.h"
#include "Components/HierarchicalInstancedStaticMeshComponent.h"
#include "GameFramework/PlayerStart.h"
#include "Engine/ExponentialHeightFog.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Engine/DirectionalLight.h"
#include "Components/DirectionalLightComponent.h"
#include "Engine/SkyLight.h"
#include "Components/SkyLightComponent.h"
#include "Engine/StaticMeshActor.h"
#include "PhysicsEngine/BodyInstance.h"

DEFINE_LOG_CATEGORY_STATIC(LogMudtrackSetup, Log, All);

AMudtrackGameMode::AMudtrackGameMode()
{
	DefaultPawnClass = AOffroadVehiclePawn::StaticClass();
	HUDClass = AMudtrackHUD::StaticClass();
	bStartPlayersAsSpectators = false;
}

void AMudtrackGameMode::BeginPlay()
{
	Super::BeginPlay();

	// Prefer the Blueprint subclass if the project has one: that is where the
	// imported mesh paths live (BodyMeshAsset etc). Spawning the bare C++ class
	// leaves those defaults empty and the vehicle renders as nothing.
	// Scripts/import_assets.py writes DefaultPawnClass into DefaultGame.ini, so
	// by the time we get here the class CDO may already name it; this is the
	// runtime fallback for the case where it does not.
	if (DefaultPawnClass == AOffroadVehiclePawn::StaticClass())
	{
		static const TCHAR* BpPawnPath = TEXT("/Game/Mudtrack/BP_Offroad4x4.BP_Offroad4x4_C");
		if (UClass* BpPawn = StaticLoadClass(AOffroadVehiclePawn::StaticClass(), nullptr, BpPawnPath))
		{
			DefaultPawnClass = BpPawn;
			UE_LOG(LogMudtrackSetup, Log, TEXT("Using Blueprint pawn: %s"), BpPawnPath);
		}
		else
		{
			UE_LOG(LogMudtrackSetup, Warning,
				TEXT("Blueprint pawn not found (%s); using the C++ class, which has no ")
				TEXT("mesh defaults - the vehicle will be invisible."), BpPawnPath);
		}
	}

	EnsureWorldSetup();

	// Running the protocol tests in a real game session is the only way to get
	// physics that actually integrates: a commandlet-created world does not
	// advance the Chaos scene.
	const FString CmdLine = FCommandLine::Get();
	if (CmdLine.Contains(TEXT("MudtrackRunTests")) || CmdLine.Contains(TEXT("-MudtrackAutoTest")))
	{
		UWorld* World = GetWorld();
		if (World != nullptr)
		{
			FActorSpawnParameters P;
			P.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
			ATestDriver* Driver = World->SpawnActor<ATestDriver>(
				ATestDriver::StaticClass(), FVector(0.f, 0.f, 5000.f), FRotator::ZeroRotator, P);
			UE_LOG(LogMudtrackSetup, Display, TEXT("Test driver spawned: %s"),
				Driver ? TEXT("ok") : TEXT("FAILED"));
		}
	}

	// -MudtrackShot=<seconds>: take one screenshot of the player's view after
	// that much game time and quit. A visual check that needs no window focus
	// and no packaging -- the picture is what the player sees.
	float ShotAt = 0.f;
	if (FParse::Value(*CmdLine, TEXT("MudtrackShot="), ShotAt) && ShotAt > 0.f)
	{
		FTimerHandle ShotTimer;
		GetWorldTimerManager().SetTimer(ShotTimer, FTimerDelegate::CreateLambda([]()
		{
			const FString Path = FPaths::ConvertRelativePathToFull(
				FPaths::ProjectSavedDir() / TEXT("Shots/view.png"));
			FScreenshotRequest::RequestScreenshot(Path, /*bShowUI=*/true, /*bAddUniqueSuffix=*/false);
			UE_LOG(LogMudtrackSetup, Display, TEXT("Screenshot requested: %s"), *Path);
		}), ShotAt, false);

		FTimerHandle QuitTimer;
		GetWorldTimerManager().SetTimer(QuitTimer, FTimerDelegate::CreateLambda([]()
		{
			FPlatformMisc::RequestExit(false);
		}), ShotAt + 1.5f, false);
	}
}

void AMudtrackGameMode::EnsureWorldSetup()
{
	UWorld* World = GetWorld();
	if (World == nullptr) return;

	// ---- lighting: set up so that poor physics cannot hide behind darkness
	bool bHasSun = false, bHasSky = false, bHasFog = false;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (It->IsA<ADirectionalLight>()) bHasSun = true;
		if (It->IsA<ASkyLight>()) bHasSky = true;
		if (It->IsA<AExponentialHeightFog>()) bHasFog = true;
	}

	if (!bHasSun)
	{
		ADirectionalLight* Sun = World->SpawnActor<ADirectionalLight>(
			FVector(0.f, 0.f, 2000.f), FRotator(-48.f, 35.f, 0.f));
		if (Sun && Sun->GetLightComponent())
		{
			// Physical units, matching the cameras' fixed EV100 14 exposure and
			// the level built by Scripts/import_assets.py.
			UDirectionalLightComponent* L = Cast<UDirectionalLightComponent>(Sun->GetLightComponent());
			Sun->GetLightComponent()->SetMobility(EComponentMobility::Movable);
			Sun->GetLightComponent()->SetIntensity(75000.f);
			Sun->GetLightComponent()->SetLightColor(FLinearColor(1.0f, 0.95f, 0.86f));
			Sun->GetLightComponent()->SetCastShadows(true);
			if (L != nullptr)
			{
				L->SetAtmosphereSunLight(true);
			}
		}
	}
	if (!bHasSky)
	{
		ASkyLight* Sky = World->SpawnActor<ASkyLight>(FVector(0.f, 0.f, 1200.f), FRotator::ZeroRotator);
		if (Sky && Sky->GetLightComponent())
		{
			Sky->GetLightComponent()->SetIntensity(1.0f);
			Sky->GetLightComponent()->SetMobility(EComponentMobility::Movable);
			Sky->GetLightComponent()->bRealTimeCapture = true;
		}
	}
	if (!bHasFog)
	{
		AExponentialHeightFog* Fog = World->SpawnActor<AExponentialHeightFog>(
			FVector(0.f, 0.f, 0.f), FRotator::ZeroRotator);
		if (Fog && Fog->GetComponent())
		{
			// Light haze only, deliberately not enough to hide the terrain.
			Fog->GetComponent()->SetFogDensity(0.012f);
			Fog->GetComponent()->SetFogHeightFalloff(0.12f);
		}
	}

	// ---- soil field
	Soil = nullptr;
	for (TActorIterator<ASoilField> It(World); It; ++It) { Soil = *It; break; }
	if (Soil != nullptr)
	{
		// The level already has one; just make sure it is sized and generated,
		// since BeginPlay order between actors is not guaranteed.
		Soil->EnsureInitialised();
	}
	else
	{
		Soil = World->SpawnActor<ASoilField>(FVector::ZeroVector, FRotator::ZeroRotator);
		if (Soil)
		{
			Soil->FieldHalfExtent = MapHalfExtent;
			Soil->CellSize = 25.f;
			Soil->TileCells = 32;
			Soil->GenerateTerrain(WorldSeed);
			Soil->MarkAllDirty();
			Soil->EnsureInitialised();
		}
	}

	UE_LOG(LogMudtrackSetup, Log, TEXT("World setup complete. Soil=%s"),
		Soil ? TEXT("present") : TEXT("MISSING"));
}
