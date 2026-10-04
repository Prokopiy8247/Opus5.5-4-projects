// MudtrackGameMode.h
// Wires the default pawn, HUD and the world setup for the forest map.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "MudtrackGameMode.generated.h"

class ASoilField;

UCLASS()
class MUDTRACK_API AMudtrackGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	AMudtrackGameMode();

	virtual void BeginPlay() override;

	/** Build the soil field and the surrounding scenery if the level has none. */
	UFUNCTION(BlueprintCallable, Category = "Setup")
	void EnsureWorldSetup();

	/** Deterministic seed for terrain and scatter, so tests are reproducible. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Setup")
	int32 WorldSeed = 20261002;

	/** Map half-extent in cm; 30000 = 600 m across. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Setup")
	float MapHalfExtent = 30000.f;

protected:
	UPROPERTY()
	TObjectPtr<ASoilField> Soil;
};
