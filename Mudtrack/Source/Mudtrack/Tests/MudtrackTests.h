// MudtrackTests.h
// Automated protocol tests for the vehicle/soil system.
//
// DESIGN: FRAME-STEPPED, NOT BLOCKING
// -----------------------------------
// These tests must run inside a real game session, because that is the only way
// the Chaos physics scene actually advances. That means the suite cannot block:
// it cannot loop calling GEngine->Tick() (that re-enters the tick task manager
// and asserts), and it cannot sleep.
//
// Instead the suite is a state machine advanced one step per engine frame. Each
// test function is a single action that runs quickly; whenever it needs time to
// pass it calls SimSeconds() and returns. The driver calls Advance() every
// frame, and the suite resumes at the next step when that time has elapsed.
//
// This keeps the tests readable (they read like straight-line code with explicit
// waits) while letting the real engine loop do the physics.

#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "../Soil/SoilTypes.h"
#include "MudtrackTests.generated.h"

class AOffroadVehiclePawn;
class ASoilField;
class UWorld;

/** One test's recorded outcome. */
USTRUCT(BlueprintType)
struct FTestResult
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Test") FString Name;
	UPROPERTY(BlueprintReadOnly, Category = "Test") bool bRan = false;
	UPROPERTY(BlueprintReadOnly, Category = "Test") bool bPassed = false;
	UPROPERTY(BlueprintReadOnly, Category = "Test") FString Detail;
	UPROPERTY(BlueprintReadOnly, Category = "Test") TArray<FString> Measurements;
};

/** Why a test paused, so the driver knows whether to just come back later. */
enum class ETestStep : uint8
{
	Ready,      // paused, waiting for time to elapse
	Finished    // the whole plan is complete
};

/**
 * Shared context passed through the step machine. Holds the vehicle, the soil
 * field and the accumulated frame time. One instance lives for a whole run.
 */
class MUDTRACK_API FMudtrackTestContext
{
public:
	AOffroadVehiclePawn* Vehicle = nullptr;
	ASoilField* Soil = nullptr;
	UWorld* World = nullptr;

	/** Seconds of game time elapsed since the run began. */
	float Elapsed = 0.f;

	/** Absolute deadline the current step is waiting for. */
	float WaitUntil = 0.f;

	/** Pause until (Elapsed + Seconds). Returns true if we must yield now. */
	bool WaitFor(float Seconds);

	/** True while we are before the current deadline. */
	bool IsWaiting() const { return Elapsed < WaitUntil; }

	/** Current test being built. */
	FTestResult Current;

	void BeginTest(const FString& Name);
	void FinishTest(bool bPassed, const FString& Detail);
	void Measure(const FString& Text);

	// ---- convenience helpers used by the tests
	float MeanRut() const;
	float MaxRut() const;
	float MeanSink() const;
	int32 GroundedCount() const;
	float TotalNormalLoad() const;
	float MeanSuspensionLength() const;

	void Teleport(const FVector& Loc, float YawDeg);
	void SetInputs(float Throttle, float Brake, float Steer, bool bHandbrake);
	bool FindSoilOfClass(ESoilClass Want, const FVector& Near, FVector& OutLoc) const;

	/**
	 * A start point where the vehicle's right-hand wheels run in Want and its
	 * left-hand wheels do not, for a 6 m run along +X -- split traction, the one
	 * case where locking a differential changes the outcome.
	 */
	bool FindSplitTraction(ESoilClass Want, const FVector& Near, FVector& OutLoc) const;
	AActor* FindFirstActorWithTag(const FName& Tag, const FVector& NearestTo) const;
};

UCLASS()
class MUDTRACK_API UMudtrackTests : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Start a protocol test run. Does not block: call Advance() each frame.
	 * Results are written to OutputJsonAbsolutePath when the plan completes.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mudtrack|Tests", meta = (WorldContext = "WorldContextObject"))
	static bool BeginRun(UObject* WorldContextObject, const FString& OutputJsonAbsolutePath);

	/**
	 * Advance the plan by one engine frame.
	 * @return false once the whole plan has finished.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mudtrack|Tests")
	static bool Advance(float DeltaSeconds);

	UFUNCTION(BlueprintCallable, Category = "Mudtrack|Tests")
	static bool IsRunning();

	UFUNCTION(BlueprintCallable, Category = "Mudtrack|Tests")
	static FString GetLastSummary();

	UFUNCTION(BlueprintCallable, Category = "Mudtrack|Tests")
	static TArray<FTestResult> GetLastResults();

	/** Human-readable progress, for logging. */
	UFUNCTION(BlueprintCallable, Category = "Mudtrack|Tests")
	static FString GetProgressText();

private:
	static FMudtrackTestContext Run;
	static TArray<FTestResult> LastResults;
	static FString OutputPath;
	static int32 PlanIndex;
	static bool bPlanComplete;
	static bool bInitialised;

	/**
	 * Phase counter for the test currently running. Public because the test
	 * bodies are free functions in an anonymous namespace and need to drive it.
	 */
public:
	static int32 TestPhase;
private:

	static void Finalise();
};

