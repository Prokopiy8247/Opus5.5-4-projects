// TestDriver.h
// Runs the protocol test suite inside a REAL game session and exits.
//
// WHY THIS EXISTS
// ---------------
// A commandlet-created world does not advance the Chaos scene: UWorld::Tick
// alone never integrates the rigid bodies, so every measurement comes back at
// its spawn value. Rather than hand-drive FPhysScene (whose EndFrame asserts
// when Chaos's async solve has not completed), we run a genuine game session
// headlessly and let the engine's own loop do the ticking. This actor waits for
// the world to settle, runs the suite, writes the results, then asks the engine
// to exit with a status that reflects the outcome.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "TestDriver.generated.h"

UCLASS()
class MUDTRACK_API ATestDriver : public AActor
{
	GENERATED_BODY()

public:
	ATestDriver();

	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;

	/** Seconds of game time to let the vehicle settle before testing. */
	UPROPERTY(EditAnywhere, Category = "Test")
	float WarmupSeconds = 4.0f;

	/** Leave the game running afterwards instead of exiting. */
	UPROPERTY(EditAnywhere, Category = "Test")
	bool bExitWhenFinished = true;

	/** Where the JSON result is written. */
	UPROPERTY(EditAnywhere, Category = "Test")
	FString OutputRelativePath = TEXT("MudtrackTests/results.json");

protected:
	/** Make sure the world has the actors the tests need. */
	void EnsureScenario();

	float Elapsed = 0.f;
	bool bStarted = false;
	bool Finished = false;
	int32 Passed = 0;
	int32 Total = 0;
};
